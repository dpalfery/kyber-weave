namespace KyberWeave.Core.Configuration;

/// <summary>
/// The raw directory walk behind documentation roots: the symlink-refusing enumeration and the
/// single-file containment check. It performs I/O, so it lives in the Data layer beside the
/// adapters that walk documentation roots; canonicalizing a root string is Application's
/// <c>DocsRootPath</c>.
/// </summary>
internal static class DocsRootWalk
{
    private const string RepositoryRoot = ".";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

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

    /// <summary>
    /// True when <paramref name="relativePath"/> names an existing file that is reachable
    /// from the repository root without crossing a symbolic link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the containment check for a single configured file, such as the catalog, that
    /// <see cref="EnumerateContainedFiles"/> is for a walk. The boundary is the innermost
    /// configured root containing the file, which is where a walk would reach it from, or the
    /// repository root when no root does. The boundary itself is not checked, because root
    /// selection is the host's decision. Every directory below it, and the file, is.
    /// </para>
    /// <para>
    /// Directories are checked outermost first, and <see cref="FileSystemInfo.LinkTarget"/> is
    /// read before <see cref="File.Exists(string?)"/>. Resolving a path follows every link in
    /// it, and <see cref="File.Exists(string?)"/> stats a link's target on Unix, so checking an
    /// inner component first, or existence first, would reach through the link this refuses.
    /// </para>
    /// </remarks>
    /// <param name="repoRoot">The absolute repository root.</param>
    /// <param name="relativePath">The file's repository-relative path.</param>
    /// <param name="relativeRoots">The configured documentation roots, repository-relative.</param>
    internal static bool IsContainedFile(string repoRoot, string relativePath, IEnumerable<string> relativeRoots)
    {
        string[] segments = Segments(relativePath);
        if (segments.Length == 0) return false;

        int boundaryDepth = 0;
        foreach (string root in relativeRoots)
        {
            string[] rootSegments = Segments(root);
            if (rootSegments.Length > boundaryDepth
                && rootSegments.Length < segments.Length
                && rootSegments.SequenceEqual(segments.Take(rootSegments.Length), PathComparer))
            {
                boundaryDepth = rootSegments.Length;
            }
        }

        string current = repoRoot;
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (i < boundaryDepth) continue;

            FileSystemInfo entry = i == segments.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
            if (entry.LinkTarget is not null) return false;
        }

        return File.Exists(current);
    }

    /// <summary>
    /// The path segments of a repository-relative path, accepting either separator and
    /// dropping <c>.</c>, so the repository root has none.
    /// </summary>
    private static string[] Segments(string relativePath) =>
        relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != RepositoryRoot)
            .ToArray();

    /// <summary>
    /// Lists one directory, yields its matching files, and recurses into its subdirectories,
    /// skipping every entry that is itself a symbolic link.
    /// </summary>
    /// <remarks>
    /// The listing is materialized before recursing so no directory handle stays open while
    /// a subtree is walked.
    /// </remarks>
    private static IEnumerable<string> EnumerateContainedFilesRecursive(string directory, string searchPattern)
    {
        DirectoryInfo dirInfo = new DirectoryInfo(directory);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            // The SearchOption overload this walk replaced skipped no attributes. Unix marks
            // every dot-prefixed name Hidden, so the default would drop .github/ and
            // .kyber-weave/ from a root of "." while containing nothing: LinkTarget does that.
            AttributesToSkip = 0
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

    /// <summary>
    /// Matches <paramref name="fileName"/> against <c>*</c>, a <c>*.ext</c> pattern, or an
    /// exact name, case-insensitively on Windows only.
    /// </summary>
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
