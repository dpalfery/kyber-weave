using System.Net;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Update;

/// <summary>
/// The self-update's own decisions over its seams: it stages each archive in a temporary directory, backs up every
/// binary it replaces, commits the running image last, and rolls the earlier ones back when a later commit fails.
/// </summary>
/// <remarks>
/// Every resource is a fake: the file system, the path resolver, the environment, the release transport and the
/// update system. Nothing here downloads, extracts, starts a program or reads the disk.
/// </remarks>
public sealed class SelfUpdaterTests
{
    private const string InstallDirectory = "/kw/install";
    private const string ProcessPath = InstallDirectory + "/kyber-weave";
    private const string McpPath = InstallDirectory + "/kyber-weave-mcp";
    private const string Tag = "v0.2.0";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(
        currentDirectory: "/kw/work",
        homeDirectory: "/kw/home",
        readVariable: _ => null);
    private readonly FakeSelfUpdateSystem _system;
    private readonly List<string> _log = [];

    public SelfUpdaterTests()
    {
        _paths = new FakePathResolver(_files);
        _system = new FakeSelfUpdateSystem(_files);
        _system.Extract = (_, destination) =>
        {
            string baseName = Path.GetFileName(destination)[..^"-extract".Length];
            _files.CreateDirectory(destination);
            _files.WriteAllText(Path.Combine(destination, baseName), "new-" + baseName);
        };
        _files.CreateDirectory(InstallDirectory);
    }

    private SelfUpdateHost Host => new(ProcessPath, "0.1.0", "osx-arm64", IsWindows: false, IsMacOs: false);

    private SelfUpdater CreateUpdater(HttpMessageHandler handler, SelfUpdateHost? host = null) =>
        new(handler, host ?? Host, _files, _paths, _environment, _system, _log.Add);

    /// <summary>The archives are downloaded, checked and staged in a temporary directory the file-system seam made.</summary>
    [Fact]
    public void AReleaseUpdateStagesEachArchiveInATemporaryDirectoryAndDeletesIt()
    {
        _files.WriteAllText(ProcessPath, "old-cli");
        using MapHandler handler = ServeRelease(CliBaseName, McpBaseName);

        using SelfUpdater updater = CreateUpdater(handler);
        SelfUpdateOutcome outcome = updater.Run(
            new SelfUpdateOptions("0.2.0", ReleaseCandidate: false, NoMcp: false, NoKyberDash: true, NoArbiter: true));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(2, _system.ExtractedArchives.Count);
        Assert.All(_system.ExtractedArchives, archive => Assert.StartsWith("/kw-memfs-tmp/", archive, StringComparison.Ordinal));
        Assert.Empty(_files.EnumerateDirectories("/kw-memfs-tmp"));
        Assert.Equal(2, _files.EnumerateFiles(InstallDirectory, "*", recursive: false).Count());
    }

    /// <summary>
    /// The running image is replaced after every other binary, so a failure that can still be rolled back happens
    /// before the process loses the ability to load what it has not read yet.
    /// </summary>
    [Fact]
    public void TheRunningImageIsCommittedLast()
    {
        _files.WriteAllText(ProcessPath, "old-cli");
        using MapHandler handler = ServeRelease(CliBaseName, McpBaseName);

        using SelfUpdater updater = CreateUpdater(handler);
        _ = updater.Run(
            new SelfUpdateOptions("0.2.0", ReleaseCandidate: false, NoMcp: false, NoKyberDash: true, NoArbiter: true));

        Assert.Equal<string>([McpPath, ProcessPath], _system.ReplacedDestinations);
        Assert.Equal("new-kyber-weave-mcp", _files.ReadAllText(McpPath));
        Assert.Equal("new-kyber-weave", _files.ReadAllText(ProcessPath));
    }

    /// <summary>
    /// A commit failure after the running image is not possible by ordering, but any earlier binary must be put back
    /// before the failure escapes: an update never leaves half of its binaries new.
    /// </summary>
    [Fact]
    public void AFailedCommitRollsBackTheBinariesAlreadyReplaced()
    {
        _files.WriteAllText(ProcessPath, "old-cli");
        _files.WriteAllText(McpPath, "old-mcp");
        _system.ReplaceFailure = destination =>
            destination == McpPath ? new IOException("the MCP binary is locked") : null;
        using MapHandler handler = ServeRelease(CliBaseName, McpBaseName);

        using SelfUpdater updater = CreateUpdater(handler);
        SelfUpdateOutcome outcome = updater.Run(
            new SelfUpdateOptions("0.2.0", ReleaseCandidate: false, NoMcp: false, NoKyberDash: true, NoArbiter: true));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("the MCP binary is locked", outcome.Message);
        Assert.Equal("old-cli", _files.ReadAllText(ProcessPath));
        Assert.Equal("old-mcp", _files.ReadAllText(McpPath));
        Assert.NotEmpty(_system.Copies);
    }

    /// <summary>
    /// The tray record and the installed kyberdash are probed through the seams, so the skip is decided from data the
    /// test owns rather than from the developer's own home directory.
    /// </summary>
    [Fact]
    public void AnAlreadyCurrentHostReportsWhereTheTrayRecordWouldBe()
    {
        _files.WriteAllText(ProcessPath, "old-cli");

        using MapHandler handler = new();
        SelfUpdateHost current = new(ProcessPath, "0.1.7-rc.20", "osx-arm64", IsWindows: false, IsMacOs: false);

        using SelfUpdater updater = CreateUpdater(handler, current);
        SelfUpdateOutcome outcome = updater.Run(
            new SelfUpdateOptions("0.1.7-rc.20", ReleaseCandidate: false, NoMcp: false, NoArbiter: true));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("already on 0.1.7-rc.20", outcome.Message);
        Assert.Contains(_log, line => line.Contains("/kw/home/.kyberdash/tray.json", StringComparison.Ordinal));
    }

    /// <summary>The install directory is checked through the file-system and update-system seams.</summary>
    [Fact]
    public void AnAbsentInstallDirectoryIsRefused()
    {
        _files.DeleteTreeNoFollow(InstallDirectory);

        using MapHandler handler = new();

        using SelfUpdater updater = CreateUpdater(handler);
        SelfUpdateOutcome outcome = updater.Run(
            new SelfUpdateOptions("0.2.0", ReleaseCandidate: false, NoMcp: false));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains($"no write permission for {InstallDirectory}", outcome.Message, StringComparison.Ordinal);
    }

    private const string CliBaseName = "kyber-weave";
    private const string McpBaseName = "kyber-weave-mcp";

    /// <summary>Serves the checksum listing and one archive per binary the update asks for.</summary>
    private MapHandler ServeRelease(string cliBaseName, string mcpBaseName)
    {
        MapHandler handler = new();
        List<string> sums = [];
        foreach (string baseName in new[] { cliBaseName, mcpBaseName })
            sums.Add(ServeArchive(handler, baseName));

        handler.MapText(
            GitHubReleaseClient.AssetUri(Tag, "SHA256SUMS.txt").AbsoluteUri,
            string.Join('\n', sums));
        return handler;
    }

    private string ServeArchive(MapHandler handler, string baseName)
    {
        string archiveName = BinaryInstaller.ArchiveName(baseName, "osx-arm64");
        byte[] bytes = Encoding.UTF8.GetBytes("archive of " + archiveName);
        handler.MapFile(GitHubReleaseClient.AssetUri(Tag, archiveName).AbsoluteUri, bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes)) + "  " + archiveName;
    }

    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _map = new(StringComparer.Ordinal);

        public void MapFile(string uri, byte[] bytes) => _map[uri] = bytes;

        public void MapText(string uri, string text) => _map[uri] = Encoding.UTF8.GetBytes(text);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (_map.TryGetValue(uri, out byte[]? bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent("not found", Encoding.UTF8, "text/plain")
            });
        }
    }
}
