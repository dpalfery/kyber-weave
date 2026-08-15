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
    public static string NormalizeRoot(string value, string key)
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
}
