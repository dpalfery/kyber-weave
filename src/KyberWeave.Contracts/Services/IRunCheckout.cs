using KyberWeave.Core.RunStart;

namespace KyberWeave.Contracts.Services;

/// <summary>Tells where a live run's work belongs, from the run records and the files git keeps in each checkout.</summary>
public interface IRunCheckout
{
    /// <summary>The canonical checkout of a common git directory.</summary>
    /// <param name="commonDirectory">The repository's common git directory.</param>
    /// <returns>Its parent when it is a <c>.git</c> directory, else null.</returns>
    string? CanonicalCheckoutOf(string commonDirectory);

    /// <summary>The live runs of a repository that work in a worktree of their own.</summary>
    /// <param name="homeDirectory">The operator's home, which holds the run records.</param>
    /// <param name="canonicalCheckout">The repository's canonical checkout.</param>
    /// <returns>The runs, or null when the run records could not be read.</returns>
    IReadOnlyList<RunRecord>? LiveSeparateRuns(string homeDirectory, string canonicalCheckout);

    /// <summary>The repository's canonical checkout that holds a path.</summary>
    /// <param name="path">The path to place, absolute or relative to the current directory.</param>
    /// <returns>
    /// The canonical checkout's root when the path is inside one, or null when it is outside every repository or
    /// inside a linked worktree, whose writes and commands are the run's own.
    /// </returns>
    string? CanonicalCheckoutContaining(string path);

    /// <summary>The refusal for a write that lands in a canonical checkout while a live run works in a worktree of its own.</summary>
    /// <param name="path">The path the tool writes, absolute or relative to <paramref name="workingDirectory"/>.</param>
    /// <param name="workingDirectory">The tool call's working directory.</param>
    /// <param name="homeDirectory">The operator's home, which holds the run records.</param>
    /// <returns>The refusal, or null to allow.</returns>
    string? StrayWriteRefusal(string path, string workingDirectory, string homeDirectory);

    /// <summary>The branch a worktree has checked out, read from its git files.</summary>
    /// <param name="worktree">The worktree.</param>
    /// <returns>The branch, or null for a detached head or anything unreadable.</returns>
    string? ReadCheckedOutBranch(string worktree);
}
