using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// The shared release-source fake writes the release the test configured, whether that is the minimal corpus, a
/// directory, a pack archive or per-file content, and records every request, so no test needs a release-source
/// double of its own. Every case runs over the in-memory file system the fake is given.
/// </summary>
public sealed class FakeSquadReleaseSourceTests
{
    private const string EmptyAssetSha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly InMemoryFileSystem _files = new();
    private readonly string _work;

    public FakeSquadReleaseSourceTests()
    {
        _work = Path.Combine(_files.Root, "work");
        _files.CreateDirectory(_work);
    }

    [Fact]
    public async Task TheUnconfiguredFakeWritesTheMinimalCorpusAndRecordsTheRequest()
    {
        FakeSquadReleaseSource source = new() { Files = _files };
        SquadReleaseRequest request = Request("minimal");

        SquadReleaseResult result = await source.DownloadAndExtractAsync(request);

        Assert.Equal(
            Utf8NoBom("schema: kyber-squad.squad/v1\nversion-source: kyber-weave-assembly\n"),
            _files.ReadAllBytes(Path.Combine(request.DestinationPath, "squad.yml")));
        Assert.Equal(
            Utf8NoBom("schema: kyber-squad.toolchain/v1\nrequired-features: []\nvalidated-release: null"),
            _files.ReadAllBytes(Path.Combine(request.DestinationPath, "toolchain.yml")));
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
        string sourceRoot = Path.Combine(_work, "corpus");
        WriteFile(sourceRoot, "squad.yml", "corpus");
        WriteFile(sourceRoot, "agents/conductor.md", "the conductor");
        WriteFile(sourceRoot, "skills/task-gate/SKILL.md", "the skill");
        Dictionary<string, byte[]> before = Snapshot(sourceRoot);
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromDirectory(sourceRoot, _files);
        SquadReleaseRequest request = Request("copied");

        SquadReleaseResult result = await source.DownloadAndExtractAsync(request);

        Assert.Equal("corpus", _files.ReadAllText(Path.Combine(request.DestinationPath, "squad.yml")));
        Assert.Equal(
            "the conductor",
            _files.ReadAllText(Path.Combine(request.DestinationPath, "agents", "conductor.md")));
        Assert.Equal(
            "the skill",
            _files.ReadAllText(Path.Combine(request.DestinationPath, "skills", "task-gate", "SKILL.md")));
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
            Files = _files,
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
            _files.ReadAllBytes(Path.Combine(request.DestinationPath, "squad.yml")));
        Assert.Equal(
            Utf8NoBom("---\nname: response-format\n---\nbody"),
            _files.ReadAllBytes(
                Path.Combine(request.DestinationPath, "skills", "response-format", "SKILL.md")));
        Assert.Equal(
            ["skills/response-format/SKILL.md", "squad.yml", "toolchain.yml"],
            result.ExtractedFiles);
    }

    [Fact]
    public async Task ConfiguringASourceDirectoryAndAnArchiveIsRefused()
    {
        string sourceRoot = Path.Combine(_work, "ambiguous-source");
        WriteFile(sourceRoot, "squad.yml", "source");
        string archivePath = await WriteArchiveAsync(Path.Combine(_work, "ambiguous.zip"), ("squad.yml", "source"));
        FakeSquadReleaseSource source = new()
        {
            Files = _files,
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
        string sourceRoot = Path.Combine(_work, "linked-source");
        WriteFile(sourceRoot, "squad.yml", "linked corpus");
        _files.CreateFileLink(
            Path.Combine(sourceRoot, "linked.yml"),
            Path.Combine(sourceRoot, "squad.yml"));
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromDirectory(sourceRoot, _files);
        SquadReleaseRequest request = Request("linked");

        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAndExtractAsync(request));

        Assert.False(_files.FileExists(Path.Combine(request.DestinationPath, "linked.yml")));
    }

    [Fact]
    public async Task AnArchiveEntryThatEscapesTheDestinationIsRefused()
    {
        string archivePath = await WriteArchiveAsync(
            Path.Combine(_work, "escaping.zip"),
            ("../evil.txt", "escaped archive content"));
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromArchive(archivePath, _files);
        SquadReleaseRequest request = Request("escaping-archive");

        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAndExtractAsync(request));

        Assert.False(_files.FileExists(Path.Combine(_work, "evil.txt")));
    }

    [Fact]
    public async Task ASymbolicLinkArchiveEntryIsRefused()
    {
        string archivePath = await WriteArchiveAsync(
            Path.Combine(_work, "linked.zip"),
            ("linked.yml", "squad.yml", unchecked((int)(0xA000u << 16))));
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromArchive(archivePath, _files);
        SquadReleaseRequest request = Request("linked-archive");

        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAndExtractAsync(request));

        Assert.False(_files.FileExists(Path.Combine(request.DestinationPath, "linked.yml")));
    }

    [Fact]
    public async Task ADuplicateArchivePathIsRefused()
    {
        string archivePath = await WriteArchiveAsync(
            Path.Combine(_work, "duplicate.zip"),
            ("squad.yml", "first"),
            ("squad.yml", "second"));
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromArchive(archivePath, _files);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => source.DownloadAndExtractAsync(Request("duplicate-archive")));
    }

    [Fact]
    public async Task AFileContentPathThatEscapesTheDestinationIsRefused()
    {
        FakeSquadReleaseSource source = new()
        {
            Files = _files,
            FileContents = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["../escaped.txt"] = "escaped content",
            },
        };
        SquadReleaseRequest request = Request("escaping-content");

        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAndExtractAsync(request));

        Assert.False(_files.FileExists(Path.Combine(_work, "escaped.txt")));
    }

    [Fact]
    public async Task ARootedFileContentPathIsRefused()
    {
        string rootedPath = Path.Combine(_work, "rooted.txt");
        FakeSquadReleaseSource source = new()
        {
            Files = _files,
            FileContents = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [rootedPath] = "rooted content",
            },
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => source.DownloadAndExtractAsync(Request("rooted-content")));

        Assert.False(_files.FileExists(rootedPath));
    }

    [Theory]
    [InlineData("evil", "1.2.3")]
    [InlineData("owner/name/extra", "1.2.3")]
    [InlineData("owner/name", "not-a-version")]
    [InlineData("owner/name", "01.2.3")]
    public async Task AMalformedRequestIsRefusedLikeTheRealSource(string repository, string version)
    {
        FakeSquadReleaseSource source = new() { Files = _files };

        await Assert.ThrowsAsync<ArgumentException>(
            () => source.DownloadAndExtractAsync(
                new SquadReleaseRequest(repository, version, Path.Combine(_work, "malformed"))));

        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task AWhitespaceDestinationIsRefusedLikeTheRealSource()
    {
        FakeSquadReleaseSource source = new() { Files = _files };

        await Assert.ThrowsAsync<ArgumentException>(
            () => source.DownloadAndExtractAsync(new SquadReleaseRequest("owner/name", "1.2.3", "   ")));

        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task APrereleaseVersionIsAccepted()
    {
        FakeSquadReleaseSource source = new() { Files = _files };
        SquadReleaseRequest request = Request("prerelease", version: "1.2.3-rc.1");

        SquadReleaseResult result = await source.DownloadAndExtractAsync(request);

        Assert.Equal("1.2.3-rc.1", result.Version);
        Assert.Equal("kyber-squad-1.2.3-rc.1.zip", result.Asset.Name);
    }

    [Fact]
    public async Task TheAfterExtractHookSeesTheExtractedRootAndItsEditLands()
    {
        string sourceRoot = Path.Combine(_work, "padded-corpus");
        WriteFile(sourceRoot, "agents/conductor.md", "the conductor\n");
        FakeSquadReleaseSource source = new()
        {
            Files = _files,
            SourceDirectory = sourceRoot,
            AfterExtract = (root, _) =>
            {
                _files.AppendAllText(Path.Combine(root, "agents", "conductor.md"), "the padding\n");
                return Task.CompletedTask;
            },
        };
        SquadReleaseRequest request = Request("padded");

        await source.DownloadAndExtractAsync(request);

        Assert.Equal(
            "the conductor\nthe padding\n",
            _files.ReadAllText(Path.Combine(request.DestinationPath, "agents", "conductor.md")));
    }

    [Fact]
    public async Task FromArchiveExtractsThePackAndReportsTheArchivesOwnIdentity()
    {
        string archivePath = await WriteArchiveAsync(
            Path.Combine(_work, "kyber-squad-1.2.3.zip"),
            ("squad.yml", "packed"),
            ("skills/task-gate/SKILL.md", "packed skill"));
        byte[] archiveBytes = _files.ReadAllBytes(archivePath);
        FakeSquadReleaseSource source = FakeSquadReleaseSource.FromArchive(archivePath, _files);
        SquadReleaseRequest request = Request("unpacked");

        SquadReleaseResult result = await source.DownloadAndExtractAsync(request);

        Assert.Equal("packed", _files.ReadAllText(Path.Combine(request.DestinationPath, "squad.yml")));
        Assert.Equal(
            ["skills/task-gate/SKILL.md", "squad.yml"],
            result.ExtractedFiles);
        Assert.Equal("kyber-squad-1.2.3.zip", result.Asset.Name);
        Assert.Equal("https://localhost/kyber-squad-1.2.3.zip", result.Asset.DownloadUri.AbsoluteUri);
        Assert.Equal(_files.GetLength(archivePath), result.Asset.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(archiveBytes)), result.Checksum.Sha256);
    }

    [Fact]
    public async Task AnExplicitIdentityIsReportedUnchanged()
    {
        Uri downloadUri = new("https://example.com/asset.zip");
        FakeSquadReleaseSource source = new()
        {
            Files = _files,
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
        Assert.False(_files.DirectoryExists(request.DestinationPath));
    }

    [Fact]
    public async Task ACancelledRequestIsRefusedBeforeItIsRecorded()
    {
        FakeSquadReleaseSource source = new() { Files = _files };
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
            Path.Combine(_work, name));
    }

    private void WriteFile(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }

    private async Task<string> WriteArchiveAsync(
        string archivePath,
        params (string Name, string Content, int ExternalAttributes)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content, int externalAttributes) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                entry.ExternalAttributes = externalAttributes;
                await using Stream entryStream = await entry.OpenAsync();
                await entryStream.WriteAsync(Utf8NoBom(content));
            }
        }

        _files.WriteAllBytes(archivePath, stream.ToArray());
        return archivePath;
    }

    private async Task<string> WriteArchiveAsync(
        string archivePath,
        params (string Name, string Content)[] entries)
    {
        (string Name, string Content, int ExternalAttributes)[] expanded = entries
            .Select(static entry => (entry.Name, entry.Content, 0))
            .ToArray();
        return await WriteArchiveAsync(archivePath, expanded);
    }

    private Dictionary<string, byte[]> Snapshot(string root)
    {
        return _files.EnumerateFiles(root, "*", recursive: true)
            .ToDictionary(
                file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                _files.ReadAllBytes,
                StringComparer.Ordinal);
    }

    private static byte[] Utf8NoBom(string text)
    {
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
    }
}
