namespace KyberWeave.Core.Docs.Mcp;

/// <summary>Which segment of a configuration path stopped the guard, and why.</summary>
internal enum McpLinkObstruction
{
    /// <summary>Every segment is a real file or directory, so the path may be read or written.</summary>
    None,

    /// <summary>A segment is a symbolic link, so the bytes would land where the link points.</summary>
    Link,

    /// <summary>A segment could not be inspected, so "absent" cannot be told from "denied".</summary>
    Denied
}

/// <summary>
/// The first segment of a configuration path the guard will not read through.
/// </summary>
/// <param name="Obstruction">Which of the two reasons applies, or none.</param>
/// <param name="Segment">
/// The repository-relative segment, with <c>/</c> separators, or null when nothing stopped the walk.
/// </param>
internal readonly record struct McpLinkProbe(McpLinkObstruction Obstruction, string? Segment);

/// <summary>
/// Finds the symbolic link, if any, that stands between a repository root and one of its
/// configuration files.
/// </summary>
/// <remarks>
/// <para>
/// Requirement 2.6 confines this feature to the repository and 5.2 says a write lands in a
/// repository-scope file or nowhere, but a link defeats both without announcing itself: the path is
/// inside the tree, so every containment check passes, and the bytes land wherever the link points.
/// The case is a developer sharing one MCP configuration across checkouts —
/// <c>ln -s ~/.mcp.json .mcp.json</c> — which turns a repository-scope write into a write to the
/// user scope, the exact boundary ADR 0051 draws.
/// </para>
/// <para>
/// The check walks every segment below the root rather than testing only the file, because
/// <c>.vscode</c> can be the link and the file behind it perfectly ordinary. Each segment is tested
/// with <see cref="FileSystemInfo.LinkTarget"/> and never <see cref="FileSystemInfo.ResolveLinkTarget"/>:
/// resolving opens the link, which is the read this guard exists to refuse. The idiom is the one
/// <c>DocsRootPath.IsContainedFile</c> and <c>ManagedGlossaryService.RejectLinkedPath</c> already
/// follow, and it is the reason this is a small type of its own: the read path, the write path and
/// the removal path must agree about what a link is, and three copies of that answer is three
/// answers waiting to differ.
/// </para>
/// </remarks>
internal static class McpConfigurationLink
{
    /// <summary>
    /// The repository-relative path of the first segment of <paramref name="absolutePath"/> that is
    /// a symbolic link, or null when the whole path is made of real files and directories.
    /// </summary>
    /// <param name="repositoryRoot">The repository whose boundary the path must stay inside.</param>
    /// <param name="absolutePath">The configuration file's absolute path.</param>
    /// <remarks>
    /// A path that resolves outside the root is not this guard's question and answers null: the
    /// catalog's own ZCode walk is the one place a parent directory is read deliberately, and it
    /// reports what it finds rather than writing there.
    /// </remarks>
    public static string? LinkedSegment(string repositoryRoot, string absolutePath) =>
        Probe(repositoryRoot, absolutePath) is { Obstruction: McpLinkObstruction.Link } probe
            ? probe.Segment
            : null;

    /// <summary>
    /// The first segment of <paramref name="absolutePath"/> this guard will not read through, and
    /// which of the two reasons it is.
    /// </summary>
    /// <param name="repositoryRoot">The repository whose boundary the path must stay inside.</param>
    /// <param name="absolutePath">The configuration file's absolute path.</param>
    /// <remarks>
    /// The parent reference is recognised as a whole path segment, never as a leading pair of
    /// characters: <c>..foo</c> is the name of a file beside the repository's own, and treating it as
    /// "outside the root" answered "no link" for a path that was one — the check, the repair and the
    /// removal would all then read and write through it.
    /// </remarks>
    public static McpLinkProbe Probe(string repositoryRoot, string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        string relative = Path.GetRelativePath(repositoryRoot, absolutePath);
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        if (Path.IsPathRooted(relative) || segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
            return default;

        string current = repositoryRoot;
        string traversed = string.Empty;

        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            traversed = traversed.Length == 0 ? segment : traversed + "/" + segment;

            if (IsLink(current))
                return new McpLinkProbe(McpLinkObstruction.Link, traversed);
        }

        return default;
    }

    /// <summary>
    /// The clause a refusal opens with: which file, and whether it is the link or sits behind one.
    /// </summary>
    /// <param name="relativePath">The configuration file, as the operator's repository names it.</param>
    /// <param name="linkedSegment">The linked segment <see cref="LinkedSegment"/> found.</param>
    /// <remarks>
    /// Shared so the check, the repair and the removal tell the operator the same thing about the
    /// same file. A diagnostic that says only "inside your repository" is worse than none: what
    /// happened is that a file outside it was in reach.
    /// </remarks>
    public static string Describe(string relativePath, string linkedSegment) =>
        string.Equals(linkedSegment, relativePath, StringComparison.OrdinalIgnoreCase)
            ? $"{relativePath} is a symbolic link"
            : $"{relativePath} lies behind the symbolic link '{linkedSegment}'";

    /// <summary>
    /// Whether one path component is a symbolic link or a reparse point. A component that does not
    /// exist is not a link, so a file the repair is about to create is not refused.
    /// </summary>
    /// <remarks>
    /// A component that cannot be inspected answers "not a link" rather than throwing: one
    /// permission error in a repository's tree must not abort a <c>docs init</c>, and the read or
    /// write that follows reports the real problem with the file it names.
    /// </remarks>
    private static bool IsLink(string path)
    {
        FileSystemInfo entry = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        try
        {
            // LinkTarget answers even for a link whose target is gone, which is the one case a
            // File.Exists pre-check would have hidden.
            return entry.LinkTarget is not null
                || (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception exception) when (exception is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
