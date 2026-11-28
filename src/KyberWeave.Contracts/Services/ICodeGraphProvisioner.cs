using KyberWeave.Domain.Doctor;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Contracts.Services;

/// <summary>Checks and, when the mode allows, prepares the repository's CodeGraph index.</summary>
public interface ICodeGraphProvisioner
{
    /// <summary>Runs the CodeGraph checks, preparing the index in every mode except <see cref="DoctorMode.Check"/>.</summary>
    /// <param name="root">The resolved repository root.</param>
    /// <param name="mode">The run's mode.</param>
    /// <param name="options">The budgets and the CodeGraph executable name.</param>
    /// <param name="executor">Runs the CodeGraph child, bounded by the index timeout.</param>
    /// <param name="lockDirectory">Where the lock file lives.</param>
    /// <param name="restore">Which tracked files an ensure run restores after writing them.</param>
    /// <returns>The checks and whether the binary is missing.</returns>
    CodeGraphOutcome Run(
        string root,
        DoctorMode mode,
        DoctorOptions options,
        IProcessExecutor executor,
        string lockDirectory,
        CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles);

    /// <summary>Runs the CodeGraph checks without blocking a thread, preparing the index in every mode except <see cref="DoctorMode.Check"/>.</summary>
    /// <param name="root">The resolved repository root.</param>
    /// <param name="mode">The run's mode.</param>
    /// <param name="options">The budgets and the CodeGraph executable name.</param>
    /// <param name="executor">Runs the CodeGraph child, bounded by the index timeout.</param>
    /// <param name="lockDirectory">Where the lock file lives.</param>
    /// <param name="restore">Which tracked files an ensure run restores after writing them.</param>
    /// <param name="cancellationToken">Cancels the index and git children.</param>
    /// <returns>The checks and whether the binary is missing.</returns>
    Task<CodeGraphOutcome> RunAsync(
        string root,
        DoctorMode mode,
        DoctorOptions options,
        IProcessRunner executor,
        string lockDirectory,
        CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings the index up to date under the cross-process lock without blocking a thread: <c>codegraph init</c> when
    /// <c>.codegraph/</c> is absent and <c>codegraph sync</c> otherwise.
    /// </summary>
    /// <param name="root">The repository root, which is also the child's working directory.</param>
    /// <param name="options">The CodeGraph executable and index budget.</param>
    /// <param name="executor">Runs the CodeGraph child.</param>
    /// <param name="lockDirectory">Where the lock file lives.</param>
    /// <param name="lockWait">How long to wait for a lock another run holds. Zero tries once and reports the index busy.</param>
    /// <param name="cancellationToken">Cancels the lock wait and the child.</param>
    /// <returns>The <c>codegraph.index</c> check.</returns>
    Task<DoctorCheck> ProvisionAsync(
        string root,
        DoctorOptions options,
        IProcessRunner executor,
        string lockDirectory,
        TimeSpan lockWait,
        CancellationToken cancellationToken = default);
}
