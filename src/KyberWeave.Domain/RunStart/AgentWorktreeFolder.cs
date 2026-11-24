namespace KyberWeave.Core.RunStart;

/// <summary>
///     The folder, inside the repository, that holds the worktrees <c>run start</c> creates for its runs.
/// </summary>
/// <remarks>
///     One constant names the folder so that run start and every reader of it agree on the spelling. The
///     folder sits inside the checkout rather than beside it, so a run started from a linked worktree keeps its
///     worktree inside that session. Git would otherwise list the folder as untracked in the checkout, which
///     later reads as a dirty tree, so run start also writes <see cref="ExcludeLine" /> to the local exclude file.
/// </remarks>
public static class AgentWorktreeFolder
{
    /// <summary>The folder name, relative to the repository root.</summary>
    public const string Name = ".agent_worktrees";

    /// <summary>The line that excludes the folder from git's status, in the form git's exclude file expects.</summary>
    public const string ExcludeLine = ".agent_worktrees/";
}
