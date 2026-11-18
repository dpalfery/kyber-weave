using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Review;

/// <summary>Reads the lines a finding cites from the file system, refusing a path outside the repository, an oversized file and a link.</summary>
/// <param name="files">The file system every read and metadata probe goes through.</param>
/// <param name="contained">Decides whether the resolved file is inside the tree and reached without a link.</param>
public sealed class FileCitedSourceFiles(IFileSystem files, IContainedFileReader contained) : ICitedSourceFiles
{
    /// <inheritdoc />
    public string? Resolve(string repositoryRoot, string file, long maxBytes)
    {
        string root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(repositoryRoot, file));
        return path.StartsWith(root, StringComparison.Ordinal)
            && contained.IsContainedFile(repositoryRoot, path[root.Length..])
            && files.GetLength(path) <= maxBytes
                ? path
                : null;
    }

    /// <inheritdoc />
    public string[] ReadWindow(string path, long firstLine, long count) =>
        [.. files.ReadLines(path).Skip((int)(firstLine - 1)).Take((int)Math.Min(count, int.MaxValue))];
}
