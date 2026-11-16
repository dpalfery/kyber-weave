using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Supplies the step-1 provider the host configuration names, so served tools that ask a model (section selection,
/// comment triage) answer through the same provider the hook uses.
/// </summary>
public interface IArbiterProviderSource
{
    /// <summary>Builds the provider for <paramref name="config"/>, with the user override applied.</summary>
    /// <param name="config">The loaded host configuration.</param>
    /// <returns>The provider; <c>NoneProvider</c> when the configuration names no provider.</returns>
    IArbiterProvider ProviderFor(KyberWeaveConfig config);
}
