using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Review;

/// <summary>Reads the lines a finding cites from the file system, refusing a path outside the repository, an oversized file and a link.</summary>
/// <param name="files">The file system every read and metadata probe goes through.</param>
/// <param name="contained">Decides whether the resolved file is inside the tree and reached without a link.</param>
/// <param name="paths">Resolves a root the caller spelled relatively.</param>
/// <param name="environment">Names the working directory a relative root resolves against.</param>
public sealed class FileCitedSourceFiles(
    IFileSystem files,
    IContainedFileReader contained,
    IPathResolver paths,
    IProcessEnvironment environment) : ICitedSourceFiles
{
    /// <inheritdoc />
    public string? Resolve(string repositoryRoot, string file, long maxBytes)
    {
        string resolvedRoot = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        string root = resolvedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = paths.GetFullPath(Path.Combine(resolvedRoot, file), environment.CurrentDirectory);
        return path.StartsWith(root, StringComparison.Ordinal)
            && contained.IsContainedFile(resolvedRoot, path[root.Length..])
            && files.GetLength(path) <= maxBytes
                ? path
                : null;
    }

    /// <inheritdoc />
    public string[] ReadWindow(string path, long firstLine, long count) =>
        [.. files.ReadLines(path).Skip(SkipCount(firstLine)).Take((int)Math.Min(count, int.MaxValue))];

    /// <summary>
    /// The lines to skip before the window's first line, clamped to what <see cref="Enumerable.Skip{TSource}"/> takes:
    /// the interface counts from 1, and casting a line number above <see cref="int.MaxValue"/> straight to
    /// <see cref="int"/> wraps negative and would return the start of the file instead of nothing.
    /// </summary>
    private static int SkipCount(long firstLine) =>
        (int)Math.Min(Math.Max(firstLine - 1, 0), int.MaxValue);
}
