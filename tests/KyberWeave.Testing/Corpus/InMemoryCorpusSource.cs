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
    private readonly Dictionary<string, string> _links;
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
        _links = new Dictionary<string, string>(_names);
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
        string path = CorpusPath.Normalize(relativePath);

        // One path holds one entry, as it does on a real file system: adding the file replaces a link there.
        _links.Remove(path);
        _files[path] = [.. bytes];
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

    /// <summary>Adds a file symbolic link, replacing one already at the path.</summary>
    /// <param name="relativePath">The link's path relative to the corpus root, with forward slashes.</param>
    /// <param name="targetRelativePath">
    /// The path the link names, relative to the corpus root with forward slashes, so a fixture can point a link at
    /// another file of the corpus and prove which side of a root boundary it lands on.
    /// </param>
    /// <returns>This corpus, so a fixture can be built in one expression.</returns>
    /// <exception cref="ArgumentException">Either path is rooted or climbs above the corpus root.</exception>
    public InMemoryCorpusSource AddFileLink(string relativePath, string targetRelativePath)
    {
        string path = CorpusPath.Normalize(relativePath);

        // A link replaces a file at its own path, as a real link does; the target keeps its entry.
        _files.Remove(path);
        _links[path] = CorpusPath.Normalize(targetRelativePath);
        return this;
    }

    /// <inheritdoc />
    public bool Exists(string relativePath)
    {
        string path = CorpusPath.Normalize(relativePath);
        return _files.ContainsKey(path) || FinalTarget(path) is not null;
    }

    /// <inheritdoc />
    public bool DirectoryExists(string relativeDirectory)
    {
        string directory = CorpusPath.NormalizeDirectory(relativeDirectory);
        if (directory.Length == 0)
            return true;

        if (_directories.Contains(directory))
            return true;

        string prefix = directory + "/";
        return Entries.Concat(_directories).Any(path => path.StartsWith(prefix, Comparison));
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
        string? file = _files.ContainsKey(path) ? path : FinalTarget(path);

        if (file is null || !_files.TryGetValue(file, out byte[]? bytes))
            throw new FileNotFoundException($"The corpus holds no file at '{relativePath}'.", path);

        return [.. bytes];
    }

    /// <inheritdoc />
    public string? LinkTarget(string relativePath)
    {
        string path = CorpusPath.Normalize(relativePath);
        return FinalTarget(path) is { } target ? CorpusPath.Resolve(Root, target) : null;
    }

    /// <summary>Resolves a file link to the file it finally names, or null when the path is no link or names nothing.</summary>
    /// <param name="path">A canonical corpus path.</param>
    /// <returns>The canonical path of the file the link resolves to, or null.</returns>
    private string? FinalTarget(string path)
    {
        if (!_links.TryGetValue(path, out string? target))
            return null;

        // Follow every hop, as the adapter's final-target resolution does, and treat a cycle as
        // unresolvable rather than looping or stopping at an arbitrary length.
        HashSet<string> visited = new(_names) { path };
        while (_links.TryGetValue(target, out string? next))
        {
            if (!visited.Add(target))
                return null;

            target = next;
        }

        return _files.ContainsKey(target) ? target : null;
    }

    /// <summary>Every path the corpus holds as an entry, with a link listed under its own path.</summary>
    private IEnumerable<string> Entries => _files.Keys.Concat(_links.Keys);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(
        string relativeDirectory,
        string searchPattern = "*",
        bool recursive = true)
    {
        string directory = CorpusPath.NormalizeDirectory(relativeDirectory);
        string pattern = string.IsNullOrEmpty(searchPattern) ? "*" : searchPattern;
        string prefix = directory.Length == 0 ? string.Empty : directory + "/";

        return Entries
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
        IEnumerable<string> fromFiles = Entries
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
