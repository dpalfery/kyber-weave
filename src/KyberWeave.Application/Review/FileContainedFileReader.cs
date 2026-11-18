using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Review;

/// <summary>Reads repository files through the file system seam under a byte bound, refusing a path outside the tree or reached through a link.</summary>
/// <param name="files">The file system every read and metadata probe goes through.</param>
public sealed class FileContainedFileReader(IFileSystem files) : IContainedFileReader
{
    /// <inheritdoc />
    public bool IsContainedFile(string repositoryRoot, string relativePath)
    {
        string[] segments = relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();

        if (segments.Length == 0
            || segments.Contains("..", StringComparer.Ordinal)
            || IsRooted(relativePath))
        {
            return false;
        }

        string current = repositoryRoot;
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (files.GetLinkTarget(current) is not null)
                return false;
        }

        return files.FileExists(current);
    }

    /// <inheritdoc />
    public string[] ReadLines(string path, long maxBytes)
    {
        using Stream stream = files.OpenRead(path);
        using StringReader reader = new(BoundedFileReader.ReadAllText(stream, maxBytes));

        List<string> lines = [];
        while (reader.ReadLine() is { } line)
            lines.Add(line);

        return [.. lines];
    }

    /// <summary>
    /// True when the caller spelled an absolute path, which is not repository-relative. A Windows drive form is refused
    /// on every platform, as <see cref="Path.IsPathRooted(string)"/> does not treat it as rooted off Windows.
    /// </summary>
    private static bool IsRooted(string path) =>
        Path.IsPathRooted(path)
        || path.StartsWith("//", StringComparison.Ordinal)
        || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');
}
