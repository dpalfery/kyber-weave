using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.RunStart;

namespace KyberWeave.Core.RunStart;

/// <summary>
/// Where a live run's work belongs, from machine facts only. The logic is <see cref="RunCheckoutGuard" /> in the data
/// layer; this static entry point stays for the callers that have no container, and answers over the real file system.
/// </summary>
public static class RunCheckout
{
    private static readonly RunCheckoutGuard Guard = new(
        new PhysicalFileSystem(),
        new FileRunStateStore(
            new PhysicalFileSystem(),
            new GitCheckedOutBranchReader(new ProcessGitCommandRunner(ProcessGitCommandRunner.DefaultTimeout))));

    /// <summary>The canonical checkout of a common git directory: its parent when it is a <c>.git</c> directory, else null.</summary>
    public static string? CanonicalCheckoutOf(string commonDirectory) => Guard.CanonicalCheckoutOf(commonDirectory);

    /// <summary>
    /// The live runs of the repository whose canonical checkout is <paramref name="canonicalCheckout" /> that
    /// work in a worktree of their own: the record's worktree exists, is not the canonical checkout, and still
    /// has the run's branch checked out.
    /// </summary>
    /// <returns>The runs, or null when the run records could not be read.</returns>
    public static IReadOnlyList<RunRecord>? LiveSeparateRuns(string homeDirectory, string canonicalCheckout) =>
        Guard.LiveSeparateRuns(homeDirectory, canonicalCheckout);

    /// <summary>
    /// The refusal for a write to <paramref name="path" />, or null to allow. A write is refused only when it
    /// lands inside a repository's canonical checkout while a live run of that repository works in a worktree
    /// of its own. A path in a linked worktree, outside every repository, or inside the git directory is allowed.
    /// </summary>
    /// <param name="path">The path the tool writes, absolute or relative to <paramref name="workingDirectory" />.</param>
    /// <param name="workingDirectory">The tool call's working directory.</param>
    /// <param name="homeDirectory">The operator's home, which holds the run records.</param>
    public static string? StrayWriteRefusal(string path, string workingDirectory, string homeDirectory) =>
        Guard.StrayWriteRefusal(path, workingDirectory, homeDirectory);

    /// <summary>
    /// The branch <paramref name="worktree" /> has checked out, read from its git files: a linked worktree's
    /// <c>.git</c> file names its git directory, a main checkout's <c>.git</c> directory holds <c>HEAD</c>.
    /// Null for a detached head or anything unreadable.
    /// </summary>
    public static string? ReadCheckedOutBranch(string worktree) => Guard.ReadCheckedOutBranch(worktree);

    /// <summary>The absolute path with every symbolic link on the way resolved.</summary>
    internal static string RealPath(string path) => Guard.RealPath(path);
}
