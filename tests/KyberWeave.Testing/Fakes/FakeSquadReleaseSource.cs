using System.IO.Compression;
using System.Security.Cryptography;
using KyberWeave.Core.Squad.Release;

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
/// another local double.
/// </remarks>
public sealed class FakeSquadReleaseSource : ISquadReleaseSource
{
    private const string DefaultAssetSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly Lock _gate = new();
    private readonly List<SquadReleaseRequest> _requests = [];

    /// <summary>A fake that copies every file under <paramref name="sourceDirectory"/> into the destination.</summary>
    internal static FakeSquadReleaseSource FromDirectory(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        return new FakeSquadReleaseSource { SourceDirectory = sourceDirectory };
    }

    /// <summary>
    /// A fake that extracts the pack archive at <paramref name="archivePath"/>, reporting the archive's own
    /// size and digest, as the product's GitHub release source does.
    /// </summary>
    internal static FakeSquadReleaseSource FromArchive(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return new FakeSquadReleaseSource { ArchivePath = archivePath };
    }

    /// <summary>A fake that records the request, then fails with <paramref name="fault"/> instead of extracting.</summary>
    internal static FakeSquadReleaseSource Failing(Exception fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        return new FakeSquadReleaseSource { Fault = fault };
    }

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

    /// <summary>The directory copied into the destination; when null the minimal corpus below is written.</summary>
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
            await ZipFile.ExtractToDirectoryAsync(ArchivePath, request.DestinationPath, true, cancellationToken);

            byte[] archiveBytes = await File.ReadAllBytesAsync(ArchivePath, cancellationToken);
            reportedSize = AssetSize ?? new FileInfo(ArchivePath).Length;
            reportedSha256 = Sha256 ?? Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
        }
        else if (SourceDirectory is not null)
        {
            CopyDirectory(SourceDirectory, request.DestinationPath);
        }
        else
        {
            await WriteMinimalCorpusAsync(request.DestinationPath, cancellationToken);
        }

        await WriteFileContentsAsync(request.DestinationPath, cancellationToken);

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

    private static async Task WriteMinimalCorpusAsync(string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);

        await File.WriteAllTextAsync(
            Path.Combine(destinationPath, "squad.yml"),
            "schema: kyber-squad.squad/v1\nversion-source: kyber-weave-assembly\n",
            cancellationToken);

        // Mirrors products/kyber-squad/toolchain.yml byte for byte in the shape that
        // matters: no validated-release. A fake that pinned a fabricated digest here kept
        // the suite green while every real install failed lock validation, so this default
        // must keep tracking the canonical file. A test needing a pinned upstream build
        // writes its own toolchain.yml into the destination after this fake returns.
        await File.WriteAllTextAsync(
            Path.Combine(destinationPath, "toolchain.yml"),
            """
            schema: kyber-squad.toolchain/v1
            required-features: []
            validated-release: null
            """,
            cancellationToken);
    }

    private async Task WriteFileContentsAsync(string destinationPath, CancellationToken cancellationToken)
    {
        foreach ((string relativePath, string content) in FileContents)
        {
            string path = Path.Combine(destinationPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, content, cancellationToken);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (FileSystemInfo entry in new DirectoryInfo(sourceDirectory).EnumerateFileSystemInfos(
                     "*",
                     new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            string targetPath = Path.Combine(destinationDirectory, entry.Name);
            if (entry is DirectoryInfo subDirectory)
            {
                CopyDirectory(subDirectory.FullName, targetPath);
            }
            else if (entry is FileInfo file)
            {
                file.CopyTo(targetPath, true);
            }
        }
    }

    private static List<string> ListExtractedFiles(string destinationPath)
    {
        return Directory
            .EnumerateFiles(destinationPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destinationPath, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
