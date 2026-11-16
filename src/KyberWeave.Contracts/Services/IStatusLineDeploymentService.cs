using KyberWeave.Domain.StatusLine;

namespace KyberWeave.Contracts.Services;

/// <summary>Plans and applies the deployment of the Kyber Utilities status-line artifacts for one harness.</summary>
public interface IStatusLineDeploymentService
{
    /// <summary>Preflights a deployment without changing the filesystem.</summary>
    /// <param name="target">The harness to deploy the artifacts for.</param>
    /// <param name="roots">The resolved per-user roots the deployment is confined to.</param>
    /// <param name="files">
    /// The complete set of artifacts to own for this harness. A file already recorded in the receipt but absent from
    /// this set stays owned and is left untouched: a deploy never silently disowns a file it wrote earlier.
    /// </param>
    /// <returns>The plan.</returns>
    /// <exception cref="StatusLineDeploymentConflictException">
    /// A planned path is occupied by an unmanaged file or directory, is a harness settings path, is inside an auto-load
    /// directory, or appears twice in <paramref name="files"/>.
    /// </exception>
    StatusLineDeploymentPlan CreateDeploy(
        StatusLineTarget target,
        StatusLineTargetRoots roots,
        IReadOnlyList<StatusLineDeploymentFile> files);

    /// <summary>Preflights an ownership-aware removal without changing the filesystem.</summary>
    /// <param name="target">The harness to remove the artifacts of.</param>
    /// <param name="roots">The resolved per-user roots.</param>
    /// <returns>The plan: it deletes only files the receipt owns and whose bytes are still the ones the deploy wrote.</returns>
    /// <exception cref="StatusLineDeploymentConflictException">The receipt records a path that does not resolve beneath the staging root.</exception>
    StatusLineDeploymentPlan CreateRemove(StatusLineTarget target, StatusLineTargetRoots roots);

    /// <summary>Reads the persisted receipt for a harness.</summary>
    /// <param name="target">The harness.</param>
    /// <param name="roots">The resolved per-user roots.</param>
    /// <returns>The receipt, or null when none exists.</returns>
    StatusLineReceipt? ReadReceipt(StatusLineTarget target, StatusLineTargetRoots roots);

    /// <summary>Applies the plan, writing or deleting exactly the paths it planned.</summary>
    /// <param name="plan">A plan this service built.</param>
    /// <remarks>
    /// The receipt is settled last, so a failure part-way through leaves the previous ownership record intact rather
    /// than claiming files that were never written. An empty resulting receipt deletes the receipt file.
    /// </remarks>
    void Apply(StatusLineDeploymentPlan plan);
}
