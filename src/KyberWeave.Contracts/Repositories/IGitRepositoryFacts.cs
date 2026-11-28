using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the facts the Arbiter derives itself from git: the repository, its worktrees, its dirty set and its change against a base.</summary>
/// <remarks>
/// Outside a repository, or without git on <c>PATH</c>, every fact is absent and nothing throws: the Arbiter rules
/// on fewer facts rather than failing the dispatch.
/// </remarks>
public interface IGitRepositoryFacts
{

    /// <summary>Resolves the repository root for a working directory.</summary>
    /// <returns>The root, or <c>null</c> when it cannot be determined.</returns>
    string? GetRoot(string workingDirectory, string gitExecutable = "git");


    /// <summary>
    /// The working trees of the repository at <paramref name="workingDirectory"/>, main first, from
    /// <c>git worktree list --porcelain</c>. Empty outside a repository or when git cannot run.
    /// </summary>
    IReadOnlyList<string> ListWorktrees(string workingDirectory, string gitExecutable = "git");


    /// <summary>
    /// Reads the repository's common git directory as an absolute path: the one directory every worktree of
    /// a repository shares, so it names the repository whichever worktree asks.
    /// </summary>
    /// <returns>The directory, or <c>null</c> outside a repository.</returns>
    string? GetCommonDirectory(string workingDirectory, string gitExecutable = "git");


    /// <summary>Reads the current branch name.</summary>
    /// <returns>The branch, or <c>null</c> on a detached head or outside a repository.</returns>
    string? GetCurrentBranch(string workingDirectory, string gitExecutable = "git");


    /// <summary>Whether <paramref name="revision" /> resolves in the checkout.</summary>
    /// <returns>False for a bogus ref or outside a repository; never throws.</returns>
    bool RefExists(string workingDirectory, string revision, string gitExecutable = "git");


    /// <summary>Reads the current commit.</summary>
    /// <returns>The full <c>HEAD</c> sha, or <c>null</c> when it cannot be determined.</returns>
    string? GetHead(string workingDirectory, string gitExecutable = "git");


    /// <summary>Reads the dirty set: staged, unstaged, and untracked paths.</summary>
    /// <returns>Porcelain status per repository-relative path; empty when absent.</returns>
    IReadOnlyDictionary<string, string> GetDirtyStatuses(
        string workingDirectory,
        string gitExecutable = "git");


    /// <summary>Hashes working-tree files the way the index sees them.</summary>
    /// <returns>Blob id per path that could be hashed; empty when absent.</returns>
    IReadOnlyDictionary<string, string> GetBlobIds(
        string workingDirectory,
        IEnumerable<string> paths,
        string gitExecutable = "git");


    /// <summary>Lists what changed since a base: committed diff plus the dirty set.</summary>
    /// <remarks>A rename reports its new path, matching the dirty set's convention.</remarks>
    /// <returns>Repository-relative paths; empty when absent.</returns>
    IReadOnlySet<string> GetChangedPathsSinceBase(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git");


    /// <summary>Reads the new-side hunk ranges of the diff against a base.</summary>
    /// <returns>One range per hunk; empty when absent.</returns>
    IReadOnlyList<HunkRange> GetHunkRanges(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git");


    /// <summary>Reads the changed hunks of the change against a base, committed work and the working tree together.</summary>
    /// <remarks>
    /// Untracked files are part of the change too: each is one whole-file hunk. The list is never
    /// cut short, because a lens that sees part of a change judges a partial change as the whole.
    /// </remarks>
    /// <returns>Every hunk ordered by path; <c>null</c> when the base is unreadable or nothing changed.</returns>
    IReadOnlyList<ChangedHunk>? GetChangedHunks(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git");


    /// <summary>
    /// Measures the change against a base once: its hunks with their line contents, and the paths
    /// renamed at full similarity. Everything the review facts need comes from this one diff.
    /// </summary>
    /// <remarks>
    /// The base is compared with the working tree, not with <c>HEAD</c>, so a change a lens has not
    /// yet committed is measured. Rename detection is on, so a rename shows as one entry; a rename
    /// that also edits content has hunks under its new path.
    /// </remarks>
    /// <returns>
    /// The change set, or <c>null</c> when the base cannot be read, an untracked file cannot be read,
    /// or nothing changed. A clean checkout is absence, not an empty measurement: a rule that reads
    /// an empty change as measured would answer for a change it never saw.
    /// </returns>
    ChangeSet? GetChangeSet(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git");


    /// <summary>
    /// Summarises a change as each changed path followed by its hunk headers, committed work
    /// and the working tree together, so a model can judge it without the full diff.
    /// </summary>
    /// <returns>The summary, or null when nothing changed or the diff cannot be read.</returns>
    string? GetDiffSummary(
        string workingDirectory,
        string baseRef,
        IReadOnlyCollection<string> changedPaths,
        string gitExecutable = "git");


    /// <summary>Captures the snapshot the ledger stores at pre-dispatch.</summary>
    /// <returns><c>HEAD</c> plus the blob id of every dirty or untracked path.</returns>
    GitSnapshot? CaptureSnapshot(
        string workingDirectory,
        string gitExecutable = "git");


    /// <summary>Diffs the working tree against a snapshot taken earlier.</summary>
    /// <remarks>
    /// A path counts when its status or blob id differs, or when <c>HEAD</c> has moved
    /// and the committed diff names it. <c>artifacts/**</c> never counts: gate output
    /// is evidence, not a dispatch change.
    /// </remarks>
    /// <returns>Repository-relative paths; empty when absent.</returns>
    IReadOnlySet<string> GetChangedPathsSinceSnapshot(
        string workingDirectory,
        GitSnapshot snapshot,
        string gitExecutable = "git");


    /// <summary>Resolves the repository root for a working directory, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The root, or <c>null</c> when it cannot be determined.</returns>
    Task<string?> GetRootAsync(
        string workingDirectory,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>The working trees of the repository, main first, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The worktrees; empty outside a repository or when git cannot run.</returns>
    Task<IReadOnlyList<string>> ListWorktreesAsync(
        string workingDirectory,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Reads the repository's common git directory, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The directory, or <c>null</c> outside a repository.</returns>
    Task<string?> GetCommonDirectoryAsync(
        string workingDirectory,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Reads the current branch name, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The branch, or <c>null</c> on a detached head or outside a repository.</returns>
    Task<string?> GetCurrentBranchAsync(
        string workingDirectory,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Whether <paramref name="revision"/> resolves in the checkout, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="revision">The revision to resolve.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>False for a bogus ref or outside a repository; never throws.</returns>
    Task<bool> RefExistsAsync(
        string workingDirectory,
        string revision,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Reads the current commit, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The full <c>HEAD</c> sha, or <c>null</c> when it cannot be determined.</returns>
    Task<string?> GetHeadAsync(
        string workingDirectory,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Reads the dirty set, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>Porcelain status per repository-relative path; empty when absent.</returns>
    Task<IReadOnlyDictionary<string, string>> GetDirtyStatusesAsync(
        string workingDirectory,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Hashes working-tree files the way the index sees them, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="paths">The repository-relative paths to hash.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>Blob id per path that could be hashed; empty when absent.</returns>
    Task<IReadOnlyDictionary<string, string>> GetBlobIdsAsync(
        string workingDirectory,
        IEnumerable<string> paths,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Lists what changed since a base, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="baseRef">The ref to compare with.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>Repository-relative paths; empty when absent.</returns>
    Task<IReadOnlySet<string>> GetChangedPathsSinceBaseAsync(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Reads the new-side hunk ranges of the diff against a base, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="baseRef">The ref to compare with.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>One range per hunk; empty when absent.</returns>
    Task<IReadOnlyList<HunkRange>> GetHunkRangesAsync(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Reads the changed hunks of the change against a base, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="baseRef">The ref to compare with.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>Every hunk ordered by path; <c>null</c> when the base is unreadable or nothing changed.</returns>
    Task<IReadOnlyList<ChangedHunk>?> GetChangedHunksAsync(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Measures the change against a base once, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="baseRef">The ref to compare with.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The change set, or <c>null</c> when it cannot be measured or nothing changed.</returns>
    Task<ChangeSet?> GetChangeSetAsync(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Summarises a change, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="baseRef">The ref to compare with.</param>
    /// <param name="changedPaths">The paths the summary lists.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The summary, or null when nothing changed or the diff cannot be read.</returns>
    Task<string?> GetDiffSummaryAsync(
        string workingDirectory,
        string baseRef,
        IReadOnlyCollection<string> changedPaths,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Captures the snapshot the ledger stores at pre-dispatch, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns><c>HEAD</c> plus the blob id of every dirty or untracked path.</returns>
    Task<GitSnapshot?> CaptureSnapshotAsync(
        string workingDirectory,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);


    /// <summary>Diffs the working tree against a snapshot taken earlier, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The directory git is asked in.</param>
    /// <param name="snapshot">The snapshot to compare with.</param>
    /// <param name="gitExecutable">The git program to start.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>Repository-relative paths; empty when absent.</returns>
    Task<IReadOnlySet<string>> GetChangedPathsSinceSnapshotAsync(
        string workingDirectory,
        GitSnapshot snapshot,
        string gitExecutable = "git",
        CancellationToken cancellationToken = default);
}
