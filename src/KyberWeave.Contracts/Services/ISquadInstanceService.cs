using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Contracts.Services;

/// <summary>Manages side-by-side harness instances, each with its own model preset.</summary>
public interface ISquadInstanceService
{
    /// <summary>Installs the instance for a target and preset when needed and synchronizes its links.</summary>
    /// <param name="target">The harness target.</param>
    /// <param name="presetName">The model preset name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The instance root and any synchronization warnings.</returns>
    Task<SquadInstanceEnsureResult> EnsureAsync(SquadTarget target, string presetName, CancellationToken cancellationToken = default);

    /// <summary>Removes the instance for a target and preset.</summary>
    /// <param name="target">The harness target.</param>
    /// <param name="presetName">The model preset name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the instance is gone.</returns>
    Task RemoveAsync(SquadTarget target, string presetName, CancellationToken cancellationToken = default);

    /// <summary>Re-synchronizes the links of every installed instance.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when every instance is refreshed.</returns>
    Task RefreshAllAsync(CancellationToken cancellationToken = default);
}
