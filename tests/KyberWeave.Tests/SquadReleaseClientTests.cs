using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Release;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// K5 -- release inputs are untrusted until HTTPS transport, the exact published digest,
/// and every archive path have been verified. Prerequisite probes execute only the named
/// PATH tools and expose parsed versions without reflecting process secrets.
/// </summary>
public sealed class SquadReleaseClientTests : IDisposable
{
    private const string Version = "1.2.3";
    private const string AssetName = "kyber-squad-1.2.3.zip";
    private const string Repository = "dpalfery/kyber-weave";
    private const string Secret = "squad-secret-must-not-be-disclosed";
    private static readonly Uri ApiRoot = new("https://api.github.test/");
    private readonly TempDirectory _temp = new();

    [Fact]
    public async Task DownloadAndExtractAsync_WithExactPublishedChecksum_UsesTheReleasePortAndReturnsArchiveIdentity()
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        var checksum = Sha256(archiveBytes);
        using var handler = ReleaseHandler(archiveBytes, $"{checksum}  {AssetName}\n");
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(handler, ApiRoot);
        var destination = Path.Combine(_temp.Path, "squad");
        var request = new SquadReleaseRequest(Repository, Version, destination);

        var result = await source.DownloadAndExtractAsync(request, CancellationToken.None);

        Assert.Equal(Version, result.Version);
        Assert.Equal(AssetName, result.Asset.Name);
        Assert.Equal(new Uri("https://downloads.github.test/" + AssetName), result.Asset.DownloadUri);
        Assert.Equal(AssetName, result.Checksum.AssetName);
        Assert.Equal(checksum, result.Checksum.Sha256);
        Assert.Equal(Path.GetFullPath(destination), result.ExtractionRoot);
        Assert.Equal(["payload/manifest.json"], result.ExtractedFiles);
        Assert.Equal(
            "canonical",
            await File.ReadAllTextAsync(
                Path.Combine(destination, "payload", "manifest.json"),
                CancellationToken.None));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WithHttpApiOrigin_RejectsBeforeRequestOrFilesystemChange()
    {
        using var handler = new RoutingHandler();
        SeedDestinationAndState();
        var before = SnapshotTree();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            new Uri("http://api.github.test/"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.DownloadAndExtractAsync(Request(), CancellationToken.None));

        Assert.Contains("HTTPS", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
        AssertTreeUnchanged(before);
    }

    [Fact]
    public void Constructor_WithRedirectFollowingHandler_RejectsTheUnsafeTransportContract()
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new GitHubSquadReleaseSource(handler, ApiRoot));

        Assert.Contains("redirect", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_WithDefaultRedirectFollowingSocketsHandler_RejectsTheUnsafeTransportContract()
    {
        using var handler = new SocketsHttpHandler();
        Assert.True(handler.AllowAutoRedirect);

        var exception = Assert.Throws<ArgumentException>(() =>
            new GitHubSquadReleaseSource(handler, ApiRoot));

        Assert.Contains("redirect", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.2.3-beta.1")]
    [InlineData("1.2.3+build.1")]
    [InlineData("not-a-version")]
    [InlineData("01.2.3")]
    public async Task DownloadAndExtractAsync_WithNonStableReleaseVersion_RejectsBeforeHttpRequest(
        string version)
    {
        using var handler = new RoutingHandler();
        SeedDestinationAndState();
        var before = SnapshotTree();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(handler, ApiRoot);
        var request = new SquadReleaseRequest(
            Repository,
            version,
            Path.Combine(_temp.Path, "destination"));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            source.DownloadAndExtractAsync(request, CancellationToken.None));

        Assert.Contains("stable X.Y.Z", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        AssertTreeUnchanged(before);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WithHttpsRedirect_FollowsRedirectAndVerifiesFinalAsset()
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        var checksum = Sha256(archiveBytes);
        using var handler = ReleaseHandler(
            archiveBytes,
            $"{checksum}  {AssetName}\n",
            redirectArchiveTo: new Uri("https://objects.github.test/squad.zip"));
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(handler, ApiRoot);

        var result = await source.DownloadAndExtractAsync(Request(), CancellationToken.None);

        Assert.Equal(checksum, result.Checksum.Sha256);
        Assert.Contains(new Uri("https://objects.github.test/squad.zip"), handler.Requests);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WithRedirectDowngradeToHttp_RejectsBeforeFollowingOrChangingState()
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        var checksum = Sha256(archiveBytes);
        var insecureLocation = new Uri("http://objects.github.test/squad.zip");
        using var handler = ReleaseHandler(
            archiveBytes,
            $"{checksum}  {AssetName}\n",
            redirectArchiveTo: insecureLocation);
        SeedDestinationAndState();
        var before = SnapshotTree();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(handler, ApiRoot);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.DownloadAndExtractAsync(Request(), CancellationToken.None));

        Assert.Contains("HTTPS", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(insecureLocation, handler.Requests);
        AssertTreeUnchanged(before);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WhenReleaseOmitsChecksumAsset_RejectsBeforeArchiveRequestOrFilesystemChange()
    {
        var archiveUri = new Uri("https://downloads.github.test/" + AssetName);
        var releaseUri = new Uri(ApiRoot, $"repos/{Repository}/releases/tags/v{Version}");
        using var handler = new RoutingHandler();
        handler.Enqueue(releaseUri, () => JsonResponse($$"""
            {
              "tag_name": "v{{Version}}",
              "assets": [
                {
                  "name": "{{AssetName}}",
                  "browser_download_url": "{{archiveUri}}",
                  "size": 8192
                }
              ]
            }
            """));
        SeedDestinationAndState();
        var before = SnapshotTree();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(handler, ApiRoot);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.DownloadAndExtractAsync(Request(), CancellationToken.None));

        Assert.Contains("SHA256SUMS.txt", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(archiveUri, handler.Requests);
        AssertTreeUnchanged(before);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WhenChecksumHasOnlyLookalikeNames_RejectsMissingExactAssetRow()
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        var checksum = Sha256(archiveBytes);
        var manifest = $"{checksum}  nested/{AssetName}\n{checksum}  {AssetName}.sig\n";

        var exception = await DownloadFailure(archiveBytes, manifest);

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AssetName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" *")]
    public async Task DownloadAndExtractAsync_WhenChecksumRowDoesNotUseExactSha256sumTextFormat_RejectsIt(
        string separator)
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        var checksum = Sha256(archiveBytes);

        var exception = await DownloadFailure(
            archiveBytes,
            $"{checksum}{separator}{AssetName}\n");

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AssetName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WhenChecksumRowIsDuplicated_RejectsAmbiguousDigest()
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        var checksum = Sha256(archiveBytes);
        var manifest = $"{checksum}  {AssetName}\n{checksum}  {AssetName}\n";

        var exception = await DownloadFailure(archiveBytes, manifest);

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AssetName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AmbiguousChecksumManifests))]
    public async Task DownloadAndExtractAsync_WhenExactFilenameHasValidAndInvalidRows_RejectsAmbiguity(
        string manifest)
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        manifest = manifest.Replace("{digest}", Sha256(archiveBytes), StringComparison.Ordinal);

        var exception = await DownloadFailure(archiveBytes, manifest);

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AssetName, exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> AmbiguousChecksumManifests => new()
    {
        $"{{digest}}  {AssetName}\n{new string('g', 64)}  {AssetName}\n",
        $"{{digest}}  {AssetName}\n{{digest}} *{AssetName}\n"
    };

    [Fact]
    public async Task DownloadAndExtractAsync_WhenAssetDigestDiffers_RejectsBeforeExtraction()
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        var wrongChecksum = new string('0', 64);

        var exception = await DownloadFailure(archiveBytes, $"{wrongChecksum}  {AssetName}\n");

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AssetName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("nested/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    [InlineData("\\\\server\\share\\absolute.txt")]
    public async Task DownloadAndExtractAsync_WhenArchivePathEscapesOrIsRooted_RejectsWholeArchive(
        string entryName)
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"), (entryName, "escape"));

        var exception = await DownloadFailure(
            archiveBytes,
            $"{Sha256(archiveBytes)}  {AssetName}\n");

        Assert.Contains("archive", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("payload/real.txt")]
    [InlineData("../../outside.txt")]
    public async Task DownloadAndExtractAsync_WhenArchiveContainsSymlinkEntry_RejectsWholeArchive(
        string linkTarget)
    {
        var archiveBytes = CreateArchiveWithSymlink("payload/link", linkTarget);

        var exception = await DownloadFailure(
            archiveBytes,
            $"{Sha256(archiveBytes)}  {AssetName}\n");

        Assert.Contains("symbolic link", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WhenExistingDestinationDirectoryIsSymlink_RejectsEscapeWithoutFollowingIt()
    {
        if (OperatingSystem.IsWindows()) return; // CI runs Linux; the containment rule is platform-independent.

        var destination = Path.Combine(_temp.Path, "destination");
        var outside = Path.Combine(_temp.Path, "outside");
        var state = Path.Combine(_temp.Path, ".kyber-weave");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(state);
        var outsideFile = Path.Combine(outside, "manifest.json");
        await File.WriteAllTextAsync(
            outsideFile,
            "outside must stay unchanged",
            CancellationToken.None);
        var linkedDirectory = Path.Combine(destination, "payload");
        Directory.CreateSymbolicLink(linkedDirectory, outside);
        await File.WriteAllTextAsync(
            Path.Combine(state, "squad.lock.yml"),
            "preserve lock",
            CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(state, "squad.receipt.json"),
            "preserve receipt",
            CancellationToken.None);
        var before = SnapshotTree();
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        using var handler = ReleaseHandler(archiveBytes, $"{Sha256(archiveBytes)}  {AssetName}\n");
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(handler, ApiRoot);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            source.DownloadAndExtractAsync(Request(), CancellationToken.None));

        Assert.Contains("symbolic link", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "outside must stay unchanged",
            await File.ReadAllTextAsync(outsideFile, CancellationToken.None));
        Assert.Equal(outside, new DirectoryInfo(linkedDirectory).LinkTarget);
        AssertTreeUnchanged(before);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WhenArchiveIsInvalid_RejectsWithoutDestinationOrStateChanges()
    {
        var invalidArchive = Encoding.UTF8.GetBytes("not a zip archive");

        var exception = await DownloadFailure(
            invalidArchive,
            $"{Sha256(invalidArchive)}  {AssetName}\n");

        Assert.Contains("archive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WhenCancellationArrivesBeforeArchiveValidation_CancelsWithoutPublishing()
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        using var cancellation = new CancellationTokenSource();
        using var handler = ReleaseHandler(
            archiveBytes,
            $"{Sha256(archiveBytes)}  {AssetName}\n",
            cancelAfterArchiveRead: cancellation);
        SeedStateOnly();
        var before = SnapshotTree();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(handler, ApiRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.DownloadAndExtractAsync(Request(), cancellation.Token));

        AssertTreeUnchanged(before);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WhenCancellationArrivesAfterStagingStarts_CancelsBeforePublish()
    {
        var archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        using var cancellation = new CancellationTokenSource();
        var stagingCreated = false;
        using var handler = ReleaseHandler(
            archiveBytes,
            $"{Sha256(archiveBytes)}  {AssetName}\n");
        SeedStateOnly();
        var before = SnapshotTree();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            ApiRoot,
            onStagingCreated: () =>
            {
                stagingCreated = true;
                cancellation.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.DownloadAndExtractAsync(Request(), cancellation.Token));

        Assert.True(stagingCreated);
        AssertTreeUnchanged(before);
        Assert.Empty(Directory.EnumerateDirectories(
            _temp.Path,
            ".destination.kyber-squad-*",
            SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("apm, version 1.2.3", "1.2.3")]
    [InlineData("apm, version 1.2.3-beta.2+build.7", "1.2.3-beta.2+build.7")]
    public void ApmProcessProbe_WithExactSemanticVersion_UsesBarePathExecutableAndReturnsExactVersion(
        string standardOutput,
        string expectedVersion)
    {
        var executor = new RecordingProcessExecutor(new ProcessResult(0, standardOutput + "\n", string.Empty));
        var probe = new ApmProcessProbe(executor);

        var result = probe.Probe();

        Assert.True(result.IsAvailable);
        Assert.Equal(expectedVersion, result.Version);
        Assert.Null(result.FailureReason);
        AssertPathOnlyVersionInvocation(executor, "apm");
    }

    [Fact]
    public void McpProcessProbe_WithExactSemanticVersion_UsesBarePathExecutableAndReturnsExactVersion()
    {
        var executor = new RecordingProcessExecutor(
            new ProcessResult(0, "kyber-weave-mcp 1.2.3\n", string.Empty));
        var probe = new McpProcessProbe(executor);

        var result = probe.Probe();

        Assert.True(result.IsAvailable);
        Assert.Equal(Version, result.Version);
        Assert.Null(result.FailureReason);
        AssertPathOnlyVersionInvocation(executor, "kyber-weave-mcp");
    }

    [Theory]
    [InlineData("apm, version 1.2")]
    [InlineData("apm, version 1.2.3.4")]
    [InlineData("apm, version v1.2.3")]
    [InlineData("apm, version 1.2.3 trailing")]
    [InlineData("noise\napm, version 1.2.3")]
    public void ApmProcessProbe_WhenOutputIsNotTheExactToolEnvelopeAndSemanticVersion_RejectsIt(
        string standardOutput)
    {
        var executor = new RecordingProcessExecutor(new ProcessResult(0, standardOutput, string.Empty));
        var probe = new ApmProcessProbe(executor);

        var result = probe.Probe();

        Assert.True(result.IsAvailable);
        Assert.Null(result.Version);
        Assert.NotNull(result.FailureReason);
        AssertPathOnlyVersionInvocation(executor, "apm");
    }

    [Theory]
    [InlineData("kyber-weave-mcp 1.2")]
    [InlineData("kyber-weave-mcp v1.2.3")]
    [InlineData("kyber-weave-mcp 1.2.3 extra")]
    [InlineData("kyber-weave 1.2.3")]
    public void McpProcessProbe_WhenOutputIsNotTheExactToolEnvelopeAndSemanticVersion_RejectsIt(
        string standardOutput)
    {
        var executor = new RecordingProcessExecutor(new ProcessResult(0, standardOutput, string.Empty));
        var probe = new McpProcessProbe(executor);

        var result = probe.Probe();

        Assert.True(result.IsAvailable);
        Assert.Null(result.Version);
        Assert.NotNull(result.FailureReason);
        AssertPathOnlyVersionInvocation(executor, "kyber-weave-mcp");
    }

    [Fact]
    public void ProcessProbes_WhenToolIsMissing_ReportUnavailableWithoutDisclosingStartupDetails()
    {
        var executor = new RecordingProcessExecutor(
            new Win32Exception($"PATH={Secret}; Authorization=Bearer {Secret}"));
        var probe = new ApmProcessProbe(executor);

        var result = probe.Probe();

        Assert.False(result.IsAvailable);
        Assert.Null(result.Version);
        Assert.NotNull(result.FailureReason);
        Assert.DoesNotContain(Secret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessProbes_WhenToolFails_DoNotExposeStandardStreamsEnvironmentOrAuthorization()
    {
        var executor = new RecordingProcessExecutor(
            new ProcessResult(
                17,
                $"GH_TOKEN={Secret}",
                $"Authorization: Bearer {Secret}"));
        var probe = new McpProcessProbe(executor);

        var result = probe.Probe();

        Assert.True(result.IsAvailable);
        Assert.Null(result.Version);
        Assert.NotNull(result.FailureReason);
        Assert.DoesNotContain(Secret, result.ToString(), StringComparison.Ordinal);
        AssertPathOnlyVersionInvocation(executor, "kyber-weave-mcp");
    }

    public void Dispose() => _temp.Dispose();

    private SquadReleaseRequest Request() =>
        new(Repository, Version, Path.Combine(_temp.Path, "destination"));

    private async Task<Exception> DownloadFailure(byte[] archiveBytes, string checksumManifest)
    {
        SeedDestinationAndState();
        var before = SnapshotTree();
        using var handler = ReleaseHandler(archiveBytes, checksumManifest);
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(handler, ApiRoot);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            source.DownloadAndExtractAsync(Request(), CancellationToken.None));

        AssertTreeUnchanged(before);
        return exception;
    }

    private void SeedDestinationAndState()
    {
        var destination = Path.Combine(_temp.Path, "destination");
        Directory.CreateDirectory(destination);
        SeedStateOnly();
        File.WriteAllText(Path.Combine(destination, "unmanaged.txt"), "preserve destination");
    }

    private void SeedStateOnly()
    {
        var state = Path.Combine(_temp.Path, ".kyber-weave");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "squad.lock.yml"), "preserve lock");
        File.WriteAllText(Path.Combine(state, "squad.receipt.json"), "preserve receipt");
    }

    private IReadOnlyDictionary<string, byte[]> SnapshotTree() =>
        Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(_temp.Path, path).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private void AssertTreeUnchanged(IReadOnlyDictionary<string, byte[]> before)
    {
        var after = SnapshotTree();
        Assert.Equal(before.Keys, after.Keys);
        foreach (var path in before.Keys)
            Assert.Equal(before[path], after[path]);
    }

    private static void AssertPathOnlyVersionInvocation(
        RecordingProcessExecutor executor,
        string expectedExecutable)
    {
        var startInfo = Assert.IsType<ProcessStartInfo>(executor.StartInfo);
        Assert.Equal(expectedExecutable, startInfo.FileName);
        Assert.False(Path.IsPathFullyQualified(startInfo.FileName));
        Assert.DoesNotContain(Path.DirectorySeparatorChar, startInfo.FileName);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, startInfo.FileName);
        Assert.Equal(["--version"], startInfo.ArgumentList);
        Assert.Equal(string.Empty, executor.StandardInput);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(string.IsNullOrEmpty(startInfo.WorkingDirectory));
        Assert.False(startInfo.Environment.ContainsKey("KYBER_SQUAD_TEST_AUTHORIZATION"));
    }

    private static RoutingHandler ReleaseHandler(
        byte[] archiveBytes,
        string checksumManifest,
        Uri? redirectArchiveTo = null,
        CancellationTokenSource? cancelAfterArchiveRead = null)
    {
        var releaseUri = new Uri(ApiRoot, $"repos/{Repository}/releases/tags/v{Version}");
        var checksumUri = new Uri("https://downloads.github.test/SHA256SUMS.txt");
        var archiveUri = new Uri("https://downloads.github.test/" + AssetName);
        var handler = new RoutingHandler();
        handler.Enqueue(
            releaseUri,
            () => JsonResponse(ReleaseJson(checksumUri, archiveUri)));
        handler.Enqueue(
            checksumUri,
            () => BytesResponse(Encoding.UTF8.GetBytes(checksumManifest), "text/plain"));

        if (redirectArchiveTo is null)
        {
            handler.Enqueue(
                archiveUri,
                () => cancelAfterArchiveRead is null
                    ? BytesResponse(archiveBytes, "application/zip")
                    : CancellationResponse(archiveBytes, cancelAfterArchiveRead));
        }
        else
        {
            handler.Enqueue(archiveUri, () => RedirectResponse(redirectArchiveTo));
            if (redirectArchiveTo.Scheme == Uri.UriSchemeHttps)
            {
                handler.Enqueue(
                    redirectArchiveTo,
                    () => BytesResponse(archiveBytes, "application/zip"));
            }
        }

        return handler;
    }

    private static string ReleaseJson(Uri checksumUri, Uri archiveUri) => $$"""
        {
          "tag_name": "v{{Version}}",
          "assets": [
            {
              "name": "SHA256SUMS.txt",
              "browser_download_url": "{{checksumUri}}",
              "size": 4096
            },
            {
              "name": "{{AssetName}}",
              "browser_download_url": "{{archiveUri}}",
              "size": 8192
            }
          ]
        }
        """;

    private static HttpResponseMessage JsonResponse(string json) =>
        BytesResponse(Encoding.UTF8.GetBytes(json), "application/json");

    private static HttpResponseMessage BytesResponse(byte[] bytes, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) }
        }
    };

    private static HttpResponseMessage RedirectResponse(Uri location) => new(HttpStatusCode.Redirect)
    {
        Headers = { Location = location }
    };

    private static HttpResponseMessage CancellationResponse(
        byte[] bytes,
        CancellationTokenSource cancellation) => new(HttpStatusCode.OK)
    {
        Content = new CancelAfterSerializationContent(bytes, cancellation)
        {
            Headers =
            {
                ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip")
            }
        }
    };

    private static byte[] CreateArchive(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateArchiveWithSymlink(string name, string target)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(name);
            entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(target);
        }

        return stream.ToArray();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, Queue<Func<HttpResponseMessage>>> _responses = [];

        public List<Uri> Requests { get; } = [];

        public void Enqueue(Uri uri, Func<HttpResponseMessage> responseFactory)
        {
            if (!_responses.TryGetValue(uri, out var queue))
            {
                queue = new Queue<Func<HttpResponseMessage>>();
                _responses.Add(uri, queue);
            }

            queue.Enqueue(responseFactory);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.RequestUri);
            Requests.Add(request.RequestUri);
            if (!_responses.TryGetValue(request.RequestUri, out var queue) || queue.Count == 0)
                throw new Xunit.Sdk.XunitException($"Unexpected HTTP request: {request.RequestUri}");

            return Task.FromResult(queue.Dequeue()());
        }
    }

    private sealed class CancelAfterSerializationContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly CancellationTokenSource _cancellation;

        public CancelAfterSerializationContent(
            byte[] bytes,
            CancellationTokenSource cancellation)
        {
            _bytes = bytes;
            _cancellation = cancellation;
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            await stream.WriteAsync(_bytes, CancellationToken.None);
            await _cancellation.CancelAsync();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }

    private sealed class RecordingProcessExecutor : IProcessExecutor
    {
        private readonly ProcessResult _result;
        private readonly Exception? _exception;

        public RecordingProcessExecutor(ProcessResult result) => _result = result;

        public RecordingProcessExecutor(Exception exception) => _exception = exception;

        public ProcessStartInfo? StartInfo { get; private set; }

        public string? StandardInput { get; private set; }

        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
        {
            StartInfo = startInfo;
            StandardInput = standardInput;
            if (_exception is not null)
                throw _exception;

            return _result;
        }
    }
}
