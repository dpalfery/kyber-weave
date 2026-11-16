namespace KyberWeave.Contracts.Repositories;

/// <summary>Names the working tree a directory belongs to and the git directory its sibling worktrees share.</summary>
public interface IGitWorktreeIdentity
{
    /// <summary>Asks git for the working tree's top level and the git directory its worktrees share.</summary>
    /// <param name="directory">A directory inside the working tree.</param>
    /// <param name="topLevel">The absolute top level of the working tree.</param>
    /// <param name="commonDirectory">The absolute git directory every worktree of the repository shares.</param>
    /// <param name="failure">Why git could not say; null on success.</param>
    /// <returns>True when both were named.</returns>
    bool TryRoots(string directory, out string? topLevel, out string? commonDirectory, out string? failure);
}
