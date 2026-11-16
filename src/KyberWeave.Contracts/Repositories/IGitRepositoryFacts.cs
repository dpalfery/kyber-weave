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
}
