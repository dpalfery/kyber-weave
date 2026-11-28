namespace KyberWeave.Contracts.Repositories;

/// <summary>Names the working tree a directory belongs to and the git directory its sibling worktrees share.</summary>
public interface IGitWorktreeIdentity
{
    /// <summary>Asks git for the working tree's top level and the git directory its worktrees share.</summary>
    /// <param name="directory">A directory inside the working tree.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The worktree roots, or the reason git could not name them.</returns>
    Task<GitWorktreeRoots> TryRootsAsync(string directory, CancellationToken cancellationToken = default);
}

/// <summary>The working tree a directory belongs to, or why git could not name it.</summary>
/// <param name="TopLevel">The absolute top level of the working tree, or null when git could not say.</param>
/// <param name="CommonDirectory">The absolute git directory every worktree of the repository shares, or null when git could not say.</param>
/// <param name="Failure">Why git could not say; null on success.</param>
public sealed record GitWorktreeRoots(string? TopLevel, string? CommonDirectory, string? Failure)
{
    /// <summary>True when both roots were named.</summary>
    public bool Found => TopLevel is not null && CommonDirectory is not null;
}
