using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads what git says about a working tree: its branch, its commits, and the files a change touched.</summary>
/// <remarks>Every call runs git with argv and never a shell, and every method throws <see cref="InvalidOperationException"/> when git cannot answer.</remarks>
public interface IGitChangeReader
{
    /// <summary>Lists the paths changed since <paramref name="baseRef"/> that still exist, together with untracked files.</summary>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="baseRef">The ref to diff the working tree against.</param>
    IReadOnlyList<string> Resolve(string workingDirectory, string baseRef);

    /// <summary>The checked-out branch name, or an empty string on a detached head.</summary>
    string CurrentBranch(string workingDirectory);

    /// <summary>The commit <c>HEAD</c> names.</summary>
    string HeadCommit(string workingDirectory);

    /// <summary>The merge-base commit of <paramref name="baseRef"/> and <c>HEAD</c>.</summary>
    string MergeBaseCommit(string workingDirectory, string baseRef);

    /// <summary>Whether <paramref name="commit"/> is <c>HEAD</c> or one of its ancestors; false when git does not know it.</summary>
    bool IsAncestorOfHead(string workingDirectory, string commit);

    /// <summary>Lists every path the working tree changed since <paramref name="commit"/>, deleted paths included, together with untracked files.</summary>
    IReadOnlyList<string> ResolveSince(string workingDirectory, string commit);

    /// <summary>Lists the paths changed since the merge base of <paramref name="baseRef"/> and <c>HEAD</c> with the kind of each change, together with untracked files.</summary>
    IReadOnlyList<ChangedPath> ResolveSinceMergeBase(string workingDirectory, string baseRef);

    /// <summary>The unified diff from the merge base of <paramref name="baseRef"/> and <c>HEAD</c> to the files on disk.</summary>
    /// <returns>The merge-base commit and the patch text.</returns>
    (string MergeBase, string Patch) DiffSinceMergeBase(string workingDirectory, string baseRef);

    /// <summary>The unified diff from <paramref name="commit"/> to the files on disk; untracked files are not in it.</summary>
    string DiffSince(string workingDirectory, string commit);

    /// <summary>
    /// Lists the one-line commits from <paramref name="fromCommit"/> to <c>HEAD</c> that touched any of
    /// <paramref name="paths"/>, newest first, as <c>git log --oneline</c> writes them.
    /// </summary>
    /// <param name="workingDirectory">The repository root.</param>
    /// <param name="fromCommit">The commit the history starts after, the change's first review cycle.</param>
    /// <param name="paths">The repository-relative paths the commits must have touched.</param>
    IReadOnlyList<string> LogOneline(string workingDirectory, string fromCommit, IReadOnlyList<string> paths);
}
