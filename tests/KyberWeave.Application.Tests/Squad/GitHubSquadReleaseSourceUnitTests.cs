using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Proves <see cref="GitHubSquadReleaseSource"/> through its file-system, path, environment and identifier seams and a
/// scripted transport: no test here touches a real directory or opens a socket.
/// </summary>
public sealed class GitHubSquadReleaseSourceUnitTests
{
    private const string Version = "1.2.3";
    private const string AssetName = "kyber-squad-1.2.3.zip";
    private const string Repository = "dpalfery/kyber-weave";
    private const string Work = "/work";
    private const string Destination = "/work/squad";
    private static readonly Uri ApiRoot = new("https://api.github.test/");

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    public GitHubSquadReleaseSourceUnitTests() => _files.CreateDirectory(Work);

    [Fact]
    public async Task ExtractionPublishesTheArchiveTreeAndListsItsFilesInOrder()
    {
        byte[] archive = Zip(("payload/b.txt", "b"), ("payload/a.txt", "a"), ("docs/", null));

        SquadReleaseResult result = await Download(archive);

        Assert.Equal(Destination, result.ExtractionRoot);
        Assert.Equal(["payload/a.txt", "payload/b.txt"], result.ExtractedFiles);
        Assert.Equal("a", _files.ReadAllText("/work/squad/payload/a.txt"));
        Assert.Equal("b", _files.ReadAllText("/work/squad/payload/b.txt"));
        Assert.True(_files.DirectoryExists("/work/squad/docs"));
        Assert.Empty(StagingDirectories());
    }

    [Fact]
    public async Task ARelativeDestinationResolvesAgainstTheEnvironmentWorkingDirectory()
    {
        SquadReleaseResult result = await Download(
            Zip(("payload/a.txt", "a")),
            request: new SquadReleaseRequest(Repository, Version, "squad"));

        Assert.Equal(Destination, result.ExtractionRoot);
        Assert.True(_files.FileExists("/work/squad/payload/a.txt"));
    }

    [Fact]
    public async Task TheStagingDirectoryIsNamedFromTheInjectedIdentifier()
    {
        string? stagingSeen = null;

        _ = await Download(
            Zip(("payload/a.txt", "a")),
            onStagingCreated: () => stagingSeen = Assert.Single(StagingDirectories()));

        Assert.Equal("/work/.squad.kyber-squad-00000001000000000000000000000000", stagingSeen);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("nested/../../escape.txt")]
    [InlineData("nested/./file.txt")]
    [InlineData("nested//file.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    [InlineData("\\\\server\\share\\absolute.txt")]
    public async Task AnEscapingOrRootedEntryRejectsTheWholeArchiveAndLeavesNothingBehind(string entryName)
    {
        Exception error = await DownloadFailure(Zip(("payload/ok.txt", "ok"), (entryName, "escape")));

        Assert.IsType<InvalidDataException>(error);
        Assert.Contains("path", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNothingPublished();
    }

    [Theory]
    [InlineData("payload/real.txt")]
    [InlineData("../../outside.txt")]
    public async Task ASymbolicLinkEntryRejectsTheWholeArchive(string linkTarget)
    {
        Exception error = await DownloadFailure(ZipWithSymbolicLink("payload/link", linkTarget));

        Assert.IsType<InvalidDataException>(error);
        Assert.Contains("symbolic link", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNothingPublished();
    }

    [Fact]
    public async Task AWindowsReparsePointEntryRejectsTheWholeArchive()
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry("payload/junction");
            entry.ExternalAttributes = (int)FileAttributes.ReparsePoint;
        }

        Exception error = await DownloadFailure(stream.ToArray());

        Assert.Contains("symbolic link", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNothingPublished();
    }

    [Fact]
    public async Task ADuplicateEntryRejectsTheWholeArchive()
    {
        Exception error = await DownloadFailure(Zip(("payload/a.txt", "one"), ("payload/a.txt", "two")));

        Assert.Contains("duplicate path 'payload/a.txt'", error.Message, StringComparison.Ordinal);
        AssertNothingPublished();
    }

    [Fact]
    public async Task EntriesThatDifferOnlyByCaseAreDistinctOnACaseSensitiveHost()
    {
        SquadReleaseResult result = await Download(Zip(("payload/a.txt", "lower"), ("payload/A.txt", "upper")));

        Assert.Equal(["payload/A.txt", "payload/a.txt"], result.ExtractedFiles);
    }

    [Fact]
    public async Task AnEntryBelowAFileRejectsTheWholeArchive()
    {
        Exception error = await DownloadFailure(Zip(("payload", "file"), ("payload/inner.txt", "x")));

        Assert.Contains("descends through file 'payload'", error.Message, StringComparison.Ordinal);
        AssertNothingPublished();
    }

    [Fact]
    public async Task AFileOverADirectoryRejectsTheWholeArchive()
    {
        Exception error = await DownloadFailure(Zip(("payload/inner.txt", "x"), ("payload", "file")));

        Assert.Contains("conflicts with a directory", error.Message, StringComparison.Ordinal);
        AssertNothingPublished();
    }

    [Fact]
    public async Task AnUnreadableArchiveIsReportedAsInvalid()
    {
        Exception error = await DownloadFailure("not a zip"u8.ToArray());

        Assert.IsType<InvalidDataException>(error);
        Assert.Contains("archive", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNothingPublished();
    }

    [Fact]
    public async Task AnExistingDestinationIsRefusedAndKeptIntact()
    {
        _files.CreateDirectory(Destination);
        _files.WriteAllText("/work/squad/keep.txt", "keep");

        Exception error = await DownloadFailure(Zip(("payload/a.txt", "a")), expectUntouchedDestination: false);

        Assert.IsType<IOException>(error);
        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        Assert.Equal("keep", _files.ReadAllText("/work/squad/keep.txt"));
        Assert.Empty(StagingDirectories());
    }

    [Fact]
    public async Task AMissingParentDirectoryIsRefused()
    {
        SquadReleaseRequest request = new(Repository, Version, "/absent/squad");

        Exception error = await DownloadFailure(Zip(("payload/a.txt", "a")), request: request);

        Assert.IsType<DirectoryNotFoundException>(error);
        Assert.False(_files.DirectoryExists("/absent"));
    }

    [Fact]
    public async Task ADestinationThatIsASymbolicLinkIsRefusedWithoutFollowingIt()
    {
        _files.CreateDirectory("/work/outside");
        _files.CreateDirectoryLink(Destination, "/work/outside");

        Exception error = await DownloadFailure(Zip(("payload/a.txt", "a")), expectUntouchedDestination: false);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("symbolic link", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_files.EnumerateEntries("/work/outside"));
    }

    [Fact]
    public async Task ADestinationThatIsADanglingSymbolicLinkIsRefused()
    {
        _files.CreateDirectoryLink(Destination, "/work/missing");

        Exception error = await DownloadFailure(Zip(("payload/a.txt", "a")), expectUntouchedDestination: false);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("symbolic link", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASymbolicLinkBelowAnExistingDestinationIsRefused()
    {
        _files.CreateDirectory("/work/outside");
        _files.CreateDirectory("/work/squad/nested");
        _files.CreateDirectoryLink("/work/squad/nested/link", "/work/outside");

        Exception error = await DownloadFailure(Zip(("payload/a.txt", "a")), expectUntouchedDestination: false);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("symbolic link", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_files.EnumerateEntries("/work/outside"));
    }

    [Fact]
    public async Task AFailureWhilePublishingRemovesTheStagingDirectory()
    {
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.MoveDirectory), new IOException("disk full"));

        Exception error = await DownloadFailure(Zip(("payload/a.txt", "a")));

        Assert.Equal("disk full", error.Message);
        AssertNothingPublished();
    }

    [Fact]
    public async Task CancellationAfterStagingStartsPublishesNothing()
    {
        using CancellationTokenSource cancellation = new();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download(
            Zip(("payload/a.txt", "a")),
            onStagingCreated: cancellation.Cancel,
            cancellationToken: cancellation.Token));

        AssertNothingPublished();
    }

    [Fact]
    public async Task ALoopbackHttpApiRootIsAllowedForALocalOrigin()
    {
        byte[] archive = Zip(("payload/a.txt", "a"));
        Uri loopback = new("http://127.0.0.1:5000/");
        RoutingHandler handler = ReleaseHandler(loopback, archive, checksumHost: "http://127.0.0.1:5000/assets");
        using GitHubSquadReleaseSource source = Create(handler, loopback);

        SquadReleaseResult result = await source.DownloadAndExtractAsync(DefaultRequest(), CancellationToken.None);

        Assert.Equal(["payload/a.txt"], result.ExtractedFiles);
    }

    [Fact]
    public async Task ALoopbackOriginStillRefusesARedirectToPlainHttpElsewhere()
    {
        byte[] archive = Zip(("payload/a.txt", "a"));
        Uri loopback = new("http://127.0.0.1:5000/");
        Uri elsewhere = new("http://objects.github.test/squad.zip");
        RoutingHandler handler = ReleaseHandler(loopback, archive, checksumHost: "http://127.0.0.1:5000/assets", redirectArchiveTo: elsewhere);
        using GitHubSquadReleaseSource source = Create(handler, loopback);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.DownloadAndExtractAsync(DefaultRequest(), CancellationToken.None));

        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(elsewhere, handler.Requests);
    }

    [Fact]
    public async Task AnApiRootOverPlainHttpOffLoopbackIsRefusedBeforeAnyRequest()
    {
        RoutingHandler handler = new();
        using GitHubSquadReleaseSource source = Create(handler, new Uri("http://api.github.test/"));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.DownloadAndExtractAsync(DefaultRequest(), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheTokenComesFromTheEnvironmentSeamAndGoesToTheApiHostOnly()
    {
        byte[] archive = Zip(("payload/a.txt", "a"));
        RoutingHandler handler = ReleaseHandler(ApiRoot, archive);
        using GitHubSquadReleaseSource source = Create(
            handler,
            ApiRoot,
            variable => variable == "GH_TOKEN" ? " gh-token " : null);

        _ = await source.DownloadAndExtractAsync(DefaultRequest(), CancellationToken.None);

        Assert.Equal("Bearer gh-token", handler.Authorizations[new Uri(ApiRoot, $"repos/{Repository}/releases/tags/v{Version}")]);
        Assert.All(
            handler.Authorizations.Where(pair => pair.Key.Host != ApiRoot.Host),
            pair => Assert.Null(pair.Value));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("0.1.6-rc.4", true)]
    [InlineData("1.2.3+build.1", false)]
    [InlineData("01.2.3", false)]
    [InlineData("1.2", false)]
    [InlineData("v1.2.3", false)]
    [InlineData("not-a-version", false)]
    public void OnlyAStrictSemanticVersionIsAReleaseVersion(string version, bool expected)
    {
        Assert.Equal(expected, GitHubSquadReleaseSource.IsValidReleaseVersion(version));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("owner/name/extra")]
    [InlineData("/name")]
    [InlineData("owner/ ")]
    public async Task ARepositoryThatIsNotOwnerSlashNameIsRefusedBeforeAnyRequest(string repository)
    {
        RoutingHandler handler = new();
        using GitHubSquadReleaseSource source = Create(handler, ApiRoot);

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => source.DownloadAndExtractAsync(
            new SquadReleaseRequest(repository, Version, Destination),
            CancellationToken.None));

        Assert.Contains("owner/name", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AReleaseTaggedForAnotherVersionIsRefused()
    {
        RoutingHandler handler = ReleaseHandler(ApiRoot, Zip(("payload/a.txt", "a")), tag: "v9.9.9");
        using GitHubSquadReleaseSource source = Create(handler, ApiRoot);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => source.DownloadAndExtractAsync(DefaultRequest(), CancellationToken.None));

        Assert.Contains("v9.9.9", error.Message, StringComparison.Ordinal);
        AssertNothingPublished();
    }

    private static SquadReleaseRequest DefaultRequest() => new(Repository, Version, Destination);

    private async Task<SquadReleaseResult> Download(
        byte[] archive,
        SquadReleaseRequest? request = null,
        Action? onStagingCreated = null,
        CancellationToken cancellationToken = default)
    {
        using RoutingHandler handler = ReleaseHandler(ApiRoot, archive);
        using GitHubSquadReleaseSource source = Create(handler, ApiRoot, onStagingCreated: onStagingCreated);
        return await source.DownloadAndExtractAsync(request ?? DefaultRequest(), cancellationToken);
    }

    private async Task<Exception> DownloadFailure(
        byte[] archive,
        SquadReleaseRequest? request = null,
        bool expectUntouchedDestination = true)
    {
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => Download(archive, request));
        if (expectUntouchedDestination)
            Assert.False(_files.PathExists(Destination));
        return error;
    }

    private GitHubSquadReleaseSource Create(
        HttpMessageHandler handler,
        Uri apiRoot,
        Func<string, string?>? readVariable = null,
        Action? onStagingCreated = null) =>
        new(
            handler,
            apiRoot,
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(currentDirectory: Work, readVariable: readVariable),
            new FakeIdGenerator(),
            onStagingCreated);

    private IEnumerable<string> StagingDirectories() =>
        _files.EnumerateDirectories(Work).Where(path => Path.GetFileName(path).StartsWith(".squad.kyber-squad-", StringComparison.Ordinal));

    private void AssertNothingPublished()
    {
        Assert.False(_files.PathExists(Destination));
        Assert.Empty(StagingDirectories());
    }

    private static RoutingHandler ReleaseHandler(
        Uri apiRoot,
        byte[] archive,
        string checksumHost = "https://downloads.github.test",
        Uri? redirectArchiveTo = null,
        string tag = "v" + Version)
    {
        Uri releaseUri = new(apiRoot, $"repos/{Repository}/releases/tags/v{Version}");
        Uri checksumUri = new($"{checksumHost}/SHA256SUMS.txt");
        Uri archiveUri = new($"{checksumHost}/{AssetName}");
        string manifest = $"{Convert.ToHexStringLower(SHA256.HashData(archive))}  {AssetName}\n";
        string json = $$"""
            {"tag_name":"{{tag}}","assets":[
              {"name":"SHA256SUMS.txt","browser_download_url":"{{checksumUri}}","size":100},
              {"name":"{{AssetName}}","browser_download_url":"{{archiveUri}}","size":200}]}
            """;
        RoutingHandler handler = new();
        handler.Enqueue(releaseUri, () => Ok(Encoding.UTF8.GetBytes(json)));
        handler.Enqueue(checksumUri, () => Ok(Encoding.UTF8.GetBytes(manifest)));
        if (redirectArchiveTo is null)
        {
            handler.Enqueue(archiveUri, () => Ok(archive));
        }
        else
        {
            handler.Enqueue(archiveUri, () => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = redirectArchiveTo } });
        }

        return handler;
    }

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static byte[] Zip(params (string Name, string? Content)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string? content) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                if (content is null)
                    continue;

                using StreamWriter writer = new(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] ZipWithSymbolicLink(string name, string target)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            using StreamWriter writer = new(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(target);
        }

        return stream.ToArray();
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, Queue<Func<HttpResponseMessage>>> _responses = [];

        public List<Uri> Requests { get; } = [];

        public Dictionary<Uri, string?> Authorizations { get; } = [];

        public void Enqueue(Uri uri, Func<HttpResponseMessage> response)
        {
            if (!_responses.TryGetValue(uri, out Queue<Func<HttpResponseMessage>>? queue))
                _responses[uri] = queue = new Queue<Func<HttpResponseMessage>>();

            queue.Enqueue(response);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            Requests.Add(uri);
            Authorizations[uri] = request.Headers.Authorization?.ToString();
            if (!_responses.TryGetValue(uri, out Queue<Func<HttpResponseMessage>>? queue) || queue.Count == 0)
                throw new XunitException($"Unexpected HTTP request: {uri}");

            return Task.FromResult(queue.Dequeue()());
        }
    }
}
