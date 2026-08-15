using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Update;
using KyberWeave.Cli.Update;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

public sealed partial class UpdateCommandTests : IDisposable
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
    public void PlatformRidDetectMapsPublishedCombinations(
        bool windows, bool linux, bool macos, Architecture architecture, string expected)
    {
        Assert.Equal(expected, PlatformRid.Detect(windows, linux, macos, architecture));
    }

    [Fact]
    public void PlatformRidDetectRejectsWinArm64()
    {
        SelfUpdateException ex = Assert.Throws<SelfUpdateException>(
            () => PlatformRid.Detect(windows: true, linux: false, macos: false, Architecture.Arm64));
        Assert.Contains("win-arm64", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2.0-rc.1", "0.2.0-rc.1")]
    [InlineData("0.1.0+714f187ab97d66e1199c33d5aaa0c9ab76ffae0f", "0.1.0")]
    [InlineData("V0.2.0-dev.1", "0.2.0-dev.1")]
    public void ReleaseVersionNormalizeStripsPrefixAndMetadata(string input, string expected)
    {
        Assert.Equal(expected, ReleaseVersion.Normalize(input));
    }

    [Fact]
    public void ReleaseVersionNormalizeRejectsPathFragments()
    {
        Assert.Throws<SelfUpdateException>(() => ReleaseVersion.Normalize("../0.2.0"));
        Assert.Throws<SelfUpdateException>(() => ReleaseVersion.Normalize("0.2.0/evil"));
    }

    [Fact]
    public void ChecksumVerifierExpectedHexMatchesExactFileNameOnly()
    {
        string sums = """
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
    public void ChecksumVerifierExpectedHexAcceptsStarAndPathPrefix()
    {
        string sums = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc *dist/kyber-weave-osx-arm64.tar.gz";
        Assert.Equal(
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            ChecksumVerifier.ExpectedHex(sums, "kyber-weave-osx-arm64.tar.gz"));
    }

    [Fact]
    public void ChecksumVerifierExpectedHexMissingEntryThrows()
    {
        SelfUpdateException ex = Assert.Throws<SelfUpdateException>(
            () => ChecksumVerifier.ExpectedHex("deadbeef  other.tar.gz\n", "kyber-weave-osx-arm64.tar.gz"));
        Assert.Contains("no entry", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksumVerifierVerifyMismatchThrows()
    {
        byte[] actual = SHA256.HashData("nope"u8.ToArray());
        SelfUpdateException ex = Assert.Throws<SelfUpdateException>(
            () => ChecksumVerifier.Verify(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                actual,
                "kyber-weave-osx-arm64.tar.gz"));
        Assert.Contains("SHA256 mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureHttpsRejectsHttp()
    {
        SelfUpdateException ex = Assert.Throws<SelfUpdateException>(
            () => GitHubReleaseClient.EnsureHttps(new Uri("http://example.com/file")));
        Assert.Contains("non-HTTPS", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunLatestStableUsesReleasesLatestAndReplacesBothBinaries()
    {
        using MapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.2.0","draft":false}""");
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");

        SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions());

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
    public void RunReleaseCandidateSkipsDraftsAndUsesReleasesList()
    {
        using MapHandler handler = MapRelease("0.2.0-rc.1", "osx-arm64", windows: false);
        handler.MapJson(
            GitHubReleaseClient.ReleasesApi.AbsoluteUri,
            """
            [
              {"tag_name":"v0.9.0-rc.9","draft":true,"prerelease":true},
              {"tag_name":"v0.2.0-rc.1","draft":false,"prerelease":true},
              {"tag_name":"v0.1.1","draft":false,"prerelease":false}
            ]
            """);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");

        SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions(null, ReleaseCandidate: true, NoMcp: false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("0.2.0-rc.1", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(GitHubReleaseClient.ReleasesApi.AbsoluteUri, handler.Uris, StringComparer.Ordinal);
        Assert.DoesNotContain(GitHubReleaseClient.LatestApi.AbsoluteUri, handler.Uris, StringComparer.Ordinal);
    }

    [Fact]
    public void RunPinnedVersionSkipsApiAndStripsVPrefix()
    {
        using MapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");

        SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions("v0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("new-cli", File.ReadAllText(host.ProcessPath));
        Assert.DoesNotContain(GitHubReleaseClient.LatestApi.AbsoluteUri, handler.Uris, StringComparer.Ordinal);
        Assert.DoesNotContain(GitHubReleaseClient.ReleasesApi.AbsoluteUri, handler.Uris, StringComparer.Ordinal);
    }

    [Fact]
    public void RunReleaseCandidateWithVersionFailsWithoutHttp()
    {
        using MapHandler handler = new MapHandler();
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");

        SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions("0.2.0", ReleaseCandidate: true, NoMcp: false));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("--release-candidate", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public void RunChecksumMismatchLeavesExistingBinary()
    {
        using MapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.2.0","draft":false}""");
        handler.MapFile(
            GitHubReleaseClient.AssetUri("v0.2.0", "SHA256SUMS.txt").AbsoluteUri,
            Encoding.UTF8.GetBytes(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  kyber-weave-osx-arm64.tar.gz\n"));
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");

        SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("SHA256 mismatch", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("old-cli", File.ReadAllText(host.ProcessPath));
    }

    [Fact]
    public void RunAlreadyCurrentIsNoOp()
    {
        using MapHandler handler = new MapHandler();
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.1.1","draft":false}""");
        SelfUpdateHost host = CreateHost("0.1.1+abc", "osx-arm64");

        SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("already on 0.1.1", outcome.Message);
        Assert.Equal("old-cli", File.ReadAllText(host.ProcessPath));
        Assert.DoesNotContain(
            handler.Uris,
            uri => uri.Contains("SHA256SUMS.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void RunNoMcpDoesNotInstallMcp()
    {
        using MapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.2.0","draft":false}""");
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");

        SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions(null, false, NoMcp: true));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("new-cli", File.ReadAllText(host.ProcessPath));
        Assert.False(File.Exists(Path.Combine(_install.Path, "kyber-weave-mcp")));
        Assert.DoesNotContain(
            handler.Uris,
            uri => uri.Contains("kyber-weave-mcp-", StringComparison.Ordinal));
    }

    [Fact]
    public void RunWindowsZipReplacesExeNames()
    {
        using MapHandler handler = MapRelease("0.2.0", "win-x64", windows: true);
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.2.0","draft":false}""");
        SelfUpdateHost host = CreateHost("0.1.0", "win-x64", windows: true);

        SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("new-cli", File.ReadAllText(host.ProcessPath));
        Assert.Equal(
            "new-mcp",
            File.ReadAllText(Path.Combine(_install.Path, "kyber-weave-mcp.exe")));
    }

    [Fact]
    public void RunDotnetHostIsRefused()
    {
        SelfUpdateHost host = new SelfUpdateHost(
            Path.Combine(_install.Path, "dotnet"),
            "0.1.0",
            "osx-arm64",
            IsWindows: false,
            IsMacOs: false);
        File.WriteAllText(host.ProcessPath, "shim");

        using MapHandler handler = new MapHandler();
        SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("dotnet run", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunDotnetToolInstallIsRefused()
    {
        string tools = Path.Combine(_install.Path, ".dotnet", "tools");
        Directory.CreateDirectory(tools);
        string processPath = Path.Combine(tools, "kyber-weave");
        File.WriteAllText(processPath, "shim");
        SelfUpdateHost host = new SelfUpdateHost(processPath, "0.1.0", "osx-arm64", false, false);

        using MapHandler handler = new MapHandler();
        SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions());

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("dotnet tool", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunReadOnlyInstallDirectoryIsRefused()
    {
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Read-only POSIX file mode permissions are not supported on Windows.");

        if (GetEffectiveUserId() == 0)
            throw SkipException.ForSkip("Executing as root bypasses read-only POSIX directory permissions.");

        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        File.SetUnixFileMode(
            _install.Path,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using MapHandler handler = new MapHandler();
            SelfUpdateOutcome outcome = Run(handler, host, new SelfUpdateOptions());
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
    public void RunGitHubTokenIsSentAsBearerAndNotMentionedOnFailure()
    {
        using MapHandler handler = new MapHandler();
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        const string token = "not-a-real-token";

        SelfUpdateOutcome outcome = Run(
            handler,
            host,
            new SelfUpdateOptions("0.9.9", false, true),
            env => env == "GITHUB_TOKEN" ? token : null);

        Assert.Equal(1, outcome.ExitCode);
        Assert.DoesNotContain(token, outcome.Message, StringComparison.Ordinal);
        Assert.Contains(handler.Authorizations, value => value == "Bearer " + token);
    }

    [Fact]
    public void ExecuteWritesHumanOutput()
    {
        using MapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        handler.MapJson(
            GitHubReleaseClient.LatestApi.AbsoluteUri,
            """{"tag_name":"v0.2.0","draft":false}""");
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        UpdateCommand command = new UpdateCommand
        {
            Host = host,
            Handler = handler,
            ReadEnvironment = _ => null
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => command.Execute(null!, new UpdateSettings()));

        Assert.Equal(0, execution.Result);
        Assert.Contains("updated kyber-weave 0.2.0", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void IsDotnetToolInstallDetectsToolsDirectory()
    {
        Assert.True(SelfUpdater.IsDotnetToolInstall("/Users/x/.dotnet/tools/kyber-weave"));
        Assert.True(SelfUpdater.IsDotnetToolInstall(@"C:\Users\x\.dotnet\tools\kyber-weave.exe"));
        Assert.False(SelfUpdater.IsDotnetToolInstall("/Users/x/.local/bin/kyber-weave"));
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial uint GetEffectiveUserId();

    private SelfUpdateOutcome Run(
        HttpMessageHandler handler,
        SelfUpdateHost host,
        SelfUpdateOptions options,
        Func<string, string?>? env = null)
    {
        using SelfUpdater updater = new SelfUpdater(handler, host, _ => { }, env ?? (_ => null));
        return updater.Run(options);
    }

    private SelfUpdateHost CreateHost(string currentVersion, string rid, bool windows = false)
    {
        string name = BinaryInstaller.InstalledFileName("kyber-weave", windows);
        string path = Path.Combine(_install.Path, name);
        File.WriteAllText(path, "old-cli");
        return new SelfUpdateHost(path, currentVersion, rid, windows, IsMacOs: false);
    }

    private MapHandler MapRelease(string version, string rid, bool windows)
    {
        MapHandler handler = new MapHandler();
        string tag = "v" + version;
        string cliName = BinaryInstaller.ArchiveName("kyber-weave", rid);
        string mcpName = BinaryInstaller.ArchiveName("kyber-weave-mcp", rid);
        string cliArchive = Path.Combine(_assets.Path, cliName);
        string mcpArchive = Path.Combine(_assets.Path, mcpName);
        string cliFile = BinaryInstaller.InstalledFileName("kyber-weave", windows);
        string mcpFile = BinaryInstaller.InstalledFileName("kyber-weave-mcp", windows);
        WriteArchive(cliArchive, cliFile, "new-cli"u8.ToArray(), windows);
        WriteArchive(mcpArchive, mcpFile, "new-mcp"u8.ToArray(), windows);
        byte[] cliBytes = File.ReadAllBytes(cliArchive);
        byte[] mcpBytes = File.ReadAllBytes(mcpArchive);
        string sums = $"{Sha(cliBytes)}  {cliName}\n{Sha(mcpBytes)}  {mcpName}\n";
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
            using ZipArchive zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
            using Stream stream = entry.Open();
            stream.Write(content);
            return;
        }

        using FileStream file = File.Create(archivePath);
        using GZipStream gzip = new GZipStream(file, CompressionLevel.Fastest);
        using TarWriter writer = new TarWriter(gzip);
        using MemoryStream data = new MemoryStream(content);
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
                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
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
            string uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            Uris.Add(uri);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            if (_map.TryGetValue(uri, out Func<HttpResponseMessage>? factory))
                return Task.FromResult(factory());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent("not found")
            });
        }
    }
}
