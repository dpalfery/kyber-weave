namespace KyberWeave.Contracts.Repositories;

/// <summary>Names the branch a checkout has checked out.</summary>
public interface ICheckedOutBranchReader
{
    /// <summary>Reads the branch of a checkout or worktree.</summary>
    /// <param name="workingDirectory">The checkout.</param>
    /// <returns>The branch, or null on a detached head, outside a repository, or when git cannot answer.</returns>
    string? Read(string workingDirectory);
}
