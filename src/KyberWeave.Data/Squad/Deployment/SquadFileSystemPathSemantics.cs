using System.Text;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Centralizes deployment-path identity and containment using the spelling and aliases exposed by
/// the containing filesystem rather than an operating-system-wide case rule.
/// </summary>
public sealed class SquadFileSystemPathSemantics : IFileSystemPathSemantics
{
    /// <summary>The stateless filesystem answer, shared by the adapters that need one.</summary>
    public static SquadFileSystemPathSemantics Instance { get; } = new();

    /// <summary>
    ///     The parent listings one operation may reuse. Null outside a scope, so the ordinary answer is
    ///     always read from the filesystem.
    /// </summary>
    private static readonly AsyncLocal<Dictionary<string, FileSystemInfo[]>?> ScopedListings = new();

    /// <summary>
    ///     Reuses the listings a preflight reads while it inspects a tree it does not change. Scoped to
    ///     the caller: the cache dies with the returned scope, so no answer outlives the operation that
    ///     read it. A caller must not mutate the tree while the scope is open — the preflight plan
    ///     builders are the only callers, and they only read.
    /// </summary>
    /// <returns>The scope that restores the previous listing cache when it is disposed.</returns>
    internal static IDisposable BeginListingScope()
    {
        Dictionary<string, FileSystemInfo[]>? prior = ScopedListings.Value;
        ScopedListings.Value = new Dictionary<string, FileSystemInfo[]>(StringComparer.Ordinal);
        return new ListingScope(prior);
    }

    /// <summary>The canonical physical path of an existing deployment root.</summary>
    /// <param name="targetRoot">The directory a deployment is bound to.</param>
    /// <returns>The canonical spelling of the directory.</returns>
    /// <exception cref="SquadDeploymentConflictException">
    ///     The root is not an existing directory, or it changed while its physical path was resolved.
    /// </exception>
    public string ResolvePhysicalRoot(string targetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        string fullPath = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(fullPath))
        {
            throw new SquadDeploymentConflictException(
                $"Squad target root '{targetRoot}' must be an existing directory.");
        }

        string physicalPath = Canonicalize(fullPath);
        if (!Directory.Exists(physicalPath))
        {
            throw new SquadDeploymentConflictException(
                $"Squad target root '{targetRoot}' changed during physical resolution.");
        }

        return physicalPath;
    }

    public bool AreSame(string first, string second)
    {
        string normalizedFirst = NormalizeLexical(first);
        string normalizedSecond = NormalizeLexical(second);
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.Ordinal);
    }

    public bool IsWithin(string root, string candidate)
    {
        string normalizedRoot = NormalizeLexical(root);
        string normalizedCandidate = NormalizeLexical(candidate);
        if (HasPathPrefix(normalizedRoot, normalizedCandidate) &&
            !ContainsReparsePoint(normalizedCandidate))
        {
            return true;
        }

        string resolvedRoot = ResolveAliases(normalizedRoot);
        string resolvedCandidate = ResolveAliases(normalizedCandidate);
        if (HasPathPrefix(resolvedRoot, resolvedCandidate))
            return true;

        string canonicalRoot = Canonicalize(root);
        string canonicalCandidate = Canonicalize(candidate);
        return HasPathPrefix(canonicalRoot, canonicalCandidate);
    }

    public string Canonicalize(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string filesystemRoot = Path.GetPathRoot(fullPath)
            ?? throw new SquadPathContainmentException(
                $"Squad path '{path}' does not have a filesystem root.");
        string current = filesystemRoot;
        string[] segments = fullPath[filesystemRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            FileSystemInfo? actualEntry = ResolveActualEntry(current, segment);
            if (actualEntry is null)
            {
                for (; index < segments.Length; index++)
                    current = Path.Combine(current, segments[index]);
                break;
            }

            current = actualEntry.FullName;
            if ((actualEntry.Attributes & FileAttributes.ReparsePoint) == 0)
                continue;

            FileSystemInfo? target = actualEntry.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = Canonicalize(target.FullName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current))
            .Normalize(NormalizationForm.FormC);
    }

    private static FileSystemInfo? ResolveActualEntry(string parentPath, string segment)
    {
        if (!Directory.Exists(parentPath))
            return null;

        Dictionary<string, FileSystemInfo[]>? scope = ScopedListings.Value;
        if (scope is not null)
            return ResolveFromEntries(parentPath, ListingsFor(scope, parentPath), segment);

        // Outside a scope the walk stops as soon as the exact spelling is found, so a directory with
        // thousands of entries is not materialised when the caller's spelling is already the disk's.
        foreach (FileSystemInfo entry in new DirectoryInfo(parentPath).EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.Name, segment, StringComparison.Ordinal))
                return entry;
        }

        string lexicalCandidate = Path.Combine(parentPath, segment);
        if (!Directory.Exists(lexicalCandidate) && !File.Exists(lexicalCandidate))
            return null;

        return ResolveFromEntries(
            parentPath,
            new DirectoryInfo(parentPath).EnumerateFileSystemInfos().ToArray(),
            segment);
    }

    private static FileSystemInfo[] ListingsFor(
        Dictionary<string, FileSystemInfo[]> scope,
        string parentPath)
    {
        if (scope.TryGetValue(parentPath, out FileSystemInfo[]? entries))
            return entries;

        entries = new DirectoryInfo(parentPath).EnumerateFileSystemInfos().ToArray();
        scope[parentPath] = entries;
        return entries;
    }

    private static FileSystemInfo? ResolveFromEntries(
        string parentPath,
        FileSystemInfo[] entries,
        string segment)
    {
        foreach (FileSystemInfo entry in entries)
        {
            if (string.Equals(entry.Name, segment, StringComparison.Ordinal))
                return entry;
        }

        string lexicalCandidate = Path.Combine(parentPath, segment);
        if (!Directory.Exists(lexicalCandidate) && !File.Exists(lexicalCandidate))
            return null;

        FileSystemInfo[] aliases = entries.Where(entry =>
                string.Equals(entry.Name, segment, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return aliases.Length == 1 ? aliases[0] : null;
    }

    private sealed class ListingScope(Dictionary<string, FileSystemInfo[]>? prior) : IDisposable
    {
        public void Dispose() => ScopedListings.Value = prior;
    }

    private static bool HasPathPrefix(string root, string candidate)
    {
        string prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool ContainsReparsePoint(string path)
    {
        string? filesystemRoot = Path.GetPathRoot(path);
        if (filesystemRoot is null)
            return false;

        string current = filesystemRoot;
        string[] segments = path[filesystemRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
        }

        return false;
    }

    private static string ResolveAliases(string path)
    {
        string filesystemRoot = Path.GetPathRoot(path)
            ?? throw new SquadPathContainmentException(
                $"Squad path '{path}' does not have a filesystem root.");
        string current = filesystemRoot;
        string[] segments = path[filesystemRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                continue;

            FileSystemInfo? target = entry.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = ResolveAliases(target.FullName);
        }

        return NormalizeLexical(current);
    }

    private static string NormalizeLexical(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .Normalize(NormalizationForm.FormC);
}
