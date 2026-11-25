namespace KyberWeave.Contracts.Repositories;

/// <summary>Names the branch a checkout has checked out.</summary>
public interface ICheckedOutBranchReader
{
    /// <summary>Reads the branch of a checkout or worktree.</summary>
    /// <param name="workingDirectory">The checkout.</param>
    /// <returns>The branch, or null on a detached head, outside a repository, or when git cannot answer.</returns>
    string? Read(string workingDirectory);

    /// <summary>Tells whether one branch is an ancestor of another, as git sees them from a checkout.</summary>
    /// <param name="workingDirectory">The checkout git is asked in.</param>
    /// <param name="ancestor">The branch that may be the ancestor.</param>
    /// <param name="descendant">The branch that may descend from it.</param>
    /// <returns>True or false as git answers, or null when git cannot answer.</returns>
    bool? IsAncestor(string workingDirectory, string ancestor, string descendant);
}
