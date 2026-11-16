using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Asks the file system the questions a path check about a repository tree needs answered.</summary>
public interface IRepositoryTreeProbe
{
    /// <summary>Whether a repository root exists and can be listed.</summary>
    /// <param name="root">The repository root.</param>
    RepositoryRootAccess ProbeRoot(string root);

    /// <summary>Whether a directory exists.</summary>
    /// <param name="path">The directory's full path.</param>
    bool DirectoryExists(string path);

    /// <summary>
    /// Whether the file, or a directory above it, is a symbolic link whose target lies outside the root. A missing entry
    /// is not a link; a link that cannot be resolved is treated as escaping.
    /// </summary>
    /// <param name="root">The repository root the path must stay inside.</param>
    /// <param name="path">The repository-relative path.</param>
    bool LeadsOutsideThroughSymlink(string root, string path);
}
