using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Contracts.Services;

/// <summary>Installs, updates and removes the Kyber-Squad deployment in a target root.</summary>
public interface ISquadLifecycleService
{
    /// <summary>Gets the targets an install of <paramref name="request"/> would render for.</summary>
    /// <param name="request">The install request.</param>
    /// <returns>The effective targets, including any the existing global receipt already holds.</returns>
    IReadOnlyList<SquadTarget> EffectiveInstallTargets(SquadInstallRequest request);

    /// <summary>Installs Kyber-Squad into the target root.</summary>
    /// <param name="request">What to install and where.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome, with the plan and any warnings.</returns>
    Task<SquadLifecycleResult> InstallAsync(SquadInstallRequest request, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing deployment in the target root.</summary>
    /// <param name="request">What to update and where.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome, with the plan and any warnings.</returns>
    Task<SquadLifecycleResult> UpdateAsync(SquadUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes Kyber-Squad from the target root.</summary>
    /// <param name="request">What to remove and where.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome, with the plan and any warnings.</returns>
    Task<SquadLifecycleResult> UninstallAsync(SquadUninstallRequest request, CancellationToken cancellationToken = default);
}
