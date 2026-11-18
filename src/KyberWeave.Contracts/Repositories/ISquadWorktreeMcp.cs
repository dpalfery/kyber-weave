namespace KyberWeave.Contracts.Repositories;

/// <summary>Binds a worktree's MCP servers to the canonical repository root and keeps its CodeGraph index its own.</summary>
public interface ISquadWorktreeMcp
{
    /// <summary>Replaces the values of <c>--repo-root</c> and <c>--path</c> with absolute canonical paths.</summary>
    /// <param name="args">The original process command-line arguments.</param>
    /// <param name="canonicalRoot">The canonical repository root path; it must be absolute.</param>
    /// <returns>A new argument list with canonical paths spliced into <c>--repo-root</c> and <c>--path</c>.</returns>
    /// <exception cref="ArgumentException">
    /// The root is relative, an argument is blank, a flag has no value, or a <c>--path</c> resolves outside the root
    /// or through a link that cannot be resolved.
    /// </exception>
    IReadOnlyList<string> SpliceArguments(IReadOnlyList<string> args, string canonicalRoot);

    /// <summary>Fails closed when <c>.codegraph</c> in a worktree is a symbolic link or reparse point.</summary>
    /// <param name="worktreeDirectory">The path to the worktree directory.</param>
    /// <exception cref="DirectoryNotFoundException">The worktree directory does not exist.</exception>
    /// <exception cref="InvalidOperationException"><c>.codegraph</c> is a symbolic link or reparse point.</exception>
    void EnsureNoCodegraphSymlink(string worktreeDirectory);
}
