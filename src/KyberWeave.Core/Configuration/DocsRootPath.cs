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
    /// The canonical form of <paramref name="value"/>: forward slashes, no trailing
    /// separator, no redundant <c>.</c> segments. Empty when it denotes the repository root.
    /// </summary>
    public static string Normalize(string value, string key)
    {
        ArgumentNullException.ThrowIfNull(value);

        var path = value.Trim().Replace('\\', '/').TrimEnd('/');

        if (Path.IsPathRooted(path) || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{key} '{value}' is absolute. Paths are relative to the repository root.");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != RepositoryRoot)
            .ToArray();

        if (segments.Contains(".."))
        {
            throw new ArgumentException(
                $"{key} '{value}' escapes the repository root. Paths are relative to it and stay inside it.");
        }

        return string.Join('/', segments);
    }

    /// <summary>Canonicalizes one documentation root, which may be the repository itself.</summary>
    public static string NormalizeRoot(string value, string key)
    {
        var root = Normalize(value, key);
        return root.Length == 0 ? RepositoryRoot : root;
    }

    /// <summary>
    /// Canonicalizes documentation roots in the order given, dropping duplicates.
    /// </summary>
    /// <remarks>
    /// Order is load-bearing: the first root is the primary one, so a repository decides
    /// where <c>docs init</c> scaffolds and which root diagnostics name by deciding what it
    /// lists first. Duplicates are dropped rather than rejected, because two spellings of
    /// one directory are a redundancy, not a decision to second-guess.
    /// </remarks>
    public static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> values, string key)
    {
        ArgumentNullException.ThrowIfNull(values);

        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            var root = NormalizeRoot(value, key);
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
