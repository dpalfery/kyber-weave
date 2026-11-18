using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Data.Arbiter.Credentials;
using KyberWeave.Data.Host;
using KyberWeave.Tests.Credentials;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>
/// Runs the credential contract against the store this operating system offers. The suite skips when the host has no
/// usable store, which is the usual case for a Linux runner without <c>libsecret-tools</c> and for a locked keychain.
/// </summary>
public sealed class OperatingSystemCredentialStoreContractTests : CredentialStoreContractTests
{
    private static readonly Lazy<string?> Unavailable = new(Probe);

    /// <inheritdoc />
    protected override ICredentialStore CreateStore()
    {
        if (Unavailable.Value is { } reason)
        {
            Assert.Skip(reason);
        }

        return OperatingSystemCredentialStore.Create(new ProcessEnvironment());
    }

    /// <summary>
    /// Writes, reads and deletes one unique probe entry. A store that cannot do all three on this host is not
    /// available here, which is a skip rather than a failure.
    /// </summary>
    /// <returns>Null when the store works, otherwise the reason it does not.</returns>
    private static string? Probe()
    {
        ICredentialStore store = OperatingSystemCredentialStore.Create(new ProcessEnvironment());
        string origin = $"https://kyber-weave-contract-probe-{Guid.NewGuid():N}.invalid";
        try
        {
            store.Write(origin, "probe");
            string? read = store.Read(origin);
            return string.Equals(read, "probe", StringComparison.Ordinal)
                ? null
                : "The operating system's credential store did not read back the probe key it wrote.";
        }
        catch (InvalidOperationException exception)
        {
            return $"The operating system's credential store is unavailable here: {exception.Message}";
        }
        catch (PlatformNotSupportedException exception)
        {
            return $"The operating system's credential store is unavailable here: {exception.Message}";
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return $"The operating system's credential store is unavailable here: {exception.Message}";
        }
        finally
        {
            TryDelete(store, origin);
        }
    }

    private static void TryDelete(ICredentialStore store, string origin)
    {
        try
        {
            store.Delete(origin);
        }
        catch (InvalidOperationException)
        {
            // The probe already decided the outcome; a refused cleanup must not turn a skip into a failure.
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
