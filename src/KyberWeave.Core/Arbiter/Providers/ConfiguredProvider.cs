using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Core.Arbiter.Providers;

/// <summary>Builds the decision model a process outside the Arbiter hook may ask, from a whole host configuration.</summary>
/// <remarks>
/// A forwarding shim over <c>ConfiguredArbiterProviderFactory</c>: its caller holds a <see cref="KyberWeaveConfig"/>,
/// which has not left the legacy engine yet. It goes when that caller resolves the factory from the container.
/// </remarks>
public static class ConfiguredProvider
{
    /// <summary>The provider to ask, or null when none is configured or reachable without a call.</summary>
    /// <param name="config">The repository configuration.</param>
    /// <param name="homeDirectory">The user's home directory, where the override lives.</param>
    /// <param name="handler">The HTTP handler; null for a new <see cref="HttpClientHandler"/>.</param>
    /// <param name="storeFactory">The credential store; null for the operating system's.</param>
    public static IArbiterProvider? TryCreate(
        KyberWeaveConfig config,
        string homeDirectory,
        HttpMessageHandler? handler = null,
        Func<ICredentialStore>? storeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new ConfiguredArbiterProviderFactory(new FileArbiterUserSettings(new PhysicalFileSystem()))
            .TryCreate(config.Arbiter.Provider, homeDirectory, handler, storeFactory);
    }
}
