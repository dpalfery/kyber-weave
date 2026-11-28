using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Data.Arbiter;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>Builds the decision-model picker from the repository's configuration and the user's own provider settings.</summary>
/// <param name="providers">Builds the decision model from the repository's provider and the user's override.</param>
/// <param name="environment">Where the user's home directory comes from.</param>
/// <param name="resources">The embedded shipped content, which holds the difficulty rubric.</param>
public sealed class ConfiguredPlanTaskAgentPickerFactory(
    ConfiguredArbiterProviderFactory providers,
    IProcessEnvironment environment,
    IEmbeddedResources resources) : IPlanTaskAgentPickerFactory
{
    /// <inheritdoc />
    public async Task<IPlanTaskAgentPicker?> ForConfigAsync(
        KyberWeaveConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        string home = environment.HomeDirectory;
        if (string.IsNullOrWhiteSpace(home))
            return null;
        IArbiterProvider? provider = await providers.TryCreateAsync(config.Arbiter.Provider, home, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return provider is null
            ? null
            : new PlanTaskAgentPick(provider, PlanTaskAgentPick.ImplementationAgents(config.WorkerRoster), resources);
    }
}
