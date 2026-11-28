namespace KyberWeave.Contracts.Services;

/// <summary>How integrating one task's work ended; each value maps to one exit code the caller branches on.</summary>
public enum RunIntegrateStatus
{
    /// <summary>The task's commits are on the run branch.</summary>
    Integrated,

    /// <summary>
    /// The task's commits are on the run branch, but its scratchpad could not be copied. The task worktree is left
    /// in place, because removing it would lose the evidence the copy was meant to keep.
    /// </summary>
    IntegratedWithoutEvidence,

    /// <summary>The run branch already had every commit the task branch had.</summary>
    NothingToIntegrate,

    /// <summary>The caller supplied input, or the checkouts are in a state the step cannot act on.</summary>
    RefusedInput,

    /// <summary>A commit could not be applied; the cherry-pick was aborted and the run branch is unchanged.</summary>
    Conflict,

    /// <summary>The lock could not be taken, or git failed in a way that is not a conflict.</summary>
    Failed
}

/// <summary>Everything one task integration needs from the command line, the environment and the run records.</summary>
/// <param name="TaskId">The task whose scratchpad folder is preserved; it names <c>.agent_scratchpad/&lt;id&gt;/</c>.</param>
/// <param name="TaskWorktree">The task's worktree as given, or null for the current directory.</param>
/// <param name="RunWorktree">The run's worktree as given, or null to read the live run record for this repository.</param>
/// <param name="WorkingDirectory">The directory relative paths are read against.</param>
/// <param name="HomeDirectory">The operator's home, which holds the run records.</param>
public sealed record RunIntegrateRequest(
    string? TaskId,
    string? TaskWorktree,
    string? RunWorktree,
    string WorkingDirectory,
    string HomeDirectory);

/// <summary>How one task integration ended.</summary>
/// <param name="Status">The outcome, which fixes the exit code.</param>
/// <param name="RunHead">The run branch's head, or null when it could not be read.</param>
/// <param name="Message">Why the step refused or failed, or null on success.</param>
/// <param name="Files">The paths a conflict stopped at, which the operator reads on one <c>CONFLICT:</c> line.</param>
/// <param name="Notes">One-off lines to print before the outcome, such as a stale lock taken over.</param>
public sealed record RunIntegrateOutcome(
    RunIntegrateStatus Status,
    string? RunHead = null,
    string? Message = null,
    IReadOnlyList<string>? Files = null,
    IReadOnlyList<string>? Notes = null);

/// <summary>Brings a developer task's commit from its own worktree into the run's branch, then removes the task worktree.</summary>
public interface IRunIntegrateService
{
    /// <summary>Integrates one task, or refuses and changes nothing.</summary>
    /// <param name="request">The two worktrees, the task id and the operator's home.</param>
    /// <param name="cancellationToken">Cancels the wait for a lock and the git children the integration runs.</param>
    /// <returns>The outcome, which fixes the exit code.</returns>
    Task<RunIntegrateOutcome> IntegrateAsync(RunIntegrateRequest request, CancellationToken cancellationToken = default);
}
