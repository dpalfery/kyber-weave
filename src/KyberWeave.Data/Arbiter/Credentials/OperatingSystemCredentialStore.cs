using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Credentials;

namespace KyberWeave.Data.Arbiter.Credentials;

/// <summary>Picks the credential store the running operating system offers.</summary>
public static class OperatingSystemCredentialStore
{
    /// <summary>Creates the Windows Credential Manager, macOS Keychain or Secret Service store.</summary>
    /// <param name="environment">The platform fact that decides the store.</param>
    /// <returns>The store for this operating system.</returns>
    /// <remarks>
    /// The platform comes from the environment seam rather than <c>OperatingSystem.Is*</c>, so a unit test can
    /// ask for every platform without running on it. A family with no native store of its own, including an
    /// unidentified one, gets the Secret Service store.
    /// </remarks>
    public static ICredentialStore Create(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.OperatingSystem switch
        {
            OperatingSystemKind.Windows => new WindowsCredentialStore(),
            OperatingSystemKind.MacOS => new MacKeychainCredentialStore(new ProcessRunnerCredentialProcessRunner()),
            _ => new SecretServiceCredentialStore(new ProcessRunnerCredentialProcessRunner()),
        };
    }
}
