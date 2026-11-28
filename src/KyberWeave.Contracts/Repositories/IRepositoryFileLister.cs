namespace KyberWeave.Contracts.Repositories;

/// <summary>Lists the files that belong to a repository.</summary>
public interface IRepositoryFileLister
{
    /// <summary>Lists the regular files of a repository, as git does when it can answer and by a walk otherwise.</summary>
    /// <param name="repositoryRoot">The repository directory.</param>
    /// <returns>Repository-relative paths, forward-slashed; links, deleted files and files under dependency or build-output directories are left out.</returns>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    IReadOnlyList<string> List(string repositoryRoot);

    /// <summary>Lists the regular files of a repository without blocking a thread, as git does when it can answer and by a walk otherwise.</summary>
    /// <param name="repositoryRoot">The repository directory.</param>
    /// <param name="cancellationToken">Cancels the git listing.</param>
    /// <returns>Repository-relative paths, forward-slashed; links, deleted files and files under dependency or build-output directories are left out.</returns>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    Task<IReadOnlyList<string>> ListAsync(string repositoryRoot, CancellationToken cancellationToken = default);
}
