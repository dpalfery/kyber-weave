using System.ComponentModel;
using KyberWeave.Core.Arbiter.Credentials;

namespace KyberWeave.Tests.Credentials;

/// <summary>
/// Decides whether this host offers a usable credential store, so the integration contract run can skip where it
/// does not.
/// </summary>
/// <remarks>
/// The probe only reads an origin nothing was written to. A store that cannot be reached raises one of the typed
/// unavailable failures, and only those skip. Any other failure is a defect and is raised, so it fails the run
/// instead of hiding behind a skip. The probe never writes: an adapter that writes but reads back wrong would
/// otherwise look unavailable, and its contract tests would skip while CI stayed green.
/// </remarks>
public static class CredentialStoreAvailability
{
    /// <summary>Asks the store for an entry that was never written.</summary>
    /// <param name="store">The store to probe.</param>
    /// <param name="origin">An origin no test writes to.</param>
    /// <returns>Null when the store can be reached; otherwise why this host cannot use it.</returns>
    /// <exception cref="InvalidOperationException">The store failed for a reason that does not mean it is unavailable.</exception>
    public static async Task<string?> ProbeAsync(ICredentialStore store, string origin)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        try
        {
            _ = await store.ReadAsync(origin).ConfigureAwait(false);
            return null;
        }
        catch (KeychainUnavailableException exception)
        {
            return $"The macOS keychain is unavailable here: {exception.Message}";
        }
        catch (SecretServiceUnavailableException exception)
        {
            return $"The Secret Service is unavailable here: {exception.Message}";
        }
        catch (PlatformNotSupportedException exception)
        {
            return $"This platform has no credential store: {exception.Message}";
        }
        catch (Win32Exception exception)
        {
            return $"The credential-store tool cannot be started here: {exception.Message}";
        }
    }
}
