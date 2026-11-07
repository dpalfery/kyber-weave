using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T6 contract: <c>kyber-weave update</c> refreshes arbiter hooks through an injected
/// seam for registered projects with <c>arbiter.enabled</c>, and skips the seam when
/// no arbiter is installed. The seam is constructor/factory injection, never a shell-out.
/// </summary>
public sealed class SelfUpdaterArbiterRefreshTests : IDisposable
{
    private readonly TempDirectory _install = new();
    private readonly TempDirectory _assets = new();

    private static readonly ProcessResult QuietSuccess = new(0, string.Empty, string.Empty);

    public void Dispose()
    {
        _install.Dispose();
        _assets.Dispose();
    }

    /// <summary>
    /// When an arbiter binary is installed, the update invokes the injected refresh seam
    /// once for the enabled projects instead of shelling out to a hook script.
    /// </summary>
    [Fact]
    public void Update_InvokesArbiterRefreshSeamForEnabledProjectsWhenArbiterInstalled()
    {
        InstallArbiter();
        using MapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        RecordingArbiterHookRefresher refresher = new();
        List<string> log = [];

        using SelfUpdater updater = new(
            handler,
            host,
            log.Add,
            _ => null,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher);
        SelfUpdateOutcome outcome = updater.Run(new SelfUpdateOptions("0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(1, refresher.Calls);
    }

    /// <summary>
    /// When no arbiter binary is installed, the refresh seam is never invoked.
    /// An update must not add a surface the owner did not choose.
    /// </summary>
    [Fact]
    public void Update_SkipsArbiterRefreshSeamWhenNoArbiterInstalled()
    {
        using MapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        RecordingArbiterHookRefresher refresher = new();
        List<string> log = [];

        using SelfUpdater updater = new(
            handler,
            host,
            log.Add,
            _ => null,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher);
        SelfUpdateOutcome outcome = updater.Run(new SelfUpdateOptions("0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(0, refresher.Calls);
    }

    /// <summary>
    /// The refresh travels through the injected seam, never through a child process:
    /// no process runner sees an arbiter hook argv.
    /// </summary>
    [Fact]
    public void Update_ArbiterRefreshSeamIsInjectedNeverShelledOut()
    {
        InstallArbiter();
        using MapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        RecordingArbiterHookRefresher refresher = new();
        List<string> log = [];
        List<string> spawned = [];

        using SelfUpdater updater = new(
            handler,
            host,
            log.Add,
            _ => null,
            (file, args) =>
            {
                spawned.Add(file + " " + string.Join(" ", args));
                return 0;
            },
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher);
        SelfUpdateOutcome outcome = updater.Run(new SelfUpdateOptions("0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(1, refresher.Calls);
        Assert.DoesNotContain(spawned, line => line.Contains("arbiter", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The success line reports what the refresher actually did, not how many projects it was
    /// handed: a project whose hook update fails must not be counted as refreshed.
    /// </summary>
    [Fact]
    public void Update_ReportsTheRefreshOutcomeNotTheInputCount()
    {
        InstallArbiter();
        using MapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");

        FakeSquadUserPaths userPaths = new(Path.Combine(_install.Path, "user-home"));
        Directory.CreateDirectory(userPaths.HomeDirectory);
        SquadStateStore store = new(userPaths);

        string project = Path.Combine(_install.Path, "registered-project");
        string configDirectory = Path.Combine(project, ".kyber-weave");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, "kyber-weave.yml"),
            "arbiter:\n  enabled: true\n");

        string receiptPath = store.ResolveReceiptPath(project, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(
            receiptPath,
            store.SerializeReceipt(new SquadReceipt(
                "kyber-squad.receipt/v1",
                SquadDeploymentScope.Project,
                project,
                DateTimeOffset.UtcNow,
                [],
                [])));

        RecordingArbiterHookRefresher refresher = new()
        {
            Result = new ArbiterHookRefreshResult(Refreshed: 0, Failed: 1),
        };
        List<string> log = [];
        Func<string, string?> environment = name => name switch
        {
            "HOME" => userPaths.HomeDirectory,
            "APPDATA" => userPaths.ApplicationDataDirectory,
            _ => null,
        };

        using SelfUpdater updater = new(
            handler,
            host,
            log.Add,
            environment,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher);
        SelfUpdateOutcome outcome = updater.Run(new SelfUpdateOptions("0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains(
            log,
            line => line.Contains("0 project(s)", StringComparison.Ordinal)
                    && line.Contains("1 failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A user-level global target list is part of the update contract: a previously installed
    /// global hook is refreshed so it points at the current Arbiter binary instead of a stale one.
    /// </summary>
    [Fact]
    public void Refresh_RefreshesExistingGlobalArbiterHookListedInUserConfig()
    {
        string home = Path.Combine(_install.Path, "home");
        Directory.CreateDirectory(home);
        UserHostConfig.WriteGlobalTargets(home, ["cursor"]);
        string hookPath = WriteLegacyGlobalHook(home, SquadTarget.Cursor, "/legacy/kyber-weave-arbiter hook");

        List<string> log = [];
        Func<string, string?> environment = name => name switch
        {
            "HOME" => home,
            "APPDATA" => Path.Combine(home, ".config"),
            _ => null,
        };

        ArbiterHookRefresher refresher = new(log.Add, environment);
        ArbiterHookRefreshResult outcome = refresher.Refresh([]);

        Assert.Equal(1, outcome.Refreshed);
        string content = File.ReadAllText(hookPath);
        Assert.Contains("kyber-weave-arbiter hook --harness cursor --scope global", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/legacy/kyber-weave-arbiter hook", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Update-time refresh follows the user config gate: an already-installed global hook is refreshed
    /// only for harnesses named in <c>arbiter.global.targets</c>, and it never rewrites foreign bytes.
    /// </summary>
    [Fact]
    public void Update_RefreshesOnlyListedGlobalArbiterHooksWithoutTouchingForeignBytes()
    {
        string home = Path.Combine(_install.Path, "home");
        Directory.CreateDirectory(home);
        UserHostConfig.WriteGlobalTargets(home, ["cursor"]);
        string hookPath = WriteLegacyGlobalHook(home, SquadTarget.Cursor, "/legacy/kyber-weave-arbiter hook");

        string foreign = Path.Combine(new SquadGlobalRoots(_ => null, home).ResolveGlobalRoot(SquadTarget.Cursor), "foreign.json");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "{\"user\":\"keep-me\"}");

        using MapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        List<string> log = [];
        Func<string, string?> environment = name => name switch
        {
            "HOME" => home,
            "APPDATA" => Path.Combine(home, ".config"),
            _ => null,
        };

        using SelfUpdater updater = new(
            handler,
            host,
            log.Add,
            environment,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: new ArbiterHookRefresher(log.Add, environment));
        SelfUpdateOutcome outcome = updater.Run(new SelfUpdateOptions("0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        string content = File.ReadAllText(hookPath);
        Assert.Contains("kyber-weave-arbiter hook --harness cursor --scope global", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/legacy/kyber-weave-arbiter hook", content, StringComparison.Ordinal);
        Assert.Equal("{\"user\":\"keep-me\"}", File.ReadAllText(foreign), StringComparer.Ordinal);
    }

    private static string WriteLegacyGlobalHook(string home, SquadTarget target, string legacyCommand)
    {
        SquadGlobalRoots globalRoots = new(_ => null, home);
        string hookPath = Path.Combine(globalRoots.ResolveGlobalRoot(target), "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);

        string token = SquadTargetCatalog.GetToken(target);
        string content = $$"""
            {
              "preToolUse": [
                {
                  "command": "{{legacyCommand}} --harness {{token}} --scope global",
                  "matcher": "Task",
                  "timeout": 5,
                  "failClosed": true
                }
              ],
              "postToolUse": []
            }
            """;

        File.WriteAllText(hookPath, content);
        return hookPath;
    }

    private SelfUpdateHost CreateHost(string currentVersion, string rid, bool windows = false)
    {
        string name = BinaryInstaller.InstalledFileName("kyber-weave", windows);
        string path = Path.Combine(_install.Path, name);
        File.WriteAllText(path, "old-cli");
        return new SelfUpdateHost(path, currentVersion, rid, windows, false);
    }

    private string InstallArbiter(bool windows = false)
    {
        string path = Path.Combine(
            _install.Path,
            BinaryInstaller.InstalledFileName("kyber-weave-arbiter", windows));
        File.WriteAllText(path, "old-arbiter");
        return path;
    }

    private MapHandler MapRelease(
        string version,
        string rid,
        bool windows,
        bool withKyberDash = false,
        bool withArbiter = false)
    {
        MapHandler handler = new();
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

        if (withKyberDash)
        {
            string dashName = BinaryInstaller.ArchiveName(
                "kyberdash",
                PlatformRid.KyberDashRid(rid));
            string dashArchive = Path.Combine(_assets.Path, dashName);
            string dashFile = BinaryInstaller.InstalledFileName("kyberdash", windows);
            WriteArchive(dashArchive, dashFile, "new-kyberdash"u8.ToArray(), windows);
            byte[] dashBytes = File.ReadAllBytes(dashArchive);
            sums += $"{Sha(dashBytes)}  {dashName}\n";
            handler.MapFile(GitHubReleaseClient.AssetUri(tag, dashName).AbsoluteUri, dashBytes);
        }

        if (withArbiter)
        {
            // Ships like kyber-weave-mcp: published under this host's .NET RID.
            string arbiterName = BinaryInstaller.ArchiveName("kyber-weave-arbiter", rid);
            string arbiterArchive = Path.Combine(_assets.Path, arbiterName);
            string arbiterFile = BinaryInstaller.InstalledFileName("kyber-weave-arbiter", windows);
            WriteArchive(arbiterArchive, arbiterFile, "new-arbiter"u8.ToArray(), windows);
            byte[] arbiterBytes = File.ReadAllBytes(arbiterArchive);
            sums += $"{Sha(arbiterBytes)}  {arbiterName}\n";
            handler.MapFile(GitHubReleaseClient.AssetUri(tag, arbiterName).AbsoluteUri, arbiterBytes);
        }

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
        using GZipStream gzip = new(file, CompressionLevel.Fastest);
        using TarWriter writer = new(gzip);
        using MemoryStream data = new(content);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = data
        });
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification =
            "SHA256SUMS.txt and the digests it is compared against are lowercase hex by sha256sum convention; ToUpperInvariant would not match the published format.")]
    private static string Sha(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class RecordingArbiterHookRefresher : IArbiterHookRefresher
    {
        public int Calls { get; private set; }

        public List<string> RefreshedRoots { get; } = [];

        /// <summary>When unset, every input root is reported as refreshed.</summary>
        public ArbiterHookRefreshResult? Result { get; set; }

        public ArbiterHookRefreshResult Refresh(IReadOnlyList<string> projectRoots)
        {
            ArgumentNullException.ThrowIfNull(projectRoots);
            Calls++;
            RefreshedRoots.AddRange(projectRoots);
            return Result ?? new ArbiterHookRefreshResult(projectRoots.Count, Failed: 0);
        }
    }

    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _map = new(StringComparer.Ordinal);

        public void MapFile(string uri, byte[] bytes)
        {
            Map(uri, () =>
            {
                HttpResponseMessage response = new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
                return response;
            });
        }

        private void Map(string uri, Func<HttpResponseMessage> factory)
        {
            _map[uri] = factory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (_map.TryGetValue(uri, out Func<HttpResponseMessage>? factory))
            {
                return Task.FromResult(factory());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent("not found", Encoding.UTF8, "text/plain"),
            });
        }
    }
}
