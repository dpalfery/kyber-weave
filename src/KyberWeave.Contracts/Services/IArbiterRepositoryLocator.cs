namespace KyberWeave.Contracts.Services;

/// <summary>Finds the repository root and the arbiter state directory for a working path.</summary>
public interface IArbiterRepositoryLocator
{
    /// <summary>
    /// Resolves the repository root for a working path: <c>git rev-parse --show-toplevel</c> first, the path itself when
    /// git cannot answer (outside a repository, or without git on <c>PATH</c>), so offline reads degrade rather than fail.
    /// </summary>
    /// <param name="path">The working path.</param>
    /// <returns>The root, or the full path when none is found.</returns>
    string ResolveRepositoryRoot(string path);

    /// <summary>Resolves the directory holding the ledger and the decision log: <c>artifacts/arbiter/</c> under the repository root.</summary>
    /// <param name="path">The working path.</param>
    /// <returns>The arbiter state directory.</returns>
    string ResolveArbiterDirectory(string path);

    /// <summary>
    /// Finds the nearest directory at or above <paramref name="start"/> that holds a <c>.git</c> entry (a directory in a
    /// clone, a file in a worktree), without running git.
    /// </summary>
    /// <param name="start">The directory to start from.</param>
    /// <returns>The root, or the full start path when there is none.</returns>
    string FindGitRoot(string start);
}
