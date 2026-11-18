namespace KyberWeave.Tests.Corpus;

/// <summary>Turns a corpus-relative path into a canonical one, so both corpus implementations agree on what a path means.</summary>
internal static class CorpusPath
{
    private const string Separator = "/";

    /// <summary>Canonicalizes a path relative to the corpus root.</summary>
    /// <param name="relativePath">The path a test supplied, with either slash.</param>
    /// <returns>The path with forward slashes, without a leading <c>./</c> or trailing slash.</returns>
    /// <exception cref="ArgumentException">The path is empty, rooted, or climbs above the root with <c>..</c>.</exception>
    public static string Normalize(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string candidate = relativePath.Replace('\\', '/');

        // Path.IsPathRooted is platform-dependent: "C:/x" is rooted only on Windows. A corpus path is portable, so a
        // drive-letter form is refused everywhere.
        bool hasDriveLetter = candidate.Length >= 2 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':';

        if (candidate.StartsWith('/') || hasDriveLetter || Path.IsPathRooted(relativePath))
            throw new ArgumentException($"A corpus path must be relative to the repository root; '{relativePath}' is rooted.", nameof(relativePath));

        string[] segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<string> kept = new(segments.Length);

        foreach (string segment in segments)
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (kept.Count == 0)
                    throw new ArgumentException($"A corpus path must not climb above the repository root; '{relativePath}' does.", nameof(relativePath));

                kept.RemoveAt(kept.Count - 1);
                continue;
            }

            kept.Add(segment);
        }

        if (kept.Count == 0)
            throw new ArgumentException($"A corpus path must name something under the repository root; '{relativePath}' does not.", nameof(relativePath));

        return string.Join(Separator, kept);
    }

    /// <summary>
    /// Canonicalizes a directory path relative to the corpus root, where the empty string and <c>.</c> name the root
    /// itself, so a listing can start there.
    /// </summary>
    /// <param name="relativeDirectory">
    /// The directory a caller supplied, with either slash. Only the empty string and <c>.</c> name the root: a missing
    /// name, including a whitespace-only one, is a caller error, as it is for a file path.
    /// </param>
    /// <returns>The canonical directory path, or the empty string for the corpus root.</returns>
    /// <exception cref="ArgumentException">
    /// The name is null or whitespace, the path is rooted, or it climbs above the root with <c>..</c>.
    /// </exception>
    public static string NormalizeDirectory(string relativeDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        if (relativeDirectory.Length == 0)
            return string.Empty;

        string candidate = relativeDirectory.Replace('\\', '/');
        return candidate is "." or "./" ? string.Empty : Normalize(relativeDirectory);
    }

    /// <summary>Resolves a canonical corpus path against a root.</summary>
    /// <param name="root">The absolute corpus root.</param>
    /// <param name="canonicalPath">A path <see cref="Normalize"/> returned.</param>
    /// <returns>The absolute path.</returns>
    public static string Resolve(string root, string canonicalPath) =>
        Path.Combine(root, canonicalPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Gets whether a canonical path names a file directly in a canonical directory.</summary>
    /// <param name="directory">The directory's canonical path.</param>
    /// <param name="path">The file's canonical path.</param>
    /// <param name="comparison">The corpus's name comparison, which follows its host file system.</param>
    /// <returns>True when the file sits directly in the directory.</returns>
    public static bool IsDirectChild(string directory, string path, StringComparison comparison)
    {
        string prefix = directory.Length == 0 ? string.Empty : directory + Separator;
        return path.StartsWith(prefix, comparison) && path.IndexOf('/', prefix.Length) < 0;
    }
}
