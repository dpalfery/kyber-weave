using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads what git says about a working tree: its branch, its commits, and the files a change touched.</summary>
/// <remarks>
/// Every call runs git with argv and never a shell, and every method throws <see cref="InvalidOperationException"/> when git cannot answer.
/// The <c>…Async</c> members run git through the process seam without blocking a thread; a start failure is a
/// <see cref="System.ComponentModel.Win32Exception"/> and a run that outlives its limit is a <see cref="TimeoutException"/>.
/// </remarks>
public interface IGitChangeReader
{
    /// <summary>Lists the paths changed since <paramref name="baseRef"/> that still exist, together with untracked files.</summary>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="baseRef">The ref to diff the working tree against.</param>
    /// <param name="cancellationToken">Cancels the run and kills git's process tree.</param>
    Task<IReadOnlyList<string>> ResolveAsync(string workingDirectory, string baseRef, CancellationToken cancellationToken);

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

    /// <summary>The asynchronous twin of <see cref="ResolveSinceMergeBase"/>.</summary>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="baseRef">The ref the merge base is resolved against.</param>
    /// <param name="cancellationToken">Cancels the run and kills git's process tree.</param>
    Task<IReadOnlyList<ChangedPath>> ResolveSinceMergeBaseAsync(string workingDirectory, string baseRef, CancellationToken cancellationToken);

    /// <summary>The unified diff from the merge base of <paramref name="baseRef"/> and <c>HEAD</c> to the files on disk.</summary>
    /// <returns>The merge-base commit and the patch text.</returns>
    (string MergeBase, string Patch) DiffSinceMergeBase(string workingDirectory, string baseRef);

    /// <summary>The asynchronous twin of <see cref="DiffSinceMergeBase"/>.</summary>
    /// <param name="workingDirectory">The repository root.</param>
    /// <param name="baseRef">The ref the merge base is resolved against.</param>
    /// <param name="cancellationToken">Cancels the run and kills git's process tree.</param>
    /// <returns>The merge-base commit and the patch text.</returns>
    Task<(string MergeBase, string Patch)> DiffSinceMergeBaseAsync(string workingDirectory, string baseRef, CancellationToken cancellationToken);

    /// <summary>The unified diff from <paramref name="commit"/> to the files on disk; untracked files are not in it.</summary>
    string DiffSince(string workingDirectory, string commit);

    /// <summary>The asynchronous twin of <see cref="DiffSince"/>.</summary>
    /// <param name="workingDirectory">The repository root.</param>
    /// <param name="commit">The commit to compare the working tree with.</param>
    /// <param name="cancellationToken">Cancels the run and kills git's process tree.</param>
    Task<string> DiffSinceAsync(string workingDirectory, string commit, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the one-line commits from <paramref name="fromCommit"/> to <c>HEAD</c> that touched any of
    /// <paramref name="paths"/>, newest first, as <c>git log --oneline</c> writes them.
    /// </summary>
    /// <param name="workingDirectory">The repository root.</param>
    /// <param name="fromCommit">The commit the history starts after, the change's first review cycle.</param>
    /// <param name="paths">The repository-relative paths the commits must have touched; any other path is ignored.</param>
    /// <param name="cancellationToken">Cancels the run and kills git's process tree.</param>
    Task<IReadOnlyList<string>> LogOnelineAsync(string workingDirectory, string fromCommit, IReadOnlyList<string> paths, CancellationToken cancellationToken);
}
