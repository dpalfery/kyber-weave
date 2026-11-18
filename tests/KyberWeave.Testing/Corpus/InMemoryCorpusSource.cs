using System.IO.Enumeration;

namespace KyberWeave.Tests.Corpus;

/// <summary>A corpus held in memory, for tests of the corpus seam itself and for tests whose subject is a fixture.</summary>
/// <remarks>
/// Nothing here touches a disk, the environment or the clock. Paths are repository-relative with forward slashes,
/// exactly as <see cref="ICorpusSource"/> declares them, and names are compared case-sensitively.
/// </remarks>
public sealed class InMemoryCorpusSource : ICorpusSource
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Root { get; } = OperatingSystem.IsWindows() ? @"C:\in-memory-corpus" : "/in-memory-corpus";

    /// <summary>Adds a text file, replacing one already at the path.</summary>
    /// <param name="relativePath">The file's path relative to the corpus root, with forward slashes.</param>
    /// <param name="text">The text to store as UTF-8.</param>
    /// <returns>This corpus, so a fixture can be built in one expression.</returns>
    public InMemoryCorpusSource Add(string relativePath, string text) =>
        AddBytes(relativePath, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Adds a file from its bytes, replacing one already at the path.</summary>
    /// <param name="relativePath">The file's path relative to the corpus root, with forward slashes.</param>
    /// <param name="bytes">The file's contents.</param>
    /// <returns>This corpus, so a fixture can be built in one expression.</returns>
    public InMemoryCorpusSource AddBytes(string relativePath, byte[] bytes)
    {
        _files[CorpusPath.Normalize(relativePath)] = bytes ?? throw new ArgumentNullException(nameof(bytes));
        return this;
    }

    /// <inheritdoc />
    public bool Exists(string relativePath) => _files.ContainsKey(CorpusPath.Normalize(relativePath));

    /// <inheritdoc />
    public string ReadText(string relativePath)
    {
        string path = CorpusPath.Normalize(relativePath);
        return System.Text.Encoding.UTF8.GetString(ReadBytes(path));
    }

    /// <inheritdoc />
    public byte[] ReadBytes(string relativePath)
    {
        string path = CorpusPath.Normalize(relativePath);

        if (!_files.TryGetValue(path, out byte[]? bytes))
            throw new FileNotFoundException($"The corpus holds no file at '{relativePath}'.", path);

        return bytes;
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(
        string relativeDirectory,
        string searchPattern = "*",
        bool recursive = true)
    {
        string directory = CorpusPath.Normalize(relativeDirectory);
        string pattern = string.IsNullOrEmpty(searchPattern) ? "*" : searchPattern;

        return _files.Keys
            .Where(path => recursive
                ? path.StartsWith(directory + "/", StringComparison.Ordinal)
                    && !CorpusListing.HasSkippedDirectory(directory, path)
                : CorpusPath.IsDirectChild(directory, path))
            .Where(path => FileSystemName.MatchesSimpleExpression(
                pattern,
                Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)),
                ignoreCase: false))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string relativeDirectory)
    {
        string prefix = CorpusPath.Normalize(relativeDirectory) + "/";

        return _files.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(path => path[prefix.Length..])
            .Select(rest => rest.IndexOf('/', StringComparison.Ordinal) is int slash and >= 0 ? prefix + rest[..slash] : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }
}
