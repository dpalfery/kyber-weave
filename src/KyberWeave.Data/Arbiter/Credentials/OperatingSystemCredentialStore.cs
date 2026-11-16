using KyberWeave.Core.Arbiter.Credentials;

namespace KyberWeave.Data.Arbiter.Credentials;

/// <summary>Picks the credential store the running operating system offers.</summary>
public static class OperatingSystemCredentialStore
{
    /// <summary>Creates the Windows Credential Manager, macOS Keychain or Secret Service store.</summary>
    /// <returns>The store for this operating system.</returns>
    public static ICredentialStore Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsCredentialStore();
        if (OperatingSystem.IsMacOS())
            return new MacKeychainCredentialStore(new ProcessRunnerCredentialProcessRunner());
        return new SecretServiceCredentialStore(new ProcessRunnerCredentialProcessRunner());
    }
}
