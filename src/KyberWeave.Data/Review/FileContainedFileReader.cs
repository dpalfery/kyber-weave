using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Review;

/// <summary>Reads repository files from disk under a byte bound, refusing a path outside the tree or reached through a link.</summary>
public sealed class FileContainedFileReader : IContainedFileReader
{
    /// <inheritdoc />
    public bool IsContainedFile(string repositoryRoot, string relativePath) =>
        DocsRootPath.IsContainedFile(repositoryRoot, relativePath, []);

    /// <inheritdoc />
    public string[] ReadLines(string path, long maxBytes) => BoundedFileReader.ReadAllLines(path, maxBytes);
}
