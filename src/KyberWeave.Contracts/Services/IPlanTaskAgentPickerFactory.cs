using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;

namespace KyberWeave.Contracts.Services;

/// <summary>Builds the decision-model picker that chooses each unassigned task's agent as a task file is written.</summary>
public interface IPlanTaskAgentPickerFactory
{
    /// <summary>The picker for a repository's configuration, or null when no decision model is configured or its key is missing. The caller disposes it.</summary>
    /// <param name="config">The effective configuration.</param>
    IPlanTaskAgentPicker? ForConfig(KyberWeaveConfig config);

    /// <summary>
    /// The picker for a repository's configuration, built without blocking a thread: the user's key is resolved
    /// asynchronously. Null when no decision model is configured or its key is missing. The caller disposes it.
    /// </summary>
    /// <param name="config">The effective configuration.</param>
    /// <param name="cancellationToken">Cancels the key resolution.</param>
    /// <returns>The picker, or null when there is none to build.</returns>
    Task<IPlanTaskAgentPicker?> ForConfigAsync(
        KyberWeaveConfig config,
        CancellationToken cancellationToken = default);
}
