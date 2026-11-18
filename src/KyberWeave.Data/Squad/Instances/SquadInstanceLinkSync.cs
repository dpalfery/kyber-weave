using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Synchronizes directory and file links from a main harness home directory into an instance root directory,
///     preventing drift, preserving unmanaged files, and detecting broken or split links.
/// </summary>
/// <remarks>
///     <para>
///         Instances running in <see cref="SquadInstanceKind.HomeReplace" /> mode share settings, history,
///         and credentials from the main home directory through filesystem links without copying.
///     </para>
///     <para>
///         When a harness performs an atomic rewrite of a linked file (breaking the link via rename),
///         the split detection identifies whether the split file's bytes still match the main home copy.
///         If they match, the link is re-established; if they differ, both copies are preserved and a warning is raised.
///     </para>
/// </remarks>
/// <param name="files">Reads, writes and lists the instance, main-home and state files.</param>
/// <param name="paths">Canonicalizes and resolves the instance root.</param>
/// <param name="lease">Serializes concurrent syncs of one instance root.</param>
/// <param name="environment">Supplies the base for a relative root and the operating system's case rule.</param>
public sealed class SquadInstanceLinkSync(
    IWritableFileSystem files,
    IPathResolver paths,
    ILease lease,
    IProcessEnvironment environment) : ISquadInstanceLinkSync
{
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);

    // The lease refuses a second request from the process that holds the name, so concurrent callers in this
    // process queue here first and one at a time reach the cross-process lease.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, true) }
    };

    /// <summary>
    ///     Computes the unique named mutex name for the specified instance root.
    /// </summary>
    /// <param name="instanceRoot">The filesystem root of the instance.</param>
    /// <returns>A mutex name starting with <c>kyber-weave-squad-</c> followed by the canonical root key.</returns>
    public string GetMutexName(string instanceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);
        string canonical = paths.Canonicalize(instanceRoot);
        return SquadLeaseNames.ForRoot(SquadPhysicalRootIdentity.KeyOf(canonical));
    }

    /// <summary>
    ///     Synchronizes filesystem links from <paramref name="mainHome" /> into <paramref name="instanceRoot" />.
    /// </summary>
    /// <param name="instanceRoot">The root directory of the harness instance.</param>
    /// <param name="mainHome">The canonical main home directory of the harness.</param>
    /// <param name="row">The catalog row defining instance kind, rules, and exclusions.</param>
    /// <param name="renderedPaths">Paths directly rendered by Squad that must never be linked.</param>
    /// <param name="fileSystem">Filesystem abstraction for creating and inspecting links.</param>
    /// <param name="instanceStateDirectory">Directory where instance state, including <c>links.json</c>, is recorded.</param>
    /// <returns>Result containing active links and any generated warnings.</returns>
    public SquadInstanceLinkSyncResult Sync(
        string instanceRoot,
        string mainHome,
        SquadInstanceRow row,
        IReadOnlySet<string> renderedPaths,
        ISquadLinkFileSystem fileSystem,
        string instanceStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainHome);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(renderedPaths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceStateDirectory);

        if (row.Kind != SquadInstanceKind.HomeReplace) return new SquadInstanceLinkSyncResult([], []);

        string leaseName = GetMutexName(instanceRoot);
        SemaphoreSlim gate = _gates.GetOrAdd(leaseName, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            using IDisposable held = lease.Acquire(leaseName, LeaseTimeout);
            return SynchronizeCore(
                instanceRoot,
                mainHome,
                row,
                renderedPaths,
                fileSystem,
                instanceStateDirectory);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private SquadInstanceLinkSyncResult SynchronizeCore(
        string instanceRoot,
        string mainHome,
        SquadInstanceRow row,
        IReadOnlySet<string> renderedPaths,
        ISquadLinkFileSystem fileSystem,
        string instanceStateDirectory)
    {
        List<SquadLinkRecord> recordedLinks = ReadRecordedLinks(instanceStateDirectory);
        ValidateRecordedLinks(instanceRoot, recordedLinks);

        List<SquadLinkRecord> mainListing = EnumerateMainHome(mainHome, renderedPaths, row.ExtraLinks);
        List<string> instanceListing = EnumerateInstanceRoot(instanceRoot);

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        // 1. Apply Remove actions for stale recorded links whose target in main home no longer exists
        List<SquadLinkRecord> retainedStaleLinks = [];
        List<string> warnings = [];

        foreach (SquadLinkAction.Remove remove in actions.OfType<SquadLinkAction.Remove>())
        {
            string linkPath = Path.Combine(instanceRoot, remove.Record.RelativePath);
            try
            {
                if (remove.Record.Kind == SquadLinkKind.Directory)
                {
                    if (files.DirectoryExists(linkPath) || fileSystem.IsLink(linkPath))
                        fileSystem.DeleteTreeNoFollow(linkPath);
                }
                else
                {
                    if (files.FileExists(linkPath) || fileSystem.IsLink(linkPath))
                        files.DeleteFile(linkPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Failed to remove stale link '{remove.Record.RelativePath}': {ex.Message}");
                retainedStaleLinks.Add(remove.Record);
            }
        }

        // 2. Record warnings for unmanaged instance copies kept by planner
        HashSet<string> keptPaths = new(StringComparer.Ordinal);

        foreach (SquadLinkAction.Keep keep in actions.OfType<SquadLinkAction.Keep>())
        {
            keptPaths.Add(NormalizeRelativePath(keep.RelativePath));
            warnings.Add($"Instance copy kept for '{keep.RelativePath}': {keep.Warning}");
        }

        // 3. Split link check for recorded file links
        HashSet<string> differingSplitPaths = new(StringComparer.Ordinal);

        foreach (SquadLinkRecord recorded in recordedLinks)
        {
            if (recorded.Kind != SquadLinkKind.File) continue;

            string linkPath = Path.Combine(instanceRoot, recorded.RelativePath);
            string targetPath = recorded.TargetPath;

            if (!fileSystem.IsLinkTo(linkPath, targetPath))
                if (files.FileExists(linkPath) && files.FileExists(targetPath))
                {
                    if (FilesAreEqual(linkPath, targetPath))
                    {
                        try
                        {
                            files.DeleteFile(linkPath);
                            fileSystem.CreateFileLink(linkPath, targetPath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                       or SquadLinkException)
                        {
                            warnings.Add($"Failed to re-link split file '{recorded.RelativePath}': {ex.Message}");
                        }
                    }
                    else
                    {
                        differingSplitPaths.Add(NormalizeRelativePath(recorded.RelativePath));
                        warnings.Add($"Split link differing for '{recorded.RelativePath}'. Both copies kept.");
                    }
                }
        }

        // 4. Apply Add actions
        List<SquadLinkRecord> activeLinks = [];

        foreach (SquadLinkAction.Add add in actions.OfType<SquadLinkAction.Add>())
        {
            SquadLinkRecord record = add.Record;
            string normRelative = NormalizeRelativePath(record.RelativePath);

            if (differingSplitPaths.Contains(normRelative)) continue;

            if (keptPaths.Contains(normRelative)) continue;

            string linkPath = Path.Combine(instanceRoot, record.RelativePath);
            string targetPath = record.TargetPath;

            if (fileSystem.IsLinkTo(linkPath, targetPath))
            {
                activeLinks.Add(record);
                continue;
            }

            if (record.Kind == SquadLinkKind.Directory)
                try
                {
                    if (files.DirectoryExists(linkPath) || fileSystem.IsLink(linkPath))
                        fileSystem.DeleteTreeNoFollow(linkPath);

                    EnsureDirectoryExists(Path.GetDirectoryName(linkPath));
                    fileSystem.CreateFolderLink(linkPath, targetPath);
                    activeLinks.Add(record);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SquadLinkException)
                {
                    warnings.Add($"Failed to create directory link for '{record.RelativePath}': {ex.Message}");
                }
            else
                try
                {
                    if (files.FileExists(linkPath) || fileSystem.IsLink(linkPath)) files.DeleteFile(linkPath);

                    EnsureDirectoryExists(Path.GetDirectoryName(linkPath));
                    fileSystem.CreateFileLink(linkPath, targetPath);
                    activeLinks.Add(record);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SquadLinkException)
                {
                    warnings.Add($"Failed to create file link for '{record.RelativePath}': {ex.Message}");
                }
        }

        activeLinks.AddRange(retainedStaleLinks);

        List<SquadLinkRecord> sortedLinks = activeLinks
            .OrderBy(l => l.RelativePath, StringComparer.Ordinal)
            .ToList();

        WriteLinksJson(instanceStateDirectory, sortedLinks);

        return new SquadInstanceLinkSyncResult(sortedLinks, warnings);
    }

    private void ValidateRecordedLinks(string instanceRoot, IReadOnlyList<SquadLinkRecord> recordedLinks)
    {
        string canonicalRoot = FullPath(instanceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = environment.OperatingSystem == OperatingSystemKind.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (SquadLinkRecord record in recordedLinks)
        {
            if (string.IsNullOrWhiteSpace(record.RelativePath))
                throw new InvalidDataException("Recorded link relative path is empty or whitespace.");

            if (Path.IsPathRooted(record.RelativePath))
                throw new InvalidDataException($"Recorded link relative path '{record.RelativePath}' is rooted and escapes instance root.");

            string normalized = record.RelativePath.Replace('\\', '/');
            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(s => s == ".."))
                throw new InvalidDataException($"Recorded link relative path '{record.RelativePath}' contains directory traversal segments.");

            string fullPath = FullPath(Path.Combine(instanceRoot, record.RelativePath));
            if (!fullPath.StartsWith(canonicalRoot, comparison))
                throw new InvalidDataException($"Recorded link relative path '{record.RelativePath}' escapes instance root '{instanceRoot}'.");
        }
    }

    private List<SquadLinkRecord> ReadRecordedLinks(string instanceStateDirectory)
    {
        string linksJsonPath = Path.Combine(instanceStateDirectory, "links.json");
        if (!files.FileExists(linksJsonPath)) return [];

        try
        {
            string json = files.ReadAllText(linksJsonPath);
            List<SquadLinkRecord>? deserialized = JsonSerializer.Deserialize<List<SquadLinkRecord>>(json, JsonOptions);
            return deserialized ?? throw new InvalidDataException($"Corrupt instance link state file '{linksJsonPath}'.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Corrupt instance link state file '{linksJsonPath}'.", ex);
        }
    }

    private void WriteLinksJson(string instanceStateDirectory, IReadOnlyList<SquadLinkRecord> links)
    {
        EnsureDirectoryExists(instanceStateDirectory);
        string text = JsonSerializer.Serialize(links, JsonOptions) + "\n";
        string targetPath = Path.Combine(instanceStateDirectory, "links.json");
        files.WriteAllBytesAtomic(targetPath, new UTF8Encoding(false).GetBytes(text), SquadTransaction.SyncToDisk);
    }

    private List<SquadLinkRecord> EnumerateMainHome(
        string mainHome,
        IReadOnlySet<string> renderedPaths,
        IReadOnlyList<string> extraLinks)
    {
        List<SquadLinkRecord> records = [];
        if (!files.DirectoryExists(mainHome)) return records;

        HashSet<string> interestingPrefixes = new(StringComparer.Ordinal);
        foreach (string path in renderedPaths)
        {
            string norm = path.Replace('\\', '/').Trim('/');
            int slash = norm.LastIndexOf('/');
            while (slash > 0)
            {
                interestingPrefixes.Add(norm[..slash]);
                slash = norm.LastIndexOf('/', slash - 1);
            }
        }

        foreach (string extra in extraLinks)
        {
            string norm = extra.Replace('\\', '/').Trim('/');
            int slash = norm.LastIndexOf('/');
            while (slash > 0)
            {
                interestingPrefixes.Add(norm[..slash]);
                slash = norm.LastIndexOf('/', slash - 1);
            }
        }

        Queue<(string Dir, int Depth)> queue = new();
        queue.Enqueue((mainHome, 0));

        while (queue.Count > 0)
        {
            (string current, int depth) = queue.Dequeue();
            IEnumerable<FileSystemEntry> entries;
            try
            {
                entries = files.EnumerateEntries(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (FileSystemEntry entry in entries)
            {
                string relativePath = Path.GetRelativePath(mainHome, entry.Path).Replace('\\', '/');
                string normRel = relativePath.Trim('/');

                if (entry.IsDirectory)
                {
                    records.Add(new SquadLinkRecord(relativePath, SquadLinkKind.Directory, entry.Path));
                    if (depth < 10 &&
                        !entry.IsLink &&
                        interestingPrefixes.Contains(normRel))
                    {
                        queue.Enqueue((entry.Path, depth + 1));
                    }
                }
                else
                {
                    records.Add(new SquadLinkRecord(relativePath, SquadLinkKind.File, entry.Path));
                }
            }
        }

        return records;
    }

    private List<string> EnumerateInstanceRoot(string instanceRoot)
    {
        List<string> results = [];
        if (!files.DirectoryExists(instanceRoot)) return results;

        Queue<(string Dir, int Depth)> queue = new();
        queue.Enqueue((instanceRoot, 0));

        while (queue.Count > 0)
        {
            (string current, int depth) = queue.Dequeue();
            IEnumerable<FileSystemEntry> entries;
            try
            {
                entries = files.EnumerateEntries(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (FileSystemEntry entry in entries)
            {
                string relativePath = Path.GetRelativePath(instanceRoot, entry.Path).Replace('\\', '/');
                results.Add(relativePath);
                if (entry.IsDirectory && depth < 10 && !entry.IsLink)
                {
                    queue.Enqueue((entry.Path, depth + 1));
                }
            }
        }

        return results;
    }

    private bool FilesAreEqual(string path1, string path2)
    {
        try
        {
            long length = files.GetLength(path1);
            if (length != files.GetLength(path2)) return false;

            if (length == 0) return true;

            using Stream stream1 = files.OpenRead(path1);
            using Stream stream2 = files.OpenRead(path2);

            Span<byte> buffer1 = stackalloc byte[8192];
            Span<byte> buffer2 = stackalloc byte[8192];

            while (true)
            {
                int bytesRead1 = stream1.Read(buffer1);
                int bytesRead2 = stream2.Read(buffer2);

                if (bytesRead1 != bytesRead2) return false;

                if (bytesRead1 == 0) return true;

                if (!buffer1[..bytesRead1].SequenceEqual(buffer2[..bytesRead2])) return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void EnsureDirectoryExists(string? directoryPath)
    {
        if (!string.IsNullOrEmpty(directoryPath) && !files.DirectoryExists(directoryPath))
            try
            {
                files.CreateDirectory(directoryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException)
            {
            }
    }

    private string FullPath(string path) => paths.GetFullPath(path, environment.CurrentDirectory);

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }
}
