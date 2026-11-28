using KyberWeave.Core.RunStart;

namespace KyberWeave.Contracts.Services;

/// <summary>The git questions and actions <c>run start</c> needs, all run in one repository root.</summary>
public interface IRunStartRunner
{
    /// <summary>Gets the repository root every git command runs in.</summary>
    string RepositoryRoot { get; }

    /// <summary>Runs one git command and reports what it wrote, without blocking a thread while git runs.</summary>
    /// <param name="arguments">argv for git, one element per argument.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The exit code and both output streams.</returns>
    Task<GitOutcome> GitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);

    /// <summary>The repository root git reports for the current directory.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The root, or null outside a repository.</returns>
    Task<string?> DetectRepositoryRootAsync(CancellationToken cancellationToken = default);

    /// <summary>The git status outcome for the current checkout.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The outcome of <c>git status --porcelain</c>.</returns>
    Task<GitOutcome> CheckoutStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the checkout has no uncommitted change.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>True or false, or null when the probe failed.</returns>
    Task<bool?> CheckoutIsCleanAsync(CancellationToken cancellationToken = default);

    /// <summary>The git worktree list outcome for the current repository.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The outcome of <c>git worktree list --porcelain</c>.</returns>
    Task<GitOutcome> WorktreeListAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a second worktree is attached to this repository.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>True or false, or null when the probe failed.</returns>
    Task<bool?> AnotherWorktreeIsAttachedAsync(CancellationToken cancellationToken = default);

    /// <summary>What <c>origin/HEAD</c> points at.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The symbolic target, or null when git cannot say or the ref was never set.</returns>
    Task<string?> OriginHeadSymbolicRefAsync(CancellationToken cancellationToken = default);

    /// <summary>The commit a revision resolves to.</summary>
    /// <param name="revision">The revision.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The commit, or null when it does not resolve.</returns>
    Task<string?> ResolveRevisionAsync(string revision, CancellationToken cancellationToken = default);

    /// <summary>Whether a local branch of that name exists.</summary>
    /// <param name="branch">The branch.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>True when it exists.</returns>
    Task<bool> HasLocalBranchAsync(string branch, CancellationToken cancellationToken = default);

    /// <summary>Whether the remote has a branch of that name.</summary>
    /// <param name="branch">The branch.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>True when it exists.</returns>
    Task<bool> HasRemoteBranchAsync(string branch, CancellationToken cancellationToken = default);

    /// <summary>The nearest tag at a revision, else its abbreviated commit.</summary>
    /// <param name="revision">The revision to describe.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The description, or null when the revision cannot be described at all.</returns>
    Task<string?> DescribeAsync(string revision, CancellationToken cancellationToken = default);

    /// <summary>How far two revisions have diverged.</summary>
    /// <param name="left">The revision counted first, normally <c>HEAD</c>.</param>
    /// <param name="right">The revision counted second, normally the base ref.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>Commits only on <paramref name="left"/> and commits only on <paramref name="right"/>; both null when git cannot compare them.</returns>
    Task<(int? Ahead, int? Behind)> DivergenceAsync(string left, string right, CancellationToken cancellationToken = default);

    /// <summary>The commits a tip has that a base does not, with their full messages, in one call.</summary>
    /// <param name="baseRef">The ref the resumed branch started from.</param>
    /// <param name="tip">The resumed branch or its remote-tracking ref.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The outcome of the log, oldest commit first, NUL-separated.</returns>
    Task<GitOutcome> CommitMessagesAsync(string baseRef, string tip, CancellationToken cancellationToken = default);

    /// <summary>The address <c>origin</c> points at.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The address, or null when there is no such remote.</returns>
    Task<string?> OriginAddressAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the CodeGraph binary resolves on <c>PATH</c>.</summary>
    /// <returns>True when it is installed.</returns>
    bool CodeGraphBinaryIsAvailable();

    /// <summary>Refreshes the remote-tracking refs, refusing to prompt.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The outcome of the fetch.</returns>
    Task<GitOutcome> FetchAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a branch in the checkout the run is in.</summary>
    /// <param name="branch">The branch to create.</param>
    /// <param name="startRef">The remote-tracking ref the branch starts at.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The outcome of the switch.</returns>
    Task<GitOutcome> CreateBranchInPlaceAsync(string branch, string startRef, CancellationToken cancellationToken = default);

    /// <summary>Checks out an existing branch in the current checkout.</summary>
    /// <param name="branch">The branch to check out.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The outcome of the switch.</returns>
    Task<GitOutcome> SwitchToBranchAsync(string branch, CancellationToken cancellationToken = default);

    /// <summary>The worktree path a branch is created at: inside <c>.agent_worktrees</c> under the repository root, claimed so two runs in one second never share it.</summary>
    /// <param name="branch">The branch the worktree holds.</param>
    /// <param name="startedAt">When the run started; the folder name carries it in UTC.</param>
    /// <returns>The path, <c>&lt;root&gt;/.agent_worktrees/&lt;stamp&gt;-&lt;branch&gt;</c>. The caller releases the claim with <see cref="ReleaseWorktreeClaim"/> once git has run.</returns>
    string WorktreePathFor(string branch, DateTimeOffset startedAt);

    /// <summary>Excludes the worktree folder from git's status in the local exclude file, before any worktree is created in it.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The outcome: succeeded when the exclusion is in place; failed with git's or the file system's reason otherwise.</returns>
    Task<GitOutcome> EnsureWorktreeFolderExcludedAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the claim <see cref="WorktreePathFor"/> took.</summary>
    /// <param name="worktreePath">The path <see cref="WorktreePathFor"/> returned.</param>
    void ReleaseWorktreeClaim(string worktreePath);

    /// <summary>Creates a branch in a new worktree.</summary>
    /// <param name="branch">The branch to create.</param>
    /// <param name="startRef">The remote-tracking ref the branch starts at.</param>
    /// <param name="worktreePath">The directory the worktree occupies.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The outcome of <c>git worktree add</c>.</returns>
    Task<GitOutcome> CreateBranchInWorktreeAsync(string branch, string startRef, string worktreePath, CancellationToken cancellationToken = default);

    /// <summary>Checks an existing branch out in a new worktree.</summary>
    /// <param name="branch">The existing branch.</param>
    /// <param name="worktreePath">The directory the worktree occupies.</param>
    /// <param name="startRef">The remote-tracking ref to start from when the branch does not yet exist locally.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The outcome of <c>git worktree add</c>.</returns>
    Task<GitOutcome> AddWorktreeForBranchAsync(string branch, string worktreePath, string? startRef = null, CancellationToken cancellationToken = default);
}

/// <summary>Opens a <see cref="IRunStartRunner"/> over a repository.</summary>
public interface IRunStartRunnerFactory
{
    /// <summary>Opens a runner whose git commands run in a directory.</summary>
    /// <param name="repositoryRoot">The directory every git command runs in; the current directory when the root is not known yet.</param>
    /// <returns>The runner.</returns>
    IRunStartRunner Open(string repositoryRoot);
}
