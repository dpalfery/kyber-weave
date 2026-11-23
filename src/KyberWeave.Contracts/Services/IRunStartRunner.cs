using KyberWeave.Core.RunStart;

namespace KyberWeave.Contracts.Services;

/// <summary>The git questions and actions <c>run start</c> needs, all run in one repository root.</summary>
public interface IRunStartRunner
{
    /// <summary>Gets the repository root every git command runs in.</summary>
    string RepositoryRoot { get; }

    /// <summary>Runs one git command and reports what it wrote.</summary>
    /// <param name="arguments">argv for git, one element per argument.</param>
    /// <returns>The exit code and both output streams.</returns>
    GitOutcome Git(params string[] arguments);

    /// <summary>The repository root git reports for the current directory.</summary>
    /// <returns>The root, or null outside a repository.</returns>
    string? DetectRepositoryRoot();

    /// <summary>The git status outcome for the current checkout.</summary>
    /// <returns>The outcome of <c>git status --porcelain</c>.</returns>
    GitOutcome CheckoutStatus();

    /// <summary>Whether the checkout has no uncommitted change.</summary>
    /// <returns>True or false, or null when the probe failed.</returns>
    bool? CheckoutIsClean();

    /// <summary>The git worktree list outcome for the current repository.</summary>
    /// <returns>The outcome of <c>git worktree list --porcelain</c>.</returns>
    GitOutcome WorktreeList();

    /// <summary>Whether a second worktree is attached to this repository.</summary>
    /// <returns>True or false, or null when the probe failed.</returns>
    bool? AnotherWorktreeIsAttached();

    /// <summary>What <c>origin/HEAD</c> points at.</summary>
    /// <returns>The symbolic target, or null when git cannot say or the ref was never set.</returns>
    string? OriginHeadSymbolicRef();

    /// <summary>The commit a revision resolves to.</summary>
    /// <param name="revision">The revision.</param>
    /// <returns>The commit, or null when it does not resolve.</returns>
    string? ResolveRevision(string revision);

    /// <summary>Whether a local branch of that name exists.</summary>
    /// <param name="branch">The branch.</param>
    /// <returns>True when it exists.</returns>
    bool HasLocalBranch(string branch);

    /// <summary>Whether the remote has a branch of that name.</summary>
    /// <param name="branch">The branch.</param>
    /// <returns>True when it exists.</returns>
    bool HasRemoteBranch(string branch);

    /// <summary>The nearest tag at a revision, else its abbreviated commit.</summary>
    /// <param name="revision">The revision to describe.</param>
    /// <returns>The description, or null when the revision cannot be described at all.</returns>
    string? Describe(string revision);

    /// <summary>How far two revisions have diverged.</summary>
    /// <param name="left">The revision counted first, normally <c>HEAD</c>.</param>
    /// <param name="right">The revision counted second, normally the base ref.</param>
    /// <returns>Commits only on <paramref name="left"/> and commits only on <paramref name="right"/>; both null when git cannot compare them.</returns>
    (int? Ahead, int? Behind) Divergence(string left, string right);

    /// <summary>The commits a tip has that a base does not, with their full messages, in one call.</summary>
    /// <param name="baseRef">The ref the resumed branch started from.</param>
    /// <param name="tip">The resumed branch or its remote-tracking ref.</param>
    /// <returns>The outcome of the log, oldest commit first, NUL-separated.</returns>
    GitOutcome CommitMessages(string baseRef, string tip);

    /// <summary>The address <c>origin</c> points at.</summary>
    /// <returns>The address, or null when there is no such remote.</returns>
    string? OriginAddress();

    /// <summary>Whether the CodeGraph binary resolves on <c>PATH</c>.</summary>
    /// <returns>True when it is installed.</returns>
    bool CodeGraphBinaryIsAvailable();

    /// <summary>Refreshes the remote-tracking refs, refusing to prompt.</summary>
    /// <returns>The outcome of the fetch.</returns>
    GitOutcome Fetch();

    /// <summary>Creates a branch in the checkout the run is in.</summary>
    /// <param name="branch">The branch to create.</param>
    /// <param name="startRef">The remote-tracking ref the branch starts at.</param>
    /// <returns>The outcome of the switch.</returns>
    GitOutcome CreateBranchInPlace(string branch, string startRef);

    /// <summary>Checks out an existing branch in the current checkout.</summary>
    /// <param name="branch">The branch to check out.</param>
    /// <returns>The outcome of the switch.</returns>
    GitOutcome SwitchToBranch(string branch);

    /// <summary>The worktree path a branch is created at: inside <c>.agent_worktrees</c> under the repository root, claimed so two runs in one second never share it.</summary>
    /// <param name="branch">The branch the worktree holds.</param>
    /// <param name="startedAt">When the run started; the folder name carries it in UTC.</param>
    /// <returns>The path, <c>&lt;root&gt;/.agent_worktrees/&lt;stamp&gt;-&lt;branch&gt;</c>. The caller releases the claim with <see cref="ReleaseWorktreeClaim"/> once git has run.</returns>
    string WorktreePathFor(string branch, DateTimeOffset startedAt);

    /// <summary>Excludes the worktree folder from git's status in the local exclude file, before any worktree is created in it.</summary>
    /// <returns>The outcome: succeeded when the exclusion is in place; failed with git's or the file system's reason otherwise.</returns>
    GitOutcome EnsureWorktreeFolderExcluded();

    /// <summary>Removes the claim <see cref="WorktreePathFor"/> took.</summary>
    /// <param name="worktreePath">The path <see cref="WorktreePathFor"/> returned.</param>
    void ReleaseWorktreeClaim(string worktreePath);

    /// <summary>Creates a branch in a new worktree.</summary>
    /// <param name="branch">The branch to create.</param>
    /// <param name="startRef">The remote-tracking ref the branch starts at.</param>
    /// <param name="worktreePath">The directory the worktree occupies.</param>
    /// <returns>The outcome of <c>git worktree add</c>.</returns>
    GitOutcome CreateBranchInWorktree(string branch, string startRef, string worktreePath);

    /// <summary>Checks an existing branch out in a new worktree.</summary>
    /// <param name="branch">The existing branch.</param>
    /// <param name="worktreePath">The directory the worktree occupies.</param>
    /// <param name="startRef">The remote-tracking ref to start from when the branch does not yet exist locally.</param>
    /// <returns>The outcome of <c>git worktree add</c>.</returns>
    GitOutcome AddWorktreeForBranch(string branch, string worktreePath, string? startRef = null);
}

/// <summary>Opens a <see cref="IRunStartRunner"/> over a repository.</summary>
public interface IRunStartRunnerFactory
{
    /// <summary>Opens a runner whose git commands run in a directory.</summary>
    /// <param name="repositoryRoot">The directory every git command runs in; the current directory when the root is not known yet.</param>
    /// <returns>The runner.</returns>
    IRunStartRunner Open(string repositoryRoot);
}
