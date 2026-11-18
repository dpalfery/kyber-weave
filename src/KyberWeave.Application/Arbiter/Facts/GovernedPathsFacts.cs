namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// Derives <c>docs.governed-paths-changed</c>, the fact the docs-impact shortcut reads at step 0. It is computed after
/// the git facts are read, from <c>review.changed-paths</c>, because the run's changed paths are not known when the
/// fact bag is first built.
/// </summary>
internal static class GovernedPathsFacts
{
    /// <summary>The fact a docs-impact shortcut reads: true when the run's changed paths touch a governed document path.</summary>
    public const string Name = "docs.governed-paths-changed";

    private const string ChangedPaths = "review.changed-paths";
    private const string MarkdownSuffix = ".md";

    /// <summary>
    /// Sets the fact on a bag that declares it, so no other trigger's bag carries it. The fact is true when any changed
    /// path is under a configured docs root or is a markdown file. Unknown or empty changed paths also answer true, so
    /// the dispatch is kept rather than shortcut on missing evidence.
    /// </summary>
    /// <param name="facts">The fact bag the trigger's rules read.</param>
    /// <param name="docsRoots">The configured docs roots, relative to the repository root.</param>
    public static ArbiterFactSet Enrich(ArbiterFactSet facts, IReadOnlyList<string> docsRoots)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(docsRoots);

        if (!facts.Contains(Name))
        {
            return facts;
        }

        IReadOnlyList<string> paths = PathsOf(facts);
        bool governed = paths.Count == 0 || paths.Any(path => IsGoverned(path, docsRoots));
        return facts.With(Name, governed, ArbiterFactLabel.Derived);
    }

    private static IReadOnlyList<string> PathsOf(ArbiterFactSet facts)
    {
        if (!facts.TryGet(ChangedPaths, out ArbiterFact? fact) || fact.Value is not IEnumerable<string> paths)
        {
            return [];
        }

        return [.. paths];
    }

    private static bool IsGoverned(string path, IReadOnlyList<string> docsRoots)
    {
        string normal = Normalise(path);
        if (normal.EndsWith(MarkdownSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string root in docsRoots)
        {
            string prefix = Normalise(root);
            if (prefix.Length > 0 && normal.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Forward slashes, no leading <c>./</c> and no surrounding slashes, so a root and a path compare on segments.</summary>
    private static string Normalise(string path)
    {
        string forward = path.Replace('\\', '/');
        if (forward.StartsWith("./", StringComparison.Ordinal))
        {
            forward = forward[2..];
        }

        return forward.Trim('/');
    }
}
