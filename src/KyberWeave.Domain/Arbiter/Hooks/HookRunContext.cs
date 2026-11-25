using KyberWeave.Core.RunStart;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>Which candidate chose the worktree a hook reads its plan, task file and git facts from.</summary>
public enum HookRunContextSource
{
    /// <summary>No candidate could be validated, so the process directory stands and the rules decide on the absence.</summary>
    None,

    /// <summary>
    /// The worktree the event itself named: a dispatch plan, packet or agent working directory, or the absolute target
    /// of a shell command's leading <c>cd</c>.
    /// </summary>
    EventWorktree,

    /// <summary>The live run record <c>kyber-weave run start</c> wrote for this repository.</summary>
    ActiveRun,

    /// <summary>The payload's <c>cwd</c>, validated as a worktree of this repository.</summary>
    PayloadCwd,

    /// <summary>The process directory, which is the last resort for a payload that names no directory.</summary>
    ProcessDirectory,

    /// <summary>
    /// Several runs are live and no signal chose one of them: no run is active, paths are read from the canonical
    /// checkout, and a note names every live run. No guard is relaxed by it.
    /// </summary>
    Ambiguous,
}

/// <summary>What one hook invocation asks the run-context resolver.</summary>
/// <param name="Harness">The harness token the event arrived from.</param>
/// <param name="WorkspaceRoot">The workspace root the adapter read from the payload, when it names one.</param>
/// <param name="EventCwd">The payload's <c>cwd</c>, when it carries one.</param>
/// <param name="ProcessDirectory">The hook process's directory, the last resort.</param>
/// <param name="AgentWorktree">
/// A worktree the event names as the agent's working directory, when the harness reports one.
/// </param>
/// <param name="Prompt">The dispatch text, so the plan header and a named worktree can be read from it.</param>
/// <param name="HomeDirectory">The operator's home, which holds the run records.</param>
/// <param name="LeadingCdTarget">
/// The absolute directory a shell command's leading <c>cd &lt;path&gt; &amp;&amp;</c> names, when the call is a shell
/// command that starts with one. It counts only when the cwd is absent or is the canonical checkout.
/// </param>
public sealed record HookRunContextRequest(
    string Harness,
    string? WorkspaceRoot,
    string? EventCwd,
    string? ProcessDirectory,
    string? AgentWorktree,
    string? Prompt,
    string HomeDirectory,
    string? LeadingCdTarget = null);

/// <summary>Everything one hook invocation reads paths from: the repository, the run, and the worktree holding the plan.</summary>
/// <param name="RepositoryRoot">The directory configuration, the claim store and the ledger are read under.</param>
/// <param name="CommonDirectory">Git's common directory for the repository, or null when git named none.</param>
/// <param name="ActiveRun">The live run record for this repository, or null when none is live.</param>
/// <param name="Worktree">The working tree the plan, its task file and the git facts are read from.</param>
/// <param name="Worktrees">Every worktree of this repository that was validated as belonging to it.</param>
/// <param name="PlanFile">The dispatch's plan, repository-relative, when it names one.</param>
/// <param name="PlanPath">The plan's full path inside <paramref name="Worktree"/>, when it resolves and exists.</param>
/// <param name="TaskFile">The task file's full path beside the plan, whether or not it exists.</param>
/// <param name="TaskFileExists">Whether the task file was found on disk.</param>
/// <param name="Source">Which candidate chose <paramref name="Worktree"/>.</param>
/// <param name="Notes">The precise reason for each candidate refused and each path tried.</param>
public sealed record HookRunContext(
    string RepositoryRoot,
    string? CommonDirectory,
    RunRecord? ActiveRun,
    string Worktree,
    IReadOnlyList<string> Worktrees,
    string? PlanFile,
    string? PlanPath,
    string? TaskFile,
    bool TaskFileExists,
    HookRunContextSource Source,
    IReadOnlyList<string> Notes)
{
    /// <summary>Whether the dispatch's plan was read from disk in this context.</summary>
    public bool PlanExists => PlanPath is not null;

    /// <summary>Whether the task file the prompt guard needs was found.</summary>
    public bool HasTaskFile => PlanExists && TaskFileExists;
}
