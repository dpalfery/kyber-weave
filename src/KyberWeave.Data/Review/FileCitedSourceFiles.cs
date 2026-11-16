using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Data.Review;

/// <summary>Reads the lines a finding cites from disk, refusing a path outside the repository, an oversized file and a link.</summary>
public sealed class FileCitedSourceFiles : ICitedSourceFiles
{
    /// <inheritdoc />
    public string? Resolve(string repositoryRoot, string file, long maxBytes)
    {
        string root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(repositoryRoot, file));
        return path.StartsWith(root, StringComparison.Ordinal)
            && DocsRootPath.IsContainedFile(repositoryRoot, path[root.Length..], [])
            && new FileInfo(path).Length <= maxBytes
                ? path
                : null;
    }

    /// <inheritdoc />
    public string[] ReadWindow(string path, long firstLine, long count) =>
        [.. File.ReadLines(path).Skip((int)(firstLine - 1)).Take((int)Math.Min(count, int.MaxValue))];
}
