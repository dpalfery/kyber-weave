using KyberWeave.Domain.Doctor;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Contracts.Services;

/// <summary>The repository doctor: decides whether a repository's documentation tooling works.</summary>
/// <remarks>
/// The <c>doctor</c> verb, <c>docs init --provision</c> and the squad skill all call this, so the three cannot diverge.
/// Exit codes: 0 all checks pass; 1 a check failed; 2 a client-input refusal; 3 the CodeGraph binary is missing and
/// installing it needs the user's approval.
/// </remarks>
public interface IRepositoryDoctor
{
    /// <summary>Runs the doctor against a directory.</summary>
    /// <param name="root">The directory to check. The git toplevel of this directory becomes the repository root.</param>
    /// <param name="mode">Read-only, provisioning, or the ensure run that leaves the index and both tools proved.</param>
    /// <param name="options">The budgets and executable names.</param>
    /// <param name="executor">Runs git and CodeGraph children, bounded by <see cref="DoctorOptions.IndexTimeout"/>.</param>
    /// <param name="environment">Reads one environment variable. Injected so the home-directory refusal is testable.</param>
    /// <param name="lockDirectory">Where the index lock file lives. Null uses the OS temp directory.</param>
    /// <returns>The report.</returns>
    DoctorReport Run(
        string root,
        DoctorMode mode,
        DoctorOptions options,
        IProcessExecutor executor,
        Func<string, string?> environment,
        string? lockDirectory = null);

    /// <summary>
    /// Runs the doctor for the git post-checkout hook: indexes a new worktree without restoring the tracked edits the
    /// index run would otherwise revert.
    /// </summary>
    /// <param name="root">The worktree root the hook was fired in.</param>
    /// <param name="options">The index budget and executable names.</param>
    /// <param name="executor">Runs git and CodeGraph children.</param>
    /// <param name="lockDirectory">Where the index lock file lives. Null uses the OS temp directory.</param>
    /// <returns>The report.</returns>
    DoctorReport RunPostCheckout(string root, DoctorOptions options, IProcessExecutor executor, string? lockDirectory = null);
}
