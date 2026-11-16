using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>Finds the git work tree a review belongs to and the change checked out in it.</summary>
public interface IReviewWorkTreeService
{
    /// <summary>The git work tree that contains <paramref name="path"/>; a path in no work tree answers itself.</summary>
    /// <param name="path">A path inside the work tree.</param>
    string Containing(string path);

    /// <summary>The branch, base and head of the change checked out in <paramref name="workTree"/>; null when git cannot name them.</summary>
    /// <param name="workTree">The work tree.</param>
    /// <param name="baseRef">The ref the base is resolved against; the defaults are tried when null.</param>
    ReviewCycleIdentity? IdentityOf(string workTree, string? baseRef);

    /// <summary>Splits the finished cycles of <paramref name="workTree"/> into those that belong to its change and those that do not.</summary>
    /// <param name="workTree">The work tree.</param>
    /// <param name="baseRef">The ref the base is resolved against.</param>
    /// <param name="finished">The finished cycles to judge.</param>
    /// <param name="currentCycleFolder">The folder of the cycle being run, when the caller knows it.</param>
    PriorCycles Prior(string workTree, string? baseRef, IReadOnlyList<ReviewCycleRecord> finished, string? currentCycleFolder = null);

    /// <summary>One plain line when the work tree is a canonical checkout while a live run works in a worktree of its own; null otherwise.</summary>
    /// <param name="workTree">The work tree the command was run against.</param>
    /// <param name="homeDirectory">The operator's home, which holds the run records.</param>
    string? LiveRunNote(string workTree, string homeDirectory);

    /// <summary>The home directory the run records are read from.</summary>
    string HomeDirectory();
}
