using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Result of synchronizing harness instance links with the main harness home directory.
/// </summary>
/// <param name="Links">The active symbolic and junction links currently established for this instance.</param>
/// <param name="Warnings">
///     Warnings generated during link synchronization (e.g. preserved split files or kept unmanaged
///     files).
/// </param>
public sealed record SquadInstanceLinkSyncResult(
    IReadOnlyList<SquadLinkRecord> Links,
    IReadOnlyList<string> Warnings);

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
public static class SquadInstanceLinkSync
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };

    /// <summary>
    ///     Computes the unique named mutex name for the specified instance root.
    /// </summary>
    /// <param name="instanceRoot">The filesystem root of the instance.</param>
    /// <returns>A mutex name starting with <c>kyber-weave-squad-</c> followed by the canonical root key.</returns>
    public static string GetMutexName(string instanceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);
        string canonical = SquadFileSystemPathSemantics.Canonicalize(instanceRoot);
        string rootKey = SquadPhysicalRootIdentity.KeyOf(canonical);
        return $"kyber-weave-squad-{rootKey}";
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
    public static SquadInstanceLinkSyncResult Sync(
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

        string mutexName = GetMutexName(instanceRoot);
        using Mutex mutex = new(false, mutexName);
        bool acquired = false;

        try
        {
            try
            {
                acquired = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

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
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static SquadInstanceLinkSyncResult SynchronizeCore(
        string instanceRoot,
        string mainHome,
        SquadInstanceRow row,
        IReadOnlySet<string> renderedPaths,
        ISquadLinkFileSystem fileSystem,
        string instanceStateDirectory)
    {
        List<SquadLinkRecord> recordedLinks = ReadRecordedLinks(instanceStateDirectory);
        List<SquadLinkRecord> mainListing = EnumerateMainHome(mainHome, renderedPaths, row.ExtraLinks);
        List<string> instanceListing = EnumerateInstanceRoot(instanceRoot);

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        // 1. Apply Remove actions for stale recorded links whose target in main home no longer exists
        foreach (SquadLinkAction.Remove remove in actions.OfType<SquadLinkAction.Remove>())
        {
            string linkPath = Path.Combine(instanceRoot, remove.Record.RelativePath);
            if (remove.Record.Kind == SquadLinkKind.Directory)
                fileSystem.DeleteTreeNoFollow(linkPath);
            else
                try
                {
                    if (File.Exists(linkPath) || fileSystem.IsLink(linkPath)) File.Delete(linkPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
        }

        // 2. Record warnings for unmanaged instance copies kept by planner
        HashSet<string> keptPaths = new(StringComparer.Ordinal);
        List<string> warnings = [];

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
                if (File.Exists(linkPath) && File.Exists(targetPath))
                {
                    if (FilesAreEqual(linkPath, targetPath))
                    {
                        try
                        {
                            File.Delete(linkPath);
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
                    if (Directory.Exists(linkPath) || fileSystem.IsLink(linkPath))
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
                    if (File.Exists(linkPath) || fileSystem.IsLink(linkPath)) File.Delete(linkPath);

                    EnsureDirectoryExists(Path.GetDirectoryName(linkPath));
                    fileSystem.CreateFileLink(linkPath, targetPath);
                    activeLinks.Add(record);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SquadLinkException)
                {
                    warnings.Add($"Failed to create file link for '{record.RelativePath}': {ex.Message}");
                }
        }

        List<SquadLinkRecord> sortedLinks = activeLinks
            .OrderBy(l => l.RelativePath, StringComparer.Ordinal)
            .ToList();

        WriteLinksJson(instanceStateDirectory, sortedLinks);

        return new SquadInstanceLinkSyncResult(sortedLinks, warnings);
    }

    private static List<SquadLinkRecord> ReadRecordedLinks(string instanceStateDirectory)
    {
        string linksJsonPath = Path.Combine(instanceStateDirectory, "links.json");
        if (!File.Exists(linksJsonPath)) return [];

        try
        {
            string json = File.ReadAllText(linksJsonPath, Encoding.UTF8);
            List<SquadLinkRecord>? deserialized = JsonSerializer.Deserialize<List<SquadLinkRecord>>(json, JsonOptions);
            return deserialized ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private static void WriteLinksJson(string instanceStateDirectory, IReadOnlyList<SquadLinkRecord> links)
    {
        EnsureDirectoryExists(instanceStateDirectory);
        string text = JsonSerializer.Serialize(links, JsonOptions) + "\n";
        string targetPath = Path.Combine(instanceStateDirectory, "links.json");
        string tempPath = Path.Combine(instanceStateDirectory, $"links.json.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(tempPath, targetPath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
        }
    }

    private static List<SquadLinkRecord> EnumerateMainHome(
        string mainHome,
        IReadOnlySet<string> renderedPaths,
        IReadOnlyList<string> extraLinks)
    {
        List<SquadLinkRecord> records = [];
        if (!Directory.Exists(mainHome)) return records;

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

        Queue<(DirectoryInfo Dir, int Depth)> queue = new();
        queue.Enqueue((new DirectoryInfo(mainHome), 0));

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        while (queue.Count > 0)
        {
            (DirectoryInfo current, int depth) = queue.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = current.EnumerateFileSystemInfos("*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                string relativePath = Path.GetRelativePath(mainHome, entry.FullName).Replace('\\', '/');
                string normRel = relativePath.Trim('/');

                if (entry is DirectoryInfo dirEntry)
                {
                    records.Add(new SquadLinkRecord(relativePath, SquadLinkKind.Directory, dirEntry.FullName));
                    if (depth < 10 &&
                        entry.LinkTarget is null &&
                        !entry.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                        interestingPrefixes.Contains(normRel))
                    {
                        queue.Enqueue((dirEntry, depth + 1));
                    }
                }
                else
                {
                    records.Add(new SquadLinkRecord(relativePath, SquadLinkKind.File, entry.FullName));
                }
            }
        }

        return records;
    }

    private static List<string> EnumerateInstanceRoot(string instanceRoot)
    {
        List<string> results = [];
        if (!Directory.Exists(instanceRoot)) return results;

        Queue<(DirectoryInfo Dir, int Depth)> queue = new();
        queue.Enqueue((new DirectoryInfo(instanceRoot), 0));

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        while (queue.Count > 0)
        {
            (DirectoryInfo current, int depth) = queue.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = current.EnumerateFileSystemInfos("*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                string relativePath = Path.GetRelativePath(instanceRoot, entry.FullName).Replace('\\', '/');
                results.Add(relativePath);
                if (entry is DirectoryInfo dirEntry &&
                    depth < 10 &&
                    entry.LinkTarget is null &&
                    !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    queue.Enqueue((dirEntry, depth + 1));
                }
            }
        }

        return results;
    }

    private static bool FilesAreEqual(string path1, string path2)
    {
        try
        {
            FileInfo info1 = new(path1);
            FileInfo info2 = new(path2);

            if (info1.Length != info2.Length) return false;

            if (info1.Length == 0) return true;

            using FileStream stream1 = File.OpenRead(path1);
            using FileStream stream2 = File.OpenRead(path2);

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

    private static void EnsureDirectoryExists(string? directoryPath)
    {
        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
            try
            {
                Directory.CreateDirectory(directoryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException)
            {
            }
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }
}
