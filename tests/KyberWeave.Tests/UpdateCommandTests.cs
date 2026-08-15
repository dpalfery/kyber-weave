using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Update;
using KyberWeave.Cli.Update;
using Xunit;

namespace KyberWeave.Tests;

public sealed class UpdateCommandTests : IDisposable
{
    private readonly TempDirectory _install = new();
    private readonly TempDirectory _assets = new();

    public void Dispose()
    {
        _install.Dispose();
        _assets.Dispose();
    }

    [Theory]
    [InlineData(false, true, false, Architecture.X64, "linux-x64")]
    [InlineData(false, true, false, Architecture.Arm64, "linux-arm64")]
    [InlineData(false, false, true, Architecture.X64, "osx-x64")]
    [InlineData(false, false, true, Architecture.Arm64, "osx-arm64")]
    [InlineData(true, false, false, Architecture.X64, "win-x64")]
    public void PlatformRid_Detect_MapsPublishedCombinations(
        bool windows, bool linux, bool macos, Architecture architecture, string expected)
    {
        Assert.Equal(expected, PlatformRid.Detect(windows, linux, macos, architecture));
    }

    [Fact]
    public void PlatformRid_Detect_RejectsWinArm64()
    {
        var ex = Assert.Throws<SelfUpdateException>(
            () => PlatformRid.Detect(windows: true, linux: false, macos: false, Architecture.Arm64));
        Assert.Contains("win-arm64", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2.0-rc.1", "0.2.0-rc.1")]
    [InlineData("0.1.0+714f187ab97d66e1199c33d5aaa0c9ab76ffae0f", "0.1.0")]
    [InlineData("V0.2.0-dev.1", "0.2.0-dev.1")]
    public void ReleaseVersion_Normalize_StripsPrefixAndMetadata(string input, string expected)
    {
        Assert.Equal(expected, ReleaseVersion.Normalize(input));
    }

    [Fact]
    public void ReleaseVersion_Normalize_RejectsPathFragments()
    {
        Assert.Throws<SelfUpdateException>(() => ReleaseVersion.Normalize("../0.2.0"));
        Assert.Throws<SelfUpdateException>(() => ReleaseVersion.Normalize("0.2.0/evil"));
    }

    [Fact]
    public void ChecksumVerifier_ExpectedHex_MatchesExactFileNameOnly()
    {
        var sums = """
            aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  kyber-weave-linux-x64.tar.gz
            bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  kyber-weave-linux-arm64.tar.gz
            """;

        Assert.Equal(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ChecksumVerifier.ExpectedHex(sums, "kyber-weave-linux-x64.tar.gz"));
        Assert.Equal(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            ChecksumVerifier.ExpectedHex(sums, "kyber-weave-linux-arm64.tar.gz"));
    }

    [Fact]
    public void ChecksumVerifier_ExpectedHex_AcceptsStarAndPathPrefix()
    {
        var sums = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc *dist/kyber-weave-osx-arm64.tar.gz";
        Assert.Equal(
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            ChecksumVerifier.ExpectedHex(sums, "kyber-weave-osx-arm64.tar.gz"));
    }

    [Fact]
    public void ChecksumVerifier_ExpectedHex_MissingEntry_Throws()
    {
        var ex = Assert.Throws<SelfUpdateException>(
            () => ChecksumVerifier.ExpectedHex("deadbeef  other.tar.gz\n", "kyber-weave-osx-arm64.tar.gz"));
        Assert.Contains("no entry", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksumVerifier_Verify_Mismatch_Throws()
    {
        var actual = SHA256.HashData("nope"u8.ToArray());
        var ex = Assert.Throws<SelfUpdateException>(
            () => ChecksumVerifier.Verify(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                actual,
                "kyber-weave-osx-arm64.tar.gz"));
        Assert.Contains("SHA256 mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureHttps_RejectsHttp()
    {
        var ex = Assert.Throws<SelfUpdateException>(
            () => GitHubReleaseClient.EnsureHttps(new Uri("http://example.com/file")));
        Assert.Contains("non-HTTPS", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_LatestStable_UsesReleasesLatestAndReplacesBothBinaries()
    {
        using var handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.2.0","draft":false}""");
        var host = CreateHost("0.1.0", "osx-arm64");

        var outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("updated kyber-weave 0.2.0", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("new-cli", File.ReadAllText(host.ProcessPath));
        Assert.Equal(
            "new-mcp",
            File.ReadAllText(Path.Combine(_install.Path, "kyber-weave-mcp")));
        Assert.Contains(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            handler.Uris,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            GitHubReleaseClient.ReleasesApi.AbsoluteUri,
            handler.Uris,
            StringComparer.Ordinal);
    }

    [Fact]
    public void Run_ReleaseCandidate_SkipsDraftsAndUsesReleasesList()
    {
        using var handler = MapRelease("0.2.0-rc.1", "osx-arm64", windows: false);
        handler.MapJson(
            GitHubReleaseClient.ReleasesApi.AbsoluteUri,
            """
            [
              {"tag_name":"v0.9.0-rc.9","draft":true,"prerelease":true},
              {"tag_name":"v0.2.0-rc.1","draft":false,"prerelease":true},
              {"tag_name":"v0.1.1","draft":false,"prerelease":false}
            ]
            """);
        var host = CreateHost("0.1.0", "osx-arm64");

        var outcome = Run(handler, host, new SelfUpdateOptions(null, ReleaseCandidate: true, NoMcp: false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("0.2.0-rc.1", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(GitHubReleaseClient.ReleasesApi.AbsoluteUri, handler.Uris, StringComparer.Ordinal);
        Assert.DoesNotContain(GitHubReleaseClient.LatestApi.AbsoluteUri, handler.Uris, StringComparer.Ordinal);
    }

    [Fact]
    public void Run_PinnedVersion_SkipsApiAndStripsVPrefix()
    {
        using var handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        var host = CreateHost("0.1.0", "osx-arm64");

        var outcome = Run(handler, host, new SelfUpdateOptions("v0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("new-cli", File.ReadAllText(host.ProcessPath));
        Assert.DoesNotContain(GitHubReleaseClient.LatestApi.AbsoluteUri, handler.Uris, StringComparer.Ordinal);
        Assert.DoesNotContain(GitHubReleaseClient.ReleasesApi.AbsoluteUri, handler.Uris, StringComparer.Ordinal);
    }

    [Fact]
    public void Run_ReleaseCandidateWithVersion_FailsWithoutHttp()
    {
        using var handler = new MapHandler();
        var host = CreateHost("0.1.0", "osx-arm64");

        var outcome = Run(handler, host, new SelfUpdateOptions("0.2.0", ReleaseCandidate: true, NoMcp: false));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("--release-candidate", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public void Run_ChecksumMismatch_LeavesExistingBinary()
    {
        using var handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.2.0","draft":false}""");
        handler.MapFile(
            GitHubReleaseClient.AssetUri("v0.2.0", "SHA256SUMS.txt").AbsoluteUri,
            Encoding.UTF8.GetBytes(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  kyber-weave-osx-arm64.tar.gz\n"));
        var host = CreateHost("0.1.0", "osx-arm64");

        var outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("SHA256 mismatch", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("old-cli", File.ReadAllText(host.ProcessPath));
    }

    [Fact]
    public void Run_AlreadyCurrent_IsNoOp()
    {
        using var handler = new MapHandler();
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.1.1","draft":false}""");
        var host = CreateHost("0.1.1+abc", "osx-arm64");

        var outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("already on 0.1.1", outcome.Message);
        Assert.Equal("old-cli", File.ReadAllText(host.ProcessPath));
        Assert.DoesNotContain(
            handler.Uris,
            uri => uri.Contains("SHA256SUMS.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_NoMcp_DoesNotInstallMcp()
    {
        using var handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.2.0","draft":false}""");
        var host = CreateHost("0.1.0", "osx-arm64");

        var outcome = Run(handler, host, new SelfUpdateOptions(null, false, NoMcp: true));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("new-cli", File.ReadAllText(host.ProcessPath));
        Assert.False(File.Exists(Path.Combine(_install.Path, "kyber-weave-mcp")));
        Assert.DoesNotContain(
            handler.Uris,
            uri => uri.Contains("kyber-weave-mcp-", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WindowsZip_ReplacesExeNames()
    {
        using var handler = MapRelease("0.2.0", "win-x64", windows: true);
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.2.0","draft":false}""");
        var host = CreateHost("0.1.0", "win-x64", windows: true);

        var outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("new-cli", File.ReadAllText(host.ProcessPath));
        Assert.Equal(
            "new-mcp",
            File.ReadAllText(Path.Combine(_install.Path, "kyber-weave-mcp.exe")));
    }

    [Fact]
    public void Run_DotnetHost_IsRefused()
    {
        var host = new SelfUpdateHost(
            Path.Combine(_install.Path, "dotnet"),
            "0.1.0",
            "osx-arm64",
            IsWindows: false,
            IsMacOs: false);
        File.WriteAllText(host.ProcessPath, "shim");

        using var handler = new MapHandler();
        var outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("dotnet run", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_DotnetToolInstall_IsRefused()
    {
        var tools = Path.Combine(_install.Path, ".dotnet", "tools");
        Directory.CreateDirectory(tools);
        var processPath = Path.Combine(tools, "kyber-weave");
        File.WriteAllText(processPath, "shim");
        var host = new SelfUpdateHost(processPath, "0.1.0", "osx-arm64", false, false);

        using var handler = new MapHandler();
        var outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("dotnet tool", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ReadOnlyInstallDirectory_IsRefused()
    {
        if (OperatingSystem.IsWindows())
            return;

        var host = CreateHost("0.1.0", "osx-arm64");
        File.SetUnixFileMode(
            _install.Path,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var handler = new MapHandler();
            var outcome = Run(handler, host, new SelfUpdateOptions());
            Assert.Equal(1, outcome.ExitCode);
            Assert.Contains("write permission", outcome.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(
                _install.Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    [Fact]
    public void Run_GitHubToken_IsSentAsBearerAndNotMentionedOnFailure()
    {
        using var handler = new MapHandler();
        var host = CreateHost("0.1.0", "osx-arm64");
        const string token = "not-a-real-token";

        var outcome = Run(
            handler,
            host,
            new SelfUpdateOptions("0.9.9", false, true),
            env => env == "GITHUB_TOKEN" ? token : null);

        Assert.Equal(1, outcome.ExitCode);
        Assert.DoesNotContain(token, outcome.Message, StringComparison.Ordinal);
        Assert.Contains(handler.Authorizations, value => value == "Bearer " + token);
    }

    [Fact]
    public void Execute_WritesHumanOutput()
    {
        using var handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.2.0","draft":false}""");
        var host = CreateHost("0.1.0", "osx-arm64");
        var command = new UpdateCommand
        {
            Host = host,
            Handler = handler,
            ReadEnvironment = _ => null
        };

        var execution = ProcessConsoleCapture.Run(
            () => command.Execute(null!, new UpdateSettings()));

        Assert.Equal(0, execution.Result);
        Assert.Contains("updated kyber-weave 0.2.0", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void IsDotnetToolInstall_DetectsToolsDirectory()
    {
        Assert.True(SelfUpdater.IsDotnetToolInstall("/Users/x/.dotnet/tools/kyber-weave"));
        Assert.True(SelfUpdater.IsDotnetToolInstall(@"C:\Users\x\.dotnet\tools\kyber-weave.exe"));
        Assert.False(SelfUpdater.IsDotnetToolInstall("/Users/x/.local/bin/kyber-weave"));
    }

    private SelfUpdateOutcome Run(
        HttpMessageHandler handler,
        SelfUpdateHost host,
        SelfUpdateOptions options,
        Func<string, string?>? env = null)
    {
        using var updater = new SelfUpdater(handler, host, _ => { }, env ?? (_ => null));
        return updater.Run(options);
    }

    private SelfUpdateHost CreateHost(string currentVersion, string rid, bool windows = false)
    {
        var name = BinaryInstaller.InstalledFileName("kyber-weave", windows);
        var path = Path.Combine(_install.Path, name);
        File.WriteAllText(path, "old-cli");
        return new SelfUpdateHost(path, currentVersion, rid, windows, IsMacOs: false);
    }

    private MapHandler MapRelease(string version, string rid, bool windows)
    {
        var handler = new MapHandler();
        var tag = "v" + version;
        var cliName = BinaryInstaller.ArchiveName("kyber-weave", rid);
        var mcpName = BinaryInstaller.ArchiveName("kyber-weave-mcp", rid);
        var cliArchive = Path.Combine(_assets.Path, cliName);
        var mcpArchive = Path.Combine(_assets.Path, mcpName);
        var cliFile = BinaryInstaller.InstalledFileName("kyber-weave", windows);
        var mcpFile = BinaryInstaller.InstalledFileName("kyber-weave-mcp", windows);
        WriteArchive(cliArchive, cliFile, "new-cli"u8.ToArray(), windows);
        WriteArchive(mcpArchive, mcpFile, "new-mcp"u8.ToArray(), windows);
        var cliBytes = File.ReadAllBytes(cliArchive);
        var mcpBytes = File.ReadAllBytes(mcpArchive);
        var sums = $"{Sha(cliBytes)}  {cliName}\n{Sha(mcpBytes)}  {mcpName}\n";
        handler.MapFile(
            GitHubReleaseClient.AssetUri(tag, "SHA256SUMS.txt").AbsoluteUri,
            Encoding.UTF8.GetBytes(sums));
        handler.MapFile(GitHubReleaseClient.AssetUri(tag, cliName).AbsoluteUri, cliBytes);
        handler.MapFile(GitHubReleaseClient.AssetUri(tag, mcpName).AbsoluteUri, mcpBytes);
        return handler;
    }

    private static void WriteArchive(string archivePath, string entryName, byte[] content, bool windows)
    {
        if (windows)
        {
            using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
            using var stream = entry.Open();
            stream.Write(content);
            return;
        }

        using var file = File.Create(archivePath);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip);
        using var data = new MemoryStream(content);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = data
        });
    }

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _map = new(StringComparer.Ordinal);

        public List<string> Uris { get; } = [];

        public List<string?> Authorizations { get; } = [];

        public void MapJson(string uri, string json) =>
            Map(uri, () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        public void MapFile(string uri, byte[] bytes) =>
            Map(uri, () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
                return response;
            });

        public void Map(string uri, Func<HttpResponseMessage> factory) => _map[uri] = factory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            Uris.Add(uri);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            if (_map.TryGetValue(uri, out var factory))
                return Task.FromResult(factory());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent("not found")
            });
        }
    }
}
