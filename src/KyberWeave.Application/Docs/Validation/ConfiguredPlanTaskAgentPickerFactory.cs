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
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The provider's lifetime transfers to the returned picker, which holds it for the task-file run.")]
    public IPlanTaskAgentPicker? ForConfig(KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string home = environment.HomeDirectory;
        if (string.IsNullOrWhiteSpace(home))
            return null;
        IArbiterProvider? provider = providers.TryCreate(config.Arbiter.Provider, home);
        return provider is null
            ? null
            : new PlanTaskAgentPick(provider, PlanTaskAgentPick.ImplementationAgents(config.WorkerRoster), resources);
    }
}
