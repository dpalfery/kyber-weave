using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// The shared release-source fake writes the release the test configured, whether that is the minimal corpus, a
/// directory, a pack archive or per-file content, and records every request, so no test needs a release-source
/// double of its own.
/// </summary>
public sealed class FakeSquadReleaseSourceTests : IDisposable
{
    private const string EmptyAssetSha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public async Task TheUnconfiguredFakeWritesTheMinimalCorpusAndRecordsTheRequest()
    {
        FakeSquadReleaseSource source = new();
        SquadReleaseRequest request = Request("minimal");

        SquadReleaseResult result = await source.DownloadAndExtractAsync(request);

        Assert.Equal(
            Utf8NoBom("schema: kyber-squad.squad/v1\nversion-source: kyber-weave-assembly\n"),
            await File.ReadAllBytesAsync(Path.Combine(request.DestinationPath, "squad.yml")));
        Assert.Equal(
            Utf8NoBom("schema: kyber-squad.toolchain/v1\nrequired-features: []\nvalidated-release: null"),
            await File.ReadAllBytesAsync(Path.Combine(request.DestinationPath, "toolchain.yml")));
        Assert.Equal(["squad.yml", "toolchain.yml"], result.ExtractedFiles);
        Assert.Equal("kyber-squad-1.2.3.zip", result.Asset.Name);
        Assert.Equal(
            "https://github.com/owner/kyber-squad/releases/download/v1.2.3/kyber-squad-1.2.3.zip",
            result.Asset.DownloadUri.AbsoluteUri);
        Assert.Equal(1024L, result.Asset.Size);
        Assert.Equal(EmptyAssetSha256, result.Checksum.Sha256);
        Assert.Equal(Path.GetFullPath(request.DestinationPath), result.ExtractionRoot);
        Assert.Same(request, Assert.Single(source.Requests));
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task AConfiguredDirectoryIsCopiedByteForByteAndTheSourceIsLeftAlone()
    {
        string sourceRoot = Path.Combine(_temp.Path, "corpus");
        WriteFile(sourceRoot, "squad.yml", "corpus");
        WriteFile(sourceRoot, "agents/conductor.md", "the conductor");
        WriteFile(sourceRoot, "skills/task-gate/SKILL.md", "the skill");
        Dictionary<string, byte[]> before = Snapshot(sourceRoot);
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromDirectory(sourceRoot);
        SquadReleaseRequest request = Request("copied");

        SquadReleaseResult result = await source.DownloadAndExtractAsync(request);

        Assert.Equal("corpus", await File.ReadAllTextAsync(Path.Combine(request.DestinationPath, "squad.yml")));
        Assert.Equal(
            "the conductor",
            await File.ReadAllTextAsync(Path.Combine(request.DestinationPath, "agents", "conductor.md")));
        Assert.Equal(
            "the skill",
            await File.ReadAllTextAsync(Path.Combine(request.DestinationPath, "skills", "task-gate", "SKILL.md")));
        Assert.Equal(
            ["agents/conductor.md", "skills/task-gate/SKILL.md", "squad.yml"],
            result.ExtractedFiles);
        Assert.Equal(before, Snapshot(sourceRoot));
    }

    [Fact]
    public async Task FileContentOverridesReplaceAndAddFiles()
    {
        FakeSquadReleaseSource source = new()
        {
            FileContents = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["squad.yml"] = "name: kyber-squad\nschema: invalid-schema-v999\n",
                ["skills/response-format/SKILL.md"] = "---\nname: response-format\n---\nbody",
            },
        };
        SquadReleaseRequest request = Request("overrides");

        SquadReleaseResult result = await source.DownloadAndExtractAsync(request);

        Assert.Equal(
            Utf8NoBom("name: kyber-squad\nschema: invalid-schema-v999\n"),
            await File.ReadAllBytesAsync(Path.Combine(request.DestinationPath, "squad.yml")));
        Assert.Equal(
            Utf8NoBom("---\nname: response-format\n---\nbody"),
            await File.ReadAllBytesAsync(
                Path.Combine(request.DestinationPath, "skills", "response-format", "SKILL.md")));
        Assert.Equal(
            ["skills/response-format/SKILL.md", "squad.yml", "toolchain.yml"],
            result.ExtractedFiles);
    }

    [Fact]
    public async Task ConfiguringASourceDirectoryAndAnArchiveIsRefused()
    {
        string sourceRoot = Path.Combine(_temp.Path, "ambiguous-source");
        WriteFile(sourceRoot, "squad.yml", "source");
        string archivePath = Path.Combine(_temp.Path, "ambiguous.zip");
        await ZipFile.CreateFromDirectoryAsync(sourceRoot, archivePath);
        FakeSquadReleaseSource source = new()
        {
            SourceDirectory = sourceRoot,
            ArchivePath = archivePath,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.DownloadAndExtractAsync(Request("ambiguous")));

        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task ASymbolicLinkInTheSourceDirectoryIsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symbolic link needs a privilege Windows test runs do not have.");
        }

        string sourceRoot = Path.Combine(_temp.Path, "linked-source");
        WriteFile(sourceRoot, "squad.yml", "linked corpus");
        File.CreateSymbolicLink(
            Path.Combine(sourceRoot, "linked.yml"),
            Path.Combine(sourceRoot, "squad.yml"));
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromDirectory(sourceRoot);
        SquadReleaseRequest request = Request("linked");

        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAndExtractAsync(request));

        Assert.False(File.Exists(Path.Combine(request.DestinationPath, "linked.yml")));
    }

    [Fact]
    public async Task AnArchiveEntryThatEscapesTheDestinationIsRefused()
    {
        string archivePath = await WriteArchiveAsync(
            Path.Combine(_temp.Path, "escaping.zip"),
            ("../evil.txt", "escaped archive content"));
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromArchive(archivePath);
        SquadReleaseRequest request = Request("escaping-archive");

        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAndExtractAsync(request));

        Assert.False(File.Exists(Path.Combine(_temp.Path, "evil.txt")));
    }

    [Fact]
    public async Task ASymbolicLinkArchiveEntryIsRefused()
    {
        string archivePath = await WriteArchiveAsync(
            Path.Combine(_temp.Path, "linked.zip"),
            ("linked.yml", "squad.yml", unchecked((int)(0xA000u << 16))));
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromArchive(archivePath);
        SquadReleaseRequest request = Request("linked-archive");

        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAndExtractAsync(request));

        Assert.False(File.Exists(Path.Combine(request.DestinationPath, "linked.yml")));
    }

    [Fact]
    public async Task ADuplicateArchivePathIsRefused()
    {
        string archivePath = await WriteArchiveAsync(
            Path.Combine(_temp.Path, "duplicate.zip"),
            ("squad.yml", "first"),
            ("squad.yml", "second"));
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromArchive(archivePath);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => source.DownloadAndExtractAsync(Request("duplicate-archive")));
    }

    [Fact]
    public async Task AFileContentPathThatEscapesTheDestinationIsRefused()
    {
        FakeSquadReleaseSource source = new()
        {
            FileContents = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["../escaped.txt"] = "escaped content",
            },
        };
        SquadReleaseRequest request = Request("escaping-content");

        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAndExtractAsync(request));

        Assert.False(File.Exists(Path.Combine(_temp.Path, "escaped.txt")));
    }

    [Fact]
    public async Task ARootedFileContentPathIsRefused()
    {
        string rootedPath = Path.Combine(_temp.Path, "rooted.txt");
        FakeSquadReleaseSource source = new()
        {
            FileContents = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [rootedPath] = "rooted content",
            },
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => source.DownloadAndExtractAsync(Request("rooted-content")));

        Assert.False(File.Exists(rootedPath));
    }

    [Theory]
    [InlineData("evil", "1.2.3")]
    [InlineData("owner/name/extra", "1.2.3")]
    [InlineData("owner/name", "not-a-version")]
    [InlineData("owner/name", "01.2.3")]
    public async Task AMalformedRequestIsRefusedLikeTheRealSource(string repository, string version)
    {
        FakeSquadReleaseSource source = new();

        await Assert.ThrowsAsync<ArgumentException>(
            () => source.DownloadAndExtractAsync(
                new SquadReleaseRequest(repository, version, Path.Combine(_temp.Path, "malformed"))));

        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task AWhitespaceDestinationIsRefusedLikeTheRealSource()
    {
        FakeSquadReleaseSource source = new();

        await Assert.ThrowsAsync<ArgumentException>(
            () => source.DownloadAndExtractAsync(new SquadReleaseRequest("owner/name", "1.2.3", "   ")));

        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task APrereleaseVersionIsAccepted()
    {
        FakeSquadReleaseSource source = new();
        SquadReleaseRequest request = Request("prerelease", version: "1.2.3-rc.1");

        SquadReleaseResult result = await source.DownloadAndExtractAsync(request);

        Assert.Equal("1.2.3-rc.1", result.Version);
        Assert.Equal("kyber-squad-1.2.3-rc.1.zip", result.Asset.Name);
    }

    [Fact]
    public async Task TheAfterExtractHookSeesTheExtractedRootAndItsEditLands()
    {
        string sourceRoot = Path.Combine(_temp.Path, "padded-corpus");
        WriteFile(sourceRoot, "agents/conductor.md", "the conductor\n");
        FakeSquadReleaseSource source = new()
        {
            SourceDirectory = sourceRoot,
            AfterExtract = static (root, token) => File.AppendAllTextAsync(
                Path.Combine(root, "agents", "conductor.md"),
                "the padding\n",
                token),
        };
        SquadReleaseRequest request = Request("padded");

        await source.DownloadAndExtractAsync(request);

        Assert.Equal(
            "the conductor\nthe padding\n",
            await File.ReadAllTextAsync(Path.Combine(request.DestinationPath, "agents", "conductor.md")));
    }

    [Fact]
    public async Task FromArchiveExtractsThePackAndReportsTheArchivesOwnIdentity()
    {
        string archiveSource = Path.Combine(_temp.Path, "archive-source");
        WriteFile(archiveSource, "squad.yml", "packed");
        WriteFile(archiveSource, "skills/task-gate/SKILL.md", "packed skill");
        string archivePath = Path.Combine(_temp.Path, "kyber-squad-1.2.3.zip");
        await ZipFile.CreateFromDirectoryAsync(archiveSource, archivePath);
        byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath);
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromArchive(archivePath);
        SquadReleaseRequest request = Request("unpacked");

        SquadReleaseResult result = await source.DownloadAndExtractAsync(request);

        Assert.Equal("packed", await File.ReadAllTextAsync(Path.Combine(request.DestinationPath, "squad.yml")));
        Assert.Equal(
            ["skills/task-gate/SKILL.md", "squad.yml"],
            result.ExtractedFiles);
        Assert.Equal("kyber-squad-1.2.3.zip", result.Asset.Name);
        Assert.Equal("https://localhost/kyber-squad-1.2.3.zip", result.Asset.DownloadUri.AbsoluteUri);
        Assert.Equal(new FileInfo(archivePath).Length, result.Asset.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(archiveBytes)), result.Checksum.Sha256);
    }

    [Fact]
    public async Task AnExplicitIdentityIsReportedUnchanged()
    {
        Uri downloadUri = new("https://example.com/asset.zip");
        FakeSquadReleaseSource source = new()
        {
            AssetName = "asset",
            DownloadUri = downloadUri,
            AssetSize = 100,
            Sha256 = EmptyAssetSha256,
        };

        SquadReleaseResult result = await source.DownloadAndExtractAsync(Request("identity"));

        Assert.Equal("asset", result.Asset.Name);
        Assert.Same(downloadUri, result.Asset.DownloadUri);
        Assert.Equal(100L, result.Asset.Size);
        Assert.Equal(EmptyAssetSha256, result.Checksum.Sha256);
    }

    [Fact]
    public async Task AFaultIsRaisedAfterTheRequestIsRecorded()
    {
        InvalidOperationException fault = new("The Squad release is unavailable.");
        FakeSquadReleaseSource source = FakeSquadReleaseSource.Failing(fault);
        SquadReleaseRequest request = Request("failing");

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.DownloadAndExtractAsync(request));

        Assert.Same(fault, thrown);
        Assert.Same(request, Assert.Single(source.Requests));
        Assert.False(Directory.Exists(request.DestinationPath));
    }

    [Fact]
    public async Task ACancelledRequestIsRefusedBeforeItIsRecorded()
    {
        FakeSquadReleaseSource source = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.DownloadAndExtractAsync(Request("cancelled"), cancellation.Token));

        Assert.Empty(source.Requests);
    }

    private SquadReleaseRequest Request(string name, string version = "1.2.3")
    {
        return new SquadReleaseRequest(
            "owner/kyber-squad",
            version,
            Path.Combine(_temp.Path, name));
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task<string> WriteArchiveAsync(
        string archivePath,
        params (string Name, string Content, int ExternalAttributes)[] entries)
    {
        await using FileStream stream = File.Create(archivePath);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        foreach ((string name, string content, int externalAttributes) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            entry.ExternalAttributes = externalAttributes;
            await using Stream entryStream = await entry.OpenAsync();
            await entryStream.WriteAsync(Utf8NoBom(content));
        }

        return archivePath;
    }

    private static async Task<string> WriteArchiveAsync(
        string archivePath,
        params (string Name, string Content)[] entries)
    {
        (string Name, string Content, int ExternalAttributes)[] expanded = entries
            .Select(static entry => (entry.Name, entry.Content, 0))
            .ToArray();
        return await WriteArchiveAsync(archivePath, expanded);
    }

    private static Dictionary<string, byte[]> Snapshot(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);
    }

    private static byte[] Utf8NoBom(string text)
    {
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
    }
}
