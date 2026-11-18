using System.Text;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Paths;

/// <summary>
/// The path seam answered from a file system: every spelling question is decided by the entries the file system
/// reports, so case folding and symbolic links follow the volume that holds the path rather than a rule for the
/// operating system as a whole.
/// </summary>
/// <remarks>
/// <para>
/// No member reads the process working directory: a relative path is either resolved against the base the caller
/// passes to <see cref="GetFullPath" /> or refused.
/// </para>
/// <para>
/// A preflight that reads one tree many times can wrap its work in <see cref="BeginListingScope" />, which caches
/// the parent listings the canonicalization walk reads for the length of that scope.
/// </para>
/// </remarks>
public class FileSystemPathResolver : IPathResolver
{
    /// <summary>
    ///     The parent listings one operation may reuse, keyed by the file system that answered them so two resolvers
    ///     in the same flow never trade entries. Null outside a scope, so the ordinary answer is always read from the
    ///     file system.
    /// </summary>
    private static readonly AsyncLocal<Dictionary<IFileSystem, Dictionary<string, FileSystemEntry[]>>?>
        ScopedListings = new();

    private readonly IFileSystem _files;
    private readonly StringComparison _nameComparison;

    /// <summary>Creates the resolver.</summary>
    /// <param name="files">The file system whose entries decide every answer. Nothing here touches the disk directly.</param>
    /// <param name="caseSensitiveFileSystem">
    /// True when the file system compares names case-sensitively, as ext4 does; false for the case-insensitive,
    /// case-preserving APFS and NTFS behaviour.
    /// </param>
    public FileSystemPathResolver(IFileSystem files, bool caseSensitiveFileSystem)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _nameComparison = caseSensitiveFileSystem ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    /// <summary>
    ///     Reuses the listings a preflight reads while it inspects a tree it does not change. Scoped to the caller:
    ///     the cache dies with the returned scope, so no answer outlives the operation that read it, and each file
    ///     system keeps its own entries. A caller must not mutate the tree while the scope is open.
    /// </summary>
    /// <returns>The scope that restores the previous listing cache when it is disposed.</returns>
    internal static IDisposable BeginListingScope()
    {
        Dictionary<IFileSystem, Dictionary<string, FileSystemEntry[]>>? prior = ScopedListings.Value;
        ScopedListings.Value = new Dictionary<IFileSystem, Dictionary<string, FileSystemEntry[]>>(
            ReferenceEqualityComparer.Instance);
        return new ListingScope(prior);
    }

    /// <inheritdoc />
    public string GetFullPath(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathFullyQualified(baseDirectory))
        {
            throw new ArgumentException(
                $"The base directory '{baseDirectory}' must be a fully qualified path.",
                nameof(baseDirectory));
        }

        return NormalizeLexical(Path.GetFullPath(path, baseDirectory), nameof(path));
    }

    /// <inheritdoc />
    public bool AreSame(string first, string second) =>
        string.Equals(
            NormalizeLexical(first, nameof(first)),
            NormalizeLexical(second, nameof(second)),
            _nameComparison);

    /// <inheritdoc />
    public bool IsWithin(string root, string candidate)
    {
        string normalizedRoot = RequireFullyQualified(root, nameof(root));
        string normalizedCandidate = RequireFullyQualified(candidate, nameof(candidate));
        if (string.Equals(normalizedRoot, normalizedCandidate, _nameComparison))
        {
            return false;
        }

        // A parent segment climbs from wherever it appears, so it may only be collapsed once the links before it are
        // resolved: "link/.." is the parent of what the link points at, not the directory the link sits in.
        if (!HasParentSegment(normalizedRoot) &&
            !HasParentSegment(normalizedCandidate) &&
            HasPathPrefix(normalizedRoot, normalizedCandidate) &&
            !ContainsLink(normalizedCandidate))
        {
            return true;
        }

        if (HasPathPrefix(ResolveLinks(normalizedRoot), ResolveLinks(normalizedCandidate)))
        {
            return true;
        }

        string canonicalRoot = Canonicalize(normalizedRoot);
        string canonicalCandidate = Canonicalize(normalizedCandidate);
        return HasPathPrefix(canonicalRoot, canonicalCandidate) ||
               FoldsBelowTheDeepestEntry(canonicalRoot, canonicalCandidate);
    }

    /// <summary>
    ///     Whether two canonical spellings that agree only by the volume's case rule name one path because nothing
    ///     exists at the segment where they differ.
    /// </summary>
    /// <remarks>
    ///     Folding a segment that names an entry is what lets a link into a case-distinct sibling look contained, so
    ///     the rule here is narrower: the two spellings fold, and neither of them names an entry in the parent. No
    ///     directory can hold a name below a segment that does not exist, so the rest of the candidate cannot be
    ///     anything but the tail a deployment is about to create.
    /// </remarks>
    private bool FoldsBelowTheDeepestEntry(string canonicalRoot, string canonicalCandidate)
    {
        string filesystemRoot = FilesystemRoot(canonicalRoot, nameof(canonicalRoot));
        string[] rootSegments = Segments(canonicalRoot, filesystemRoot);
        string[] candidateSegments = Segments(canonicalCandidate, filesystemRoot);
        if (candidateSegments.Length <= rootSegments.Length)
        {
            return false;
        }

        string parent = filesystemRoot;
        for (int index = 0; index < rootSegments.Length; index++)
        {
            string rootSegment = rootSegments[index];
            string candidateSegment = candidateSegments[index];
            if (string.Equals(rootSegment, candidateSegment, StringComparison.Ordinal))
            {
                parent = Path.Combine(parent, rootSegment);
                continue;
            }

            return string.Equals(rootSegment, candidateSegment, _nameComparison) &&
                   ResolveActualEntry(parent, rootSegment) is null &&
                   ResolveActualEntry(parent, candidateSegment) is null;
        }

        // The loop left no segment to differ at, so the exact comparison above already answered.
        return false;
    }

    /// <inheritdoc />
    public string Canonicalize(string path)
    {
        string fullPath = RequireFullyQualified(path, nameof(path));
        string filesystemRoot = FilesystemRoot(fullPath, path);
        string current = filesystemRoot;
        string[] segments = Segments(fullPath, filesystemRoot);

        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (IsCurrentDirectory(segment))
            {
                continue;
            }

            if (IsParentDirectory(segment))
            {
                current = ParentOf(current, filesystemRoot);
                continue;
            }

            FileSystemEntry? actualEntry = ResolveActualEntry(current, segment);
            if (actualEntry is null)
            {
                for (; index < segments.Length; index++)
                {
                    current = WalkInto(current, segments[index], filesystemRoot);
                }

                break;
            }

            current = actualEntry.Value.Path;
            if (!actualEntry.Value.IsLink)
            {
                continue;
            }

            string? target = FinalTargetOf(current);
            if (target is not null)
            {
                current = Canonicalize(target);
            }
        }

        return NormalizeLexical(current, nameof(path));
    }

    /// <inheritdoc />
    public string ResolvePhysicalRoot(string targetRoot)
    {
        string fullPath = Canonicalize(targetRoot);
        if (!_files.DirectoryExists(fullPath))
        {
            throw new DirectoryNotFoundException($"'{targetRoot}' must be an existing directory.");
        }

        string physicalPath = Canonicalize(fullPath);
        if (!_files.DirectoryExists(physicalPath))
        {
            throw new DirectoryNotFoundException($"'{targetRoot}' changed during physical resolution.");
        }

        return physicalPath;
    }

    private FileSystemEntry? ResolveActualEntry(string parentPath, string segment)
    {
        if (!_files.DirectoryExists(parentPath))
        {
            return null;
        }

        Dictionary<IFileSystem, Dictionary<string, FileSystemEntry[]>>? scoped = ScopedListings.Value;
        if (scoped is not null)
        {
            if (!scoped.TryGetValue(_files, out Dictionary<string, FileSystemEntry[]>? listings))
            {
                listings = new Dictionary<string, FileSystemEntry[]>(StringComparer.Ordinal);
                scoped[_files] = listings;
            }

            return ResolveFromEntries(parentPath, ListingsFor(listings, parentPath), segment);
        }

        // Outside a scope the walk stops as soon as the exact spelling is found, so a directory with thousands of
        // entries is not materialized when the caller's spelling is already the disk's.
        foreach (FileSystemEntry entry in _files.EnumerateEntries(parentPath))
        {
            if (string.Equals(Path.GetFileName(entry.Path), segment, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return ExistsLexically(parentPath, segment)
            ? ResolveFromEntries(parentPath, [.. _files.EnumerateEntries(parentPath)], segment)
            : null;
    }

    private FileSystemEntry[] ListingsFor(Dictionary<string, FileSystemEntry[]> scope, string parentPath)
    {
        if (scope.TryGetValue(parentPath, out FileSystemEntry[]? entries))
        {
            return entries;
        }

        entries = [.. _files.EnumerateEntries(parentPath)];
        scope[parentPath] = entries;
        return entries;
    }

    private FileSystemEntry? ResolveFromEntries(string parentPath, FileSystemEntry[] entries, string segment)
    {
        foreach (FileSystemEntry entry in entries)
        {
            if (string.Equals(Path.GetFileName(entry.Path), segment, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        if (!ExistsLexically(parentPath, segment))
        {
            return null;
        }

        FileSystemEntry[] aliases = entries
            .Where(entry => string.Equals(Path.GetFileName(entry.Path), segment, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return aliases.Length == 1 ? aliases[0] : null;
    }

    private bool ExistsLexically(string parentPath, string name)
    {
        string candidate = Path.Combine(parentPath, name);
        return _files.DirectoryExists(candidate) || _files.FileExists(candidate);
    }

    /// <summary>
    ///     Whether any segment of the path is a link. A path the process cannot read the attributes of counts as
    ///     link-free, so the lexical answer stands and the caller learns the real problem when it touches the path.
    /// </summary>
    private bool ContainsLink(string path)
    {
        string? filesystemRoot = Path.GetPathRoot(path);
        if (filesystemRoot is null)
        {
            return false;
        }

        string current = filesystemRoot;
        foreach (string segment in Segments(path, filesystemRoot))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((_files.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Rewrites every segment that is a link to the segment its final target names.</summary>
    private string ResolveLinks(string path)
    {
        string filesystemRoot = FilesystemRoot(path, path);
        string current = filesystemRoot;
        foreach (string segment in Segments(path, filesystemRoot))
        {
            if (IsCurrentDirectory(segment))
            {
                continue;
            }

            if (IsParentDirectory(segment))
            {
                current = ParentOf(current, filesystemRoot);
                continue;
            }

            current = Path.Combine(current, segment);
            string? target = FinalTargetOf(current);
            if (target is not null)
            {
                current = ResolveLinks(NormalizeLexical(target, nameof(path)));
            }
        }

        return NormalizeLexical(current, nameof(path));
    }

    private static string FilesystemRoot(string fullPath, string path) =>
        Path.GetPathRoot(fullPath)
        ?? throw new ArgumentException(
            $"The path '{path}' does not have a filesystem root.",
            nameof(path));

    private static bool IsCurrentDirectory(string segment) =>
        segment is "." or "";

    private static bool IsParentDirectory(string segment) => segment == "..";

    /// <summary>
    ///     The parent of a directory the walk has already resolved. It is the real parent even when the directory was
    ///     reached through a link, because the walk replaced the link with its target before it arrived here.
    /// </summary>
    private static string ParentOf(string directory, string filesystemRoot) =>
        Path.GetDirectoryName(directory) ?? filesystemRoot;

    /// <summary>
    ///     Descends one segment without reading the file system. A parent segment still climbs from the resolved
    ///     directory, so the tail of a path that does not exist yet keeps climbing the same way the prefix did.
    /// </summary>
    private static string WalkInto(string directory, string segment, string filesystemRoot)
    {
        if (IsCurrentDirectory(segment))
        {
            return directory;
        }

        return IsParentDirectory(segment) ? ParentOf(directory, filesystemRoot) : Path.Combine(directory, segment);
    }

    private static bool HasParentSegment(string path)
    {
        string? filesystemRoot = Path.GetPathRoot(path);
        if (filesystemRoot is null)
        {
            return false;
        }

        foreach (string segment in Segments(path, filesystemRoot))
        {
            if (IsParentDirectory(segment))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The final target of a link, or null when the file system will not name one: a target that is gone, or a
    ///     chain that cycles. A cycle keeps the link's own spelling, so the answer does not depend on which adapter
    ///     was asked.
    /// </summary>
    private string? FinalTargetOf(string path)
    {
        try
        {
            return _files.GetFinalLinkTarget(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private bool HasPathPrefix(string root, string candidate)
    {
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        // Exact comparison, because every caller compares spellings the walk already resolved: each existing
        // segment carries the name its directory reported, and a directory that holds two names differing only in
        // case reports both, so folding them together here would place one sibling inside the other. A spelling the
        // volume does fold reaches this point as the entry's own name through Canonicalize.
        return candidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string[] Segments(string fullPath, string filesystemRoot) =>
        fullPath[filesystemRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    ///     Normalizes a path that names no link segment left to resolve: any parent segment that remains is collapsed
    ///     lexically, the trailing separator is trimmed and the spelling is normalized to Unicode form C.
    /// </summary>
    private static string NormalizeLexical(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(NotFullyQualified(path), parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    ///     Refuses a relative path and returns a fully qualified one with its trailing separator trimmed and its
    ///     spelling normalized to Unicode form C. Parent segments are left in place, because collapsing one before
    ///     the links before it are resolved would move the path somewhere it does not go.
    /// </summary>
    private static string RequireFullyQualified(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(NotFullyQualified(path), parameterName);
        }

        return Path.TrimEndingDirectorySeparator(path).Normalize(NormalizationForm.FormC);
    }

    private static string NotFullyQualified(string path) =>
        $"'{path}' is not a fully qualified path; resolve it against an explicit base directory with " +
        $"{nameof(GetFullPath)}.";

    private sealed class ListingScope(
        Dictionary<IFileSystem, Dictionary<string, FileSystemEntry[]>>? prior) : IDisposable
    {
        public void Dispose() => ScopedListings.Value = prior;
    }
}
