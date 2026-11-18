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
    ///     The parent listings one operation may reuse. Null outside a scope, so the ordinary answer is always read
    ///     from the file system.
    /// </summary>
    private static readonly AsyncLocal<Dictionary<string, FileSystemEntry[]>?> ScopedListings = new();

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
    ///     the cache dies with the returned scope, so no answer outlives the operation that read it. A caller must
    ///     not mutate the tree while the scope is open.
    /// </summary>
    /// <returns>The scope that restores the previous listing cache when it is disposed.</returns>
    internal static IDisposable BeginListingScope()
    {
        Dictionary<string, FileSystemEntry[]>? prior = ScopedListings.Value;
        ScopedListings.Value = new Dictionary<string, FileSystemEntry[]>(StringComparer.Ordinal);
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
        string normalizedRoot = NormalizeLexical(root, nameof(root));
        string normalizedCandidate = NormalizeLexical(candidate, nameof(candidate));
        if (string.Equals(normalizedRoot, normalizedCandidate, _nameComparison))
        {
            return false;
        }

        if (HasPathPrefix(normalizedRoot, normalizedCandidate) && !ContainsLink(normalizedCandidate))
        {
            return true;
        }

        if (HasPathPrefix(ResolveLinks(normalizedRoot), ResolveLinks(normalizedCandidate)))
        {
            return true;
        }

        return HasPathPrefix(Canonicalize(normalizedRoot), Canonicalize(normalizedCandidate));
    }

    /// <inheritdoc />
    public string Canonicalize(string path)
    {
        string fullPath = NormalizeLexical(path, nameof(path));
        string filesystemRoot = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException(
                $"The path '{path}' does not have a filesystem root.",
                nameof(path));
        string current = filesystemRoot;
        string[] segments = Segments(fullPath, filesystemRoot);

        for (int index = 0; index < segments.Length; index++)
        {
            FileSystemEntry? actualEntry = ResolveActualEntry(current, segments[index]);
            if (actualEntry is null)
            {
                for (; index < segments.Length; index++)
                {
                    current = Path.Combine(current, segments[index]);
                }

                break;
            }

            current = actualEntry.Value.Path;
            if (!actualEntry.Value.IsLink)
            {
                continue;
            }

            string? target = _files.GetFinalLinkTarget(current);
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
        string fullPath = NormalizeLexical(targetRoot, nameof(targetRoot));
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

        Dictionary<string, FileSystemEntry[]>? scope = ScopedListings.Value;
        if (scope is not null)
        {
            return ResolveFromEntries(parentPath, ListingsFor(scope, parentPath), segment);
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
        string filesystemRoot = Path.GetPathRoot(path)
            ?? throw new ArgumentException(
                $"The path '{path}' does not have a filesystem root.",
                nameof(path));
        string current = filesystemRoot;
        foreach (string segment in Segments(path, filesystemRoot))
        {
            current = Path.Combine(current, segment);
            string? target = _files.GetFinalLinkTarget(current);
            if (target is not null)
            {
                current = ResolveLinks(NormalizeLexical(target, nameof(path)));
            }
        }

        return NormalizeLexical(current, nameof(path));
    }

    private bool HasPathPrefix(string root, string candidate)
    {
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, _nameComparison);
    }

    private static string[] Segments(string fullPath, string filesystemRoot) =>
        fullPath[filesystemRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeLexical(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                $"'{path}' is not a fully qualified path; resolve it against an explicit base directory with " +
                $"{nameof(GetFullPath)}.",
                parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Normalize(NormalizationForm.FormC);
    }

    private sealed class ListingScope(Dictionary<string, FileSystemEntry[]>? prior) : IDisposable
    {
        public void Dispose() => ScopedListings.Value = prior;
    }
}
