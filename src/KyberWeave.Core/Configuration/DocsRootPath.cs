namespace KyberWeave.Core.Configuration;

/// <summary>
/// Canonicalizes the repository-relative paths a host supplies for documentation roots and
/// the catalog, and refuses one that leaves the repository.
/// </summary>
/// <remarks>
/// These paths reach <see cref="Path.Combine(string, string)"/>, which returns its second
/// argument outright when that argument is rooted, and <c>..</c> segments walk upward.
/// Unchecked, a docs root reads — and a catalog supplies vocabulary from — anywhere the
/// process can reach. <c>DocsScaffolder.RequireContained</c> already guards the write side;
/// this is the read side, shared by the config loader and the <c>--docs-root</c> option so
/// that neither entry point is the lenient one.
/// </remarks>
internal static class DocsRootPath
{
    /// <summary>The repository root itself, as a documentation root.</summary>
    public const string RepositoryRoot = ".";

    /// <summary>
    /// Path identity for documentation-root strings and corpus files.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="NormalizeRoots"/> and the document loader so "the same
    /// directory" means the same thing in both places. Windows volumes are
    /// case-insensitive; everywhere else is treated as case-sensitive — the same rule
    /// <c>DocsScaffolder</c> uses for containment, and deliberately not
    /// <c>OperatingSystem.IsLinux()</c>, which would collapse distinct roots on a
    /// case-sensitive APFS volume or FreeBSD.
    /// </remarks>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// The canonical form of <paramref name="value"/>: forward slashes, no trailing
    /// separator, no redundant <c>.</c> segments. Empty when it denotes the repository root.
    /// </summary>
    public static string Normalize(string value, string key)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Rootedness is checked before TrimEnd('/') — otherwise '/' collapses to empty and
        // is welcomed as the repository root, and a drive-letter form like 'C:/' can slip
        // past Path.IsPathRooted on non-Windows hosts where drive letters are not special.
        string path = value.Trim().Replace('\\', '/');

        if (IsAbsolute(path))
        {
            throw new ArgumentException(
                $"{key} '{value}' is absolute. Paths are relative to the repository root.");
        }

        path = path.TrimEnd('/');

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != RepositoryRoot)
            .ToArray();

        if (segments.Contains(".."))
        {
            throw new ArgumentException(
                $"{key} '{value}' escapes the repository root. Paths are relative to it and stay inside it.");
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// True when <paramref name="path"/> is absolute on this host, or looks like a Windows
    /// absolute / UNC path that <c>Path.IsPathRooted</c> would miss elsewhere.
    /// </summary>
    private static bool IsAbsolute(string path) =>
        Path.IsPathRooted(path)
        || path.StartsWith("//", StringComparison.Ordinal)
        || LooksLikeWindowsDriveAbsolute(path);

    /// <summary>
    /// <c>C:</c> / <c>C:/</c> are absolute on Windows; on Linux and macOS
    /// <c>Path.IsPathRooted</c> does not treat the drive letter as a root, so they
    /// must be refused explicitly rather than normalized into a relative segment.
    /// </summary>
    private static bool LooksLikeWindowsDriveAbsolute(string path) =>
        path.Length >= 2
        && char.IsAsciiLetter(path[0])
        && path[1] == ':';

    /// <summary>Canonicalizes one documentation root, which may be the repository itself.</summary>
    private static string NormalizeRoot(string value, string key)
    {
        string root = Normalize(value, key);
        return root.Length == 0 ? RepositoryRoot : root;
    }

    /// <summary>
    /// Canonicalizes documentation roots in the order given, dropping duplicates.
    /// </summary>
    /// <remarks>
    /// Order is load-bearing: the first root is the primary one, so a repository decides
    /// where <c>docs init</c> scaffolds and which root diagnostics name by deciding what it
    /// lists first. Duplicates are dropped rather than rejected, because two spellings of
    /// one directory are a redundancy, not a decision to second-guess. Duplicate detection
    /// follows <see cref="PathComparer"/> so a case-sensitive volume keeps <c>docs</c> and
    /// <c>Docs</c> as distinct roots.
    /// </remarks>
    public static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> values, string key)
    {
        ArgumentNullException.ThrowIfNull(values);

        List<string> roots = new List<string>();
        HashSet<string> seen = new HashSet<string>(PathComparer);

        foreach (string value in values)
        {
            string root = NormalizeRoot(value, key);
            if (seen.Add(root)) roots.Add(root);
        }

        if (roots.Count == 0)
        {
            throw new ArgumentException(
                $"{key} is empty. Name at least one directory, or omit it to keep the default.");
        }

        return roots;
    }

    /// <summary>
    /// Enumerates files under a root directory matching a search pattern, without descending
    /// into symbolic links.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method walks the directory tree manually one level at a time, rather than using
    /// <see cref="SearchOption.AllDirectories"/>, because .NET's built-in recursive enumeration
    /// invariably follows directory symbolic links (dotnet/runtime#52666). Resolving a symlink
    /// to check containment would mean statting paths outside the currently-listed directory,
    /// violating the principle that a walk never performs filesystem operations on paths beyond
    /// those its enumeration explicitly discovers.
    /// </para>
    /// <para>
    /// When an entry is itself a symlink (checked via <see cref="FileSystemInfo.LinkTarget"/>
    /// without resolving the link), the entry is skipped entirely: a symlinked directory is not
    /// descended into, a symlinked file is not yielded. This uniform policy — never resolving to
    /// find out where a link points — avoids the sandboxing/permission prompts that resolving a
    /// link could trigger, and keeps the walk confined to the space its enumeration discovers.
    /// Even a symlink whose target happens to sit inside the root is skipped, because
    /// determining containment would require resolving the link first.
    /// </para>
    /// </remarks>
    /// <param name="root">The root directory to enumerate.</param>
    /// <param name="searchPattern">A search pattern (e.g., "*.md") matching filenames to include.</param>
    /// <returns>An enumerable of full paths to files matching the pattern, in no guaranteed order.</returns>
    internal static IEnumerable<string> EnumerateContainedFiles(string root, string searchPattern)
    {
        return EnumerateContainedFilesRecursive(root, searchPattern);
    }

    private static IEnumerable<string> EnumerateContainedFilesRecursive(string directory, string searchPattern)
    {
        DirectoryInfo dirInfo = new DirectoryInfo(directory);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        IEnumerable<FileSystemInfo> entries = dirInfo.EnumerateFileSystemInfos("*", options).ToList();

        foreach (FileSystemInfo entry in entries)
        {
            // Skip symlinks entirely: do not descend into symlinked directories,
            // do not yield symlinked files. Check only the entry itself without resolving it.
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            if (entry is DirectoryInfo subdirectory)
            {
                // Recurse into non-symlinked directories
                foreach (string file in EnumerateContainedFilesRecursive(subdirectory.FullName, searchPattern))
                {
                    yield return file;
                }
            }
            else if (entry is FileInfo file && MatchesSearchPattern(file.Name, searchPattern))
            {
                // Yield files matching the search pattern
                yield return file.FullName;
            }
        }
    }

    private static bool MatchesSearchPattern(string fileName, string searchPattern)
    {
        // Match wildcard patterns like "*.md", compatible with Directory.EnumerateFiles semantics
        if (searchPattern == "*")
        {
            return true;
        }

        if (searchPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            // Pattern like "*.md" — match by extension
            string extension = searchPattern.Substring(1); // ".md"
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return fileName.EndsWith(extension, comparison);
        }

        // For other patterns, use exact match or return false
        // (This could be extended to support other glob patterns if needed)
        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fileName.Equals(searchPattern, cmp);
    }
}
