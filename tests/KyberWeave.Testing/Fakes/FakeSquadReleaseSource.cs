using System.IO.Compression;
using System.Security.Cryptography;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Data.Hooks;
using KyberWeave.Application.Update;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The one fake for <see cref="ISquadReleaseSource"/>: it records every extraction request and writes the
/// release the test configured.
/// </summary>
/// <remarks>
/// The unconfigured fake writes a minimal but schema-valid <c>squad.yml</c> and a <c>toolchain.yml</c> that
/// mirrors the canonical product file. A test that needs different content configures a source directory
/// (<see cref="FromDirectory"/>), a pack archive (<see cref="FromArchive"/>), per-file content
/// (<see cref="FileContents"/>), an edit to make after extraction (<see cref="AfterExtract"/>), the reported
/// asset identity, or a fault (<see cref="Failing"/>), so each stays a configuration of this fake rather than
/// another local double. Every read and write goes through <see cref="Files"/>, so a unit test hands the fake
/// its in-memory file system and no extraction touches the disk; a test that does not name one gets the
/// physical file system, which only the integration tests that install onto a real tree rely on.
/// </remarks>
public sealed class FakeSquadReleaseSource : ISquadReleaseSource
{
    private const string DefaultAssetSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly Lock _gate = new();
    private readonly List<SquadReleaseRequest> _requests = [];

    /// <summary>A fake that copies every file under <paramref name="sourceDirectory"/> into the destination.</summary>
    internal static FakeSquadReleaseSource FromDirectory(string sourceDirectory, IWritableFileSystem? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        return new FakeSquadReleaseSource { SourceDirectory = sourceDirectory, Files = files ?? new PhysicalFileSystem() };
    }

    /// <summary>
    /// A fake that extracts the pack archive at <paramref name="archivePath"/> after the same entry checks the
    /// product's release source applies, reporting the archive's own size and digest.
    /// </summary>
    internal static FakeSquadReleaseSource FromArchive(string archivePath, IWritableFileSystem? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return new FakeSquadReleaseSource { ArchivePath = archivePath, Files = files ?? new PhysicalFileSystem() };
    }

    /// <summary>A fake that records the request, then fails with <paramref name="fault"/> instead of extracting.</summary>
    internal static FakeSquadReleaseSource Failing(Exception fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        return new FakeSquadReleaseSource { Fault = fault };
    }

    /// <summary>The file system the release is read from and written to.</summary>
    internal IWritableFileSystem Files { get; init; } = new PhysicalFileSystem();

    /// <summary>Every extraction request received, in call order.</summary>
    public IReadOnlyList<SquadReleaseRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>How many extractions the fake attempted, including ones it failed.</summary>
    internal int CallCount => Requests.Count;

    /// <summary>The directory copied into the destination; when neither this nor <see cref="ArchivePath"/> is set, the minimal corpus is written.</summary>
    internal string? SourceDirectory { get; init; }

    /// <summary>The pack archive extracted into the destination, in place of a source directory.</summary>
    internal string? ArchivePath { get; init; }

    /// <summary>
    /// Files written into the destination after the copy and before <see cref="AfterExtract"/>, keyed by a
    /// forward-slashed path relative to the extraction root. A key that exists is replaced; a new key is added.
    /// </summary>
    internal IReadOnlyDictionary<string, string> FileContents { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>An edit made to the extracted tree, such as padding a file the renderer then measures.</summary>
    internal Func<string, CancellationToken, Task>? AfterExtract { get; init; }

    /// <summary>The asset name to report; when null the fake derives it from the request or archive.</summary>
    internal string? AssetName { get; init; }

    /// <summary>The download URI to report; when null the fake derives it from the name and mode.</summary>
    internal Uri? DownloadUri { get; init; }

    /// <summary>The asset size to report; when null the fake reports 1024 bytes, or the archive's length.</summary>
    internal long? AssetSize { get; init; }

    /// <summary>The checksum to report; when null the fake reports a fixed digest, or the archive's own.</summary>
    internal string? Sha256 { get; init; }

    /// <summary>Raised for every request instead of extracting.</summary>
    internal Exception? Fault { get; init; }

    public async Task<SquadReleaseResult> DownloadAndExtractAsync(
        SquadReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        ValidateSource();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _requests.Add(request);
        }

        if (Fault is not null)
        {
            throw Fault;
        }

        long reportedSize = AssetSize ?? 1024;
        string reportedSha256 = Sha256 ?? DefaultAssetSha256;

        if (ArchivePath is not null)
        {
            ValidateArchiveEntries(ArchivePath, request.DestinationPath, cancellationToken);
            ExtractArchive(ArchivePath, request.DestinationPath, cancellationToken);

            byte[] archiveBytes = Files.ReadAllBytes(ArchivePath);
            reportedSize = AssetSize ?? Files.GetLength(ArchivePath);
            reportedSha256 = Sha256 ?? Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
        }
        else if (SourceDirectory is not null)
        {
            CopyDirectory(SourceDirectory, request.DestinationPath);
        }
        else
        {
            WriteMinimalCorpus(request.DestinationPath);
        }

        WriteFileContents(request.DestinationPath);

        if (AfterExtract is not null)
        {
            await AfterExtract(request.DestinationPath, cancellationToken);
        }

        string assetName = AssetName ?? $"kyber-squad-{request.Version}.zip";
        Uri downloadUri = DownloadUri ?? (ArchivePath is not null
            ? new Uri($"https://localhost/{assetName}")
            : new Uri($"https://github.com/{request.Repository}/releases/download/v{request.Version}/{assetName}"));

        List<string> extractedFiles = ListExtractedFiles(request.DestinationPath);
        return new SquadReleaseResult(
            request.Version,
            new SquadReleaseAsset(assetName, downloadUri, reportedSize),
            new SquadReleaseChecksum(assetName, reportedSha256),
            Path.GetFullPath(request.DestinationPath),
            extractedFiles);
    }

    public void Dispose()
    {
        // No unmanaged resources.
    }

    /// <summary>
    /// A fake extracts one thing. Configuring both a source directory and an archive is a typo, so it fails
    /// here instead of silently preferring one.
    /// </summary>
    private void ValidateSource()
    {
        if (SourceDirectory is not null && ArchivePath is not null)
        {
            throw new InvalidOperationException(
                "A fake release source extracts an archive or a source directory, not both.");
        }
    }

    /// <summary>
    /// The same entry rules the product's release adapter applies before it extracts an archive, so a unit test
    /// cannot hand the fake an archive a real download would reject: no rooted or climbing path, no duplicate,
    /// no symbolic link, and no path that descends through a file.
    /// </summary>
    private void ValidateArchiveEntries(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = new(Files.OpenRead(archivePath), ZipArchiveMode.Read, leaveOpen: false);
        StringComparer comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        HashSet<string> seen = new(comparison);
        HashSet<string> files = new(comparison);
        HashSet<string> directories = new(comparison);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException(
                    $"The Squad archive contains a symbolic link entry: '{entry.FullName}'.");
            }

            (string relativePath, bool isDirectory) = ValidateRelativePath(entry.FullName, destinationPath);
            if (!seen.Add(relativePath))
            {
                throw new InvalidDataException(
                    $"The Squad archive contains duplicate path '{relativePath}'.");
            }

            ValidateArchiveHierarchy(relativePath, isDirectory, files, directories);
        }
    }

    /// <summary>Writes every entry of the already validated archive under the destination, replacing what is there.</summary>
    private void ExtractArchive(string archivePath, string destinationPath, CancellationToken cancellationToken)
    {
        Files.CreateDirectory(destinationPath);
        using ZipArchive archive = new(Files.OpenRead(archivePath), ZipArchiveMode.Read, leaveOpen: false);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string relativePath, bool isDirectory) = ValidateRelativePath(entry.FullName, destinationPath);
            string path = Path.Combine(destinationPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (isDirectory)
            {
                Files.CreateDirectory(path);
                continue;
            }

            Files.CreateDirectory(Path.GetDirectoryName(path)!);
            using Stream source = entry.Open();
            using MemoryStream buffer = new();
            source.CopyTo(buffer);
            Files.WriteAllBytes(path, buffer.ToArray());
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const uint UnixFileTypeMask = 0xF000;
        const uint UnixSymbolicLink = 0xA000;
        uint unixMode = ((uint)entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        bool windowsReparsePoint =
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
        return unixMode == UnixSymbolicLink || windowsReparsePoint;
    }

    private static void ValidateArchiveHierarchy(
        string relativePath,
        bool isDirectory,
        HashSet<string> files,
        HashSet<string> directories)
    {
        string[] segments = relativePath.Split('/');
        for (int index = 1; index < segments.Length; index++)
        {
            string ancestor = string.Join('/', segments.Take(index));
            if (files.Contains(ancestor))
            {
                throw new InvalidDataException(
                    $"The Squad archive path '{relativePath}' descends through file '{ancestor}'.");
            }

            directories.Add(ancestor);
        }

        if (isDirectory)
        {
            directories.Add(relativePath);
            return;
        }

        if (directories.Contains(relativePath))
        {
            throw new InvalidDataException(
                $"The Squad archive path '{relativePath}' conflicts with a directory.");
        }

        files.Add(relativePath);
    }

    /// <summary>
    /// The same request rule the product's release adapter applies before it touches the network, so a unit
    /// test cannot hand the fake a repository, version or destination the real source would reject.
    /// </summary>
    private static void ValidateRequest(SquadReleaseRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);

        string[] repositoryParts = request.Repository.Split('/');
        if (repositoryParts.Length != 2 || repositoryParts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "The release repository must use exact 'owner/name' form.",
                nameof(request));
        }

        if (!ReleaseVersion.IsStrictSemVer(request.Version))
        {
            throw new ArgumentException(
                "The release version must use valid semantic version format (X.Y.Z or X.Y.Z-prerelease) with no leading zeros.",
                nameof(request));
        }
    }

    private void WriteMinimalCorpus(string destinationPath)
    {
        Files.CreateDirectory(destinationPath);

        Files.WriteAllText(
            Path.Combine(destinationPath, "squad.yml"),
            "schema: kyber-squad.squad/v1\nversion-source: kyber-weave-assembly\n");

        // Mirrors products/kyber-squad/toolchain.yml byte for byte in the shape that
        // matters: no validated-release. A fake that pinned a fabricated digest here kept
        // the suite green while every real install failed lock validation, so this default
        // must keep tracking the canonical file. A test needing a pinned upstream build
        // writes its own toolchain.yml into the destination after this fake returns.
        Files.WriteAllText(
            Path.Combine(destinationPath, "toolchain.yml"),
            """
            schema: kyber-squad.toolchain/v1
            required-features: []
            validated-release: null
            """);
    }

    private void WriteFileContents(string destinationPath)
    {
        foreach ((string relativePath, string content) in FileContents)
        {
            (string validatedPath, bool isDirectory) = ValidateRelativePath(relativePath, destinationPath);
            if (isDirectory)
            {
                throw new InvalidDataException(
                    $"The Squad release content names a directory, not a file: '{relativePath}'.");
            }

            string path = Path.Combine(destinationPath, validatedPath.Replace('/', Path.DirectorySeparatorChar));
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Files.CreateDirectory(directory);
            }

            Files.WriteAllText(path, content);
        }
    }

    /// <summary>
    /// The same portable-path rule the product's release adapter applies to an archive entry: a relative
    /// path that cannot be rooted, cannot climb out with <c>..</c>, and resolves inside the extraction root,
    /// so a fake cannot write where a real release could not.
    /// </summary>
    private static (string RelativePath, bool IsDirectory) ValidateRelativePath(
        string entryName,
        string destinationPath)
    {
        if (string.IsNullOrEmpty(entryName) ||
            entryName.Contains('\0', StringComparison.Ordinal) ||
            entryName.StartsWith('/') ||
            entryName.StartsWith('\\') ||
            (entryName.Length >= 2 && char.IsAsciiLetter(entryName[0]) && entryName[1] == ':'))
        {
            throw new InvalidDataException(
                $"The Squad release contains an invalid rooted path: '{entryName}'.");
        }

        string portablePath = entryName.Replace('\\', '/');
        bool isDirectory = portablePath.EndsWith('/');
        string[] segments = portablePath.Split('/');
        int segmentCount = isDirectory ? segments.Length - 1 : segments.Length;
        if (segmentCount == 0 ||
            segments.Take(segmentCount).Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"The Squad release contains an unsafe path: '{entryName}'.");
        }

        string relativePath = string.Join('/', segments.Take(segmentCount));
        string fullDestination = Path.GetFullPath(destinationPath);
        string containmentPrefix = fullDestination.EndsWith(Path.DirectorySeparatorChar)
            ? fullDestination
            : fullDestination + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(
            Path.Combine(fullDestination, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(containmentPrefix, comparison))
        {
            throw new InvalidDataException(
                $"The Squad release path escapes its extraction root: '{entryName}'.");
        }

        return (relativePath, isDirectory);
    }

    private void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Files.CreateDirectory(destinationDirectory);

        foreach (FileSystemEntry entry in Files.EnumerateEntries(sourceDirectory))
        {
            if (entry.IsLink)
            {
                throw new InvalidDataException(
                    $"The Squad corpus contains a symbolic link entry: '{entry.Path}'.");
            }

            string targetPath = Path.Combine(destinationDirectory, Path.GetFileName(entry.Path));
            if (entry.IsDirectory)
            {
                CopyDirectory(entry.Path, targetPath);
            }
            else
            {
                Files.WriteAllBytes(targetPath, Files.ReadAllBytes(entry.Path));
            }
        }
    }

    private List<string> ListExtractedFiles(string destinationPath)
    {
        return Files
            .EnumerateFiles(destinationPath, "*", recursive: true)
            .Select(path => Path.GetRelativePath(destinationPath, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
