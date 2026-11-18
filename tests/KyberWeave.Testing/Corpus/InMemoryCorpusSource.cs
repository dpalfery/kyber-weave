using System.IO.Enumeration;

namespace KyberWeave.Tests.Corpus;

/// <summary>A corpus held in memory, for tests of the corpus seam itself and for tests whose subject is a fixture.</summary>
/// <remarks>
/// Nothing here touches a disk, the environment or the clock. Paths are repository-relative with forward slashes,
/// exactly as <see cref="ICorpusSource"/> declares them, and names follow the case rule this host's file system
/// uses unless the caller chooses one.
/// </remarks>
public sealed class InMemoryCorpusSource : ICorpusSource
{
    private readonly Dictionary<string, byte[]> _files;
    private readonly HashSet<string> _directories;
    private readonly StringComparer _names;

    /// <summary>Creates an empty corpus.</summary>
    /// <param name="caseSensitive">
    /// True for Linux name rules; false for the case-insensitive, case-preserving APFS and NTFS rules; null to take
    /// this host's rule, so a fixture answers as the checkout on the same machine does.
    /// </param>
    public InMemoryCorpusSource(bool? caseSensitive = null)
    {
        CaseSensitive = caseSensitive ?? OperatingSystem.IsLinux();
        _names = CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _files = new Dictionary<string, byte[]>(_names);
        _directories = new HashSet<string>(_names);
    }

    /// <summary>Gets whether names are compared case-sensitively in this corpus.</summary>
    public bool CaseSensitive { get; }

    /// <inheritdoc />
    public string Root { get; } = OperatingSystem.IsWindows() ? @"C:\in-memory-corpus" : "/in-memory-corpus";

    private StringComparison Comparison => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

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
        ArgumentNullException.ThrowIfNull(bytes);
        _files[CorpusPath.Normalize(relativePath)] = [.. bytes];
        return this;
    }

    /// <summary>Adds a directory, including an empty one, which a file-added path implies but cannot express.</summary>
    /// <param name="relativeDirectory">The directory's path relative to the corpus root, with forward slashes.</param>
    /// <returns>This corpus, so a fixture can be built in one expression.</returns>
    public InMemoryCorpusSource AddDirectory(string relativeDirectory)
    {
        string directory = CorpusPath.NormalizeDirectory(relativeDirectory);
        if (directory.Length > 0)
            _directories.Add(directory);

        return this;
    }

    /// <inheritdoc />
    public bool Exists(string relativePath) => _files.ContainsKey(CorpusPath.Normalize(relativePath));

    /// <inheritdoc />
    public bool DirectoryExists(string relativeDirectory)
    {
        string directory = CorpusPath.NormalizeDirectory(relativeDirectory);
        if (directory.Length == 0)
            return true;

        if (_directories.Contains(directory))
            return true;

        string prefix = directory + "/";
        return _files.Keys.Concat(_directories).Any(path => path.StartsWith(prefix, Comparison));
    }

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

        return [.. bytes];
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(
        string relativeDirectory,
        string searchPattern = "*",
        bool recursive = true)
    {
        string directory = CorpusPath.NormalizeDirectory(relativeDirectory);
        string pattern = string.IsNullOrEmpty(searchPattern) ? "*" : searchPattern;
        string prefix = directory.Length == 0 ? string.Empty : directory + "/";

        return _files.Keys
            .Where(path => recursive
                ? path.StartsWith(prefix, Comparison)
                    && !CorpusListing.HasSkippedDirectory(directory, path, Comparison)
                : CorpusPath.IsDirectChild(directory, path, Comparison))
            .Where(path => FileSystemName.MatchesSimpleExpression(
                pattern,
                Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)),
                ignoreCase: !CaseSensitive))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string relativeDirectory)
    {
        string directory = CorpusPath.NormalizeDirectory(relativeDirectory);
        string prefix = directory.Length == 0 ? string.Empty : directory + "/";

        // A file implies the directories above it; a declared directory names one even when it holds no file.
        IEnumerable<string> fromFiles = _files.Keys
            .Where(path => path.StartsWith(prefix, Comparison))
            .Select(path => path[prefix.Length..])
            .Where(rest => rest.Contains('/', StringComparison.Ordinal))
            .Select(rest => prefix + rest[..rest.IndexOf('/', StringComparison.Ordinal)]);

        IEnumerable<string> fromDirectories = _directories
            .Where(path => path.StartsWith(prefix, Comparison))
            .Select(path => path[prefix.Length..])
            .Where(rest => rest.Length > 0)
            .Select(rest => prefix + (rest.IndexOf('/', StringComparison.Ordinal) is int slash and >= 0 ? rest[..slash] : rest));

        return fromFiles
            .Concat(fromDirectories)
            .Distinct(_names)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }
}
