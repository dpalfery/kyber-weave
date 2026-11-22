using System.Text;

namespace KyberDash.Data.Collector.Sources;

/// <summary>
/// Read-only filesystem seam used by <see cref="IHarnessSourceReader"/> implementations and
/// <see cref="HarnessSourceRegistry"/> so discovery runs deterministically in unit tests and safely on disk.
/// </summary>
public interface IHarnessFileSystem
{
    /// <summary>Returns <see langword="true"/> when <paramref name="path"/> exists as a directory.</summary>
    bool DirectoryExists(string path);

    /// <summary>Returns <see langword="true"/> when <paramref name="path"/> exists as a file.</summary>
    bool FileExists(string path);

    /// <summary>Enumerates matching files under <paramref name="directoryPath"/> without following symbolic links.</summary>
    IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive);

    /// <summary>Enumerates immediate child directories under <paramref name="directoryPath"/> without following symbolic links.</summary>
    IEnumerable<string> EnumerateDirectories(string directoryPath);

    /// <summary>Reads byte length and UTC last-write timestamp for <paramref name="filePath"/> when accessible.</summary>
    bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc);

    /// <summary>Reads the full UTF-8 text contents of <paramref name="filePath"/>.</summary>
    string ReadAllText(string filePath);

    /// <summary>Resolves <paramref name="path"/> to its canonical full path string.</summary>
    string GetFullPath(string path);
}

/// <summary>
/// Default <see cref="IHarnessFileSystem"/> backed by the local filesystem using symlink-safe
/// single-level directory traversal.
/// </summary>
public class PhysicalHarnessFileSystem : IHarnessFileSystem
{
    private static readonly EnumerationOptions SingleLevelOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    /// <summary>Shared singleton instance for production discovery.</summary>
    public static PhysicalHarnessFileSystem Instance { get; } = new();

    /// <inheritdoc />
    public bool DirectoryExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    /// <inheritdoc />
    public bool FileExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(searchPattern))
        {
            return [];
        }

        if (!Directory.Exists(directoryPath))
        {
            return [];
        }

        List<string> results = [];
        Queue<string> pendingDirectories = new();
        pendingDirectories.Enqueue(Path.GetFullPath(directoryPath));

        while (pendingDirectories.Count > 0)
        {
            string currentDirectory = pendingDirectories.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(currentDirectory).EnumerateFileSystemInfos("*", SingleLevelOptions);
            }
            catch (Exception exception) when (IsFilesystemAccessFailure(exception))
            {
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                // Skip symbolic links and junctions so recursive discovery cannot escape the root.
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    if (recursive)
                    {
                        pendingDirectories.Enqueue(entry.FullName);
                    }

                    continue;
                }

                if (entry is FileInfo && MatchesPattern(entry.Name, searchPattern))
                {
                    results.Add(entry.FullName);
                }
            }
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return [];
        }

        List<string> results = [];
        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos("*", SingleLevelOptions))
            {
                if (entry.LinkTarget is null && entry is DirectoryInfo)
                {
                    results.Add(entry.FullName);
                }
            }
        }
        catch (Exception exception) when (IsFilesystemAccessFailure(exception))
        {
            return [];
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    /// <inheritdoc />
    public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
    {
        lengthBytes = 0;
        lastWriteUtc = default;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            FileInfo info = new(filePath);
            if (!info.Exists || info.LinkTarget is not null)
            {
                return false;
            }

            lengthBytes = info.Length;
            lastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            return true;
        }
        catch (Exception exception) when (IsFilesystemAccessFailure(exception))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string ReadAllText(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return File.ReadAllText(filePath, Encoding.UTF8);
    }

    /// <inheritdoc />
    public string GetFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFilesystemAccessFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;
}

/// <summary>
/// Declarative descriptor of one discovered harness source unit prior to reading its payload.
/// </summary>
/// <param name="HarnessId">Canonical attributed harness identifier.</param>
/// <param name="SourceKey">Stable canonical source locator within <paramref name="HarnessId"/>.</param>
/// <param name="ProviderId">Upstream provider identifier.</param>
/// <param name="ParserId">Reader/parser identifier.</param>
/// <param name="ParserContractVersion">Parser contract version used for checkpoint invalidation.</param>
/// <param name="Format">Native source format identifier.</param>
/// <param name="SourceRootLabel">Non-PII logical root label.</param>
/// <param name="RevisionToken">Fingerprint token for checkpoint comparison.</param>
/// <param name="LastWriteUtc">Optional UTC last-write timestamp.</param>
/// <param name="SizeBytes">Optional native payload size in bytes.</param>
/// <param name="CompanionKey">Optional companion file locator (for example Droid settings JSON).</param>
/// <param name="Kind">Source origin mechanism (<see cref="SourceKind.File"/> or <see cref="SourceKind.Otlp"/>).</param>
public record SourceUnit(
    string HarnessId,
    string SourceKey,
    string ProviderId,
    string ParserId,
    string ParserContractVersion,
    string Format,
    string SourceRootLabel,
    string RevisionToken,
    DateTimeOffset? LastWriteUtc = null,
    long? SizeBytes = null,
    string? CompanionKey = null,
    SourceKind Kind = SourceKind.File);

/// <summary>
/// In-memory native payload loaded from a <see cref="SourceUnit"/> for pure mapping by an <see cref="IHarnessMapper"/>.
/// </summary>
/// <param name="Unit">Source descriptor that produced this native payload.</param>
/// <param name="RawText">UTF-8 text payload when the unit is text-based; <see langword="null"/> for binary/SQLite units.</param>
/// <param name="SizeBytes">Measured byte size of the native payload.</param>
/// <param name="LastWriteUtc">UTC last-write timestamp observed when reading the unit.</param>
/// <param name="CompanionText">Optional UTF-8 text of the companion file referenced by <see cref="SourceUnit.CompanionKey"/>.</param>
public record NativeSourceUnit(
    SourceUnit Unit,
    string? RawText,
    long SizeBytes,
    DateTimeOffset LastWriteUtc,
    string? CompanionText = null);

/// <summary>
/// Discovery context specifying user home, KyberDash state directory, filesystem seam, and optional lookback cutoff.
/// </summary>
/// <param name="HomeDirectory">Root home directory holding local harness artifacts.</param>
/// <param name="StateDirectory">KyberDash state directory holding caches such as the Antigravity status line.</param>
/// <param name="FileSystem">Filesystem seam used for discovery and reading.</param>
/// <param name="SinceUtc">Optional inclusive UTC cutoff filtering out older source units.</param>
public record SourceDiscoveryContext(
    string HomeDirectory,
    string StateDirectory,
    IHarnessFileSystem FileSystem,
    DateTimeOffset? SinceUtc = null);

/// <summary>
/// Combined output of a harness discovery pass: discovered source units and diagnostic problems.
/// </summary>
/// <param name="Sources">Discovered source units ordered deterministically.</param>
/// <param name="Problems">Structured ingest problems emitted during discovery.</param>
public record SourceDiscoveryResult(
    IReadOnlyList<SourceUnit> Sources,
    IReadOnlyList<CollectedProblem> Problems);

/// <summary>
/// Contract for discovering and reading native source units for one harness surface.
/// </summary>
public interface IHarnessSourceReader
{
    /// <summary>Canonical harness identifier owned by this reader.</summary>
    string HarnessId { get; }

    /// <summary>True when this harness is OTLP-only and expects no local transcript files on disk.</summary>
    bool IsOtlpOnly { get; }

    /// <summary>Discovers available source units and any discovery-time problems under <paramref name="context"/>.</summary>
    SourceDiscoveryResult Discover(SourceDiscoveryContext context);

    /// <summary>Reads <paramref name="unit"/> through <paramref name="fileSystem"/> into a pure in-memory <see cref="NativeSourceUnit"/>.</summary>
    NativeSourceUnit? Read(SourceUnit unit, IHarnessFileSystem fileSystem);
}
