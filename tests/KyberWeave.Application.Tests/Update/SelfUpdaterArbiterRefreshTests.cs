using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Configuration;
using KyberWeave.Testing.Update;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Application.Update;
using KyberWeave.Domain.Update;

namespace KyberWeave.Application.Tests.Update;

/// <summary>
/// T6 contract: <c>kyber-weave update</c> refreshes arbiter hooks through an injected
/// seam for registered projects with <c>arbiter.enabled</c>, and skips the seam when
/// no arbiter is installed. The seam is constructor/factory injection, never a shell-out.
/// </summary>
public sealed class SelfUpdaterArbiterRefreshTests : IDisposable
{
    /// <summary>The bare global hook path the block formats write beneath their own root.</summary>
    private const string SharedHooksPath = "hooks.json";

    private static readonly DateTimeOffset GlobalInstalledAt =
        new(2026, 11, 06, 9, 30, 00, TimeSpan.Zero);

    private static readonly ProcessResult QuietSuccess = UpdateWorld.QuietSuccess;

    private readonly UpdateWorld _world = new();

    public void Dispose() => _world.Dispose();

    /// <summary>
    /// When an arbiter binary is installed, the update invokes the injected refresh seam
    /// once for the enabled projects instead of shelling out to a hook script.
    /// </summary>
    [Fact]
    public async Task Update_InvokesArbiterRefreshSeamForEnabledProjectsWhenArbiterInstalled()
    {
        InstallArbiter();
        using ReleaseMapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        RecordingArbiterHookRefresher refresher = new();
        List<string> log = [];

        LoopbackFinishingProcess loopback = _world.Loopback(
            handler,
            host,
            log.Add,
            _ => null,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: UpdateTestServices.NoProjects);
        using SelfUpdater updater = _world.CreateUpdater(
            handler,
            host,
            _ => null,
            log.Add,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: UpdateTestServices.NoProjects,
            finishingProcess: loopback.Invoke);
        SelfUpdateOutcome outcome = await updater.RunAsync(new SelfUpdateOptions("0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(1, refresher.Calls);
    }

    /// <summary>
    /// When no arbiter binary is installed, the refresh seam is never invoked.
    /// An update must not add a surface the owner did not choose.
    /// </summary>
    [Fact]
    public async Task Update_SkipsArbiterRefreshSeamWhenNoArbiterInstalled()
    {
        using ReleaseMapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        RecordingArbiterHookRefresher refresher = new();
        List<string> log = [];

        LoopbackFinishingProcess loopback = _world.Loopback(
            handler,
            host,
            log.Add,
            _ => null,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: UpdateTestServices.NoProjects);
        using SelfUpdater updater = _world.CreateUpdater(
            handler,
            host,
            _ => null,
            log.Add,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: UpdateTestServices.NoProjects,
            finishingProcess: loopback.Invoke);
        SelfUpdateOutcome outcome = await updater.RunAsync(new SelfUpdateOptions("0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(0, refresher.Calls);
    }

    /// <summary>
    /// The refresh never runs a hook script or the arbiter binary, and it runs inside the new kyber-weave:
    /// the injected seam is the only path to it, so no process runner sees an arbiter hook argv.
    /// </summary>
    [Fact]
    public async Task Update_ArbiterRefreshSeamIsInjectedNeverShelledOut()
    {
        InstallArbiter();
        using ReleaseMapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        RecordingArbiterHookRefresher refresher = new();
        List<string> log = [];
        List<string> spawned = [];

        Func<string, IReadOnlyList<string>, int> runProcess = (file, args) =>
        {
            spawned.Add(file + " " + string.Join(" ", args));
            return 0;
        };
        LoopbackFinishingProcess loopback = _world.Loopback(
            handler,
            host,
            log.Add,
            _ => null,
            runProcess,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: UpdateTestServices.NoProjects);
        using SelfUpdater updater = _world.CreateUpdater(
            handler,
            host,
            _ => null,
            log.Add,
            runProcess: runProcess,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: UpdateTestServices.NoProjects,
            finishingProcess: loopback.Invoke);
        SelfUpdateOutcome outcome = await updater.RunAsync(new SelfUpdateOptions("0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(1, refresher.Calls);
        Assert.DoesNotContain(spawned, line => line.Contains("arbiter", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The success line reports what the refresher actually did, not how many projects it was
    /// handed: a project whose hook update fails must not be counted as refreshed.
    /// </summary>
    [Fact]
    public async Task Update_ReportsTheRefreshOutcomeNotTheInputCount()
    {
        InstallArbiter();
        using ReleaseMapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");

        FakeSquadUserPaths userPaths = new(Path.Combine(_world.Install, "user-home"));
        _world.Files.CreateDirectory(userPaths.HomeDirectory);
        SquadStateStore store = _world.SquadStore(userPaths);

        string project = Path.Combine(_world.Install, "registered-project");
        string configDirectory = Path.Combine(project, ".kyber-weave");
        _world.Files.CreateDirectory(configDirectory);
        _world.Files.WriteAllText(
            Path.Combine(configDirectory, "kyber-weave.yml"),
            "arbiter:\n  enabled: true\n");

        string receiptPath = store.ResolveReceiptPath(project, SquadDeploymentScope.Project);
        _world.Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        _world.Files.WriteAllText(
            receiptPath,
            store.SerializeReceipt(new SquadReceipt(
                "kyber-squad.receipt/v1",
                SquadDeploymentScope.Project,
                project,
                GlobalInstalledAt,
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

        LoopbackFinishingProcess loopback = _world.Loopback(
            handler,
            host,
            log.Add,
            environment,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: _world.Targets(environment));
        using SelfUpdater updater = _world.CreateUpdater(
            handler,
            host,
            environment,
            log.Add,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: _world.Targets(environment),
            finishingProcess: loopback.Invoke);
        SelfUpdateOutcome outcome = await updater.RunAsync(new SelfUpdateOptions("0.2.0", false, false));

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
    public async Task Refresh_RefreshesExistingGlobalArbiterHookListedInUserConfig()
    {
        string home = Path.Combine(_world.Install, "home");
        _world.Files.CreateDirectory(home);
        new FileUserHostConfigStore(_world.Files).WriteGlobalTargets(home, ["cursor"]);
        string hookPath = WriteLegacyGlobalHook(home, SquadTarget.Cursor, "/legacy/kyber-weave-arbiter hook");

        List<string> log = [];
        Func<string, string?> environment = name => name switch
        {
            "HOME" => home,
            "APPDATA" => Path.Combine(home, ".config"),
            _ => null,
        };

        ArbiterHookRefresher refresher = _world.Refresher(environment, log.Add);
        ArbiterHookRefreshResult outcome = await refresher.RefreshAsync([]);

        Assert.Equal(1, outcome.Refreshed);
        string content = _world.Files.ReadAllText(hookPath);
        Assert.Contains("kyber-weave-arbiter hook --harness cursor --scope global", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/legacy/kyber-weave-arbiter hook", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A global Factory hook file written before the stop registration existed holds only
    /// PreToolUse and PostToolUse. The refresh must rewrite them and add SubagentStop, and must
    /// keep the user's own hook beside them.
    /// </summary>
    [Fact]
    public async Task Refresh_AddsSubagentStopToAnExistingGlobalFactoryHook()
    {
        string home = Path.Combine(_world.Install, "home");
        _world.Files.CreateDirectory(home);
        new FileUserHostConfigStore(_world.Files).WriteGlobalTargets(home, ["factory"]);
        string hookPath = Path.Combine(new SquadGlobalRoots(_ => null, home).ResolveGlobalRoot(SquadTarget.Factory), "hooks.json");
        _world.Files.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        _world.Files.WriteAllText(hookPath, """
            {
              "PreToolUse": [
                { "matcher": "Task", "hooks": [ { "type": "command", "command": "/legacy/kyber-weave-arbiter hook --harness factory --scope global", "timeout": 5 } ] },
                { "matcher": "Read", "hooks": [ { "type": "command", "command": "user-own-linter" } ] }
              ],
              "PostToolUse": [
                { "matcher": "Task", "hooks": [ { "type": "command", "command": "/legacy/kyber-weave-arbiter hook --harness factory --scope global", "timeout": 5 } ] }
              ]
            }
            """);

        Func<string, string?> environment = name => name switch
        {
            "HOME" => home,
            "APPDATA" => Path.Combine(home, ".config"),
            _ => null,
        };

        ArbiterHookRefresher refresher = _world.Refresher(environment, _ => { });
        ArbiterHookRefreshResult outcome = await refresher.RefreshAsync([]);

        Assert.Equal(1, outcome.Refreshed);
        string content = _world.Files.ReadAllText(hookPath);
        Assert.Contains("\"SubagentStop\"", content, StringComparison.Ordinal);
        Assert.Contains("\"SessionStart\"", content, StringComparison.Ordinal);
        Assert.Contains("\"SessionEnd\"", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter worker-guard --harness factory", content, StringComparison.Ordinal);
        Assert.Contains("\"matcher\": \"*\"", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter hook --harness factory --scope global", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/legacy/kyber-weave-arbiter hook", content, StringComparison.Ordinal);
        Assert.Contains("user-own-linter", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A global Claude settings file written before the stop registration existed holds only
    /// PreToolUse. The refresh must add the matcher-less SubagentStop entry Claude fires, and must
    /// keep the user's own hook beside it.
    /// </summary>
    [Fact]
    public async Task Refresh_AddsSubagentStopToAnExistingGlobalClaudeSettingsFile()
    {
        string home = Path.Combine(_world.Install, "home");
        _world.Files.CreateDirectory(home);
        new FileUserHostConfigStore(_world.Files).WriteGlobalTargets(home, ["claude"]);
        string hookPath = Path.Combine(
            new SquadGlobalRoots(_ => null, home).ResolveGlobalRoot(SquadTarget.Claude),
            "settings.json");
        _world.Files.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        _world.Files.WriteAllText(hookPath, """
            {
              "hooks": {
                "PreToolUse": [
                  { "matcher": "^(Agent|Task)$", "hooks": [ { "type": "command", "command": "/legacy/kyber-weave-arbiter hook --harness claude --scope global", "timeout": 5 } ] },
                  { "matcher": "Read", "hooks": [ { "type": "command", "command": "user-own-linter" } ] }
                ]
              }
            }
            """);

        Func<string, string?> environment = name => name switch
        {
            "HOME" => home,
            "APPDATA" => Path.Combine(home, ".config"),
            _ => null,
        };

        ArbiterHookRefresher refresher = _world.Refresher(environment, _ => { });
        ArbiterHookRefreshResult outcome = await refresher.RefreshAsync([]);

        Assert.Equal(1, outcome.Refreshed);
        string content = _world.Files.ReadAllText(hookPath);
        JsonObject root = JsonNode.Parse(content)!.AsObject();
        JsonArray stops = Assert.IsType<JsonArray>(root["hooks"]!["SubagentStop"]);
        JsonObject stop = Assert.IsType<JsonObject>(Assert.Single(stops));
        Assert.False(stop.ContainsKey("matcher"));
        JsonObject hook = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(stop["hooks"])));
        Assert.Equal(
            "kyber-weave-arbiter hook --harness claude --scope global",
            hook["command"]!.GetValue<string>());
        Assert.DoesNotContain("/legacy/kyber-weave-arbiter hook", content, StringComparison.Ordinal);
        Assert.Contains("user-own-linter", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Update-time refresh follows the user config gate: an already-installed global hook is refreshed
    /// only for harnesses named in <c>arbiter.global.targets</c>, and it never rewrites foreign bytes.
    /// </summary>
    [Fact]
    public async Task Update_RefreshesOnlyListedGlobalArbiterHooksWithoutTouchingForeignBytes()
    {
        string home = Path.Combine(_world.Install, "home");
        _world.Files.CreateDirectory(home);
        new FileUserHostConfigStore(_world.Files).WriteGlobalTargets(home, ["cursor"]);
        string hookPath = WriteLegacyGlobalHook(home, SquadTarget.Cursor, "/legacy/kyber-weave-arbiter hook");

        string foreign = Path.Combine(new SquadGlobalRoots(_ => null, home).ResolveGlobalRoot(SquadTarget.Cursor), "foreign.json");
        _world.Files.CreateDirectory(Path.GetDirectoryName(foreign)!);
        _world.Files.WriteAllText(foreign, "{\"user\":\"keep-me\"}");

        using ReleaseMapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");
        List<string> log = [];
        Func<string, string?> environment = name => name switch
        {
            "HOME" => home,
            "APPDATA" => Path.Combine(home, ".config"),
            _ => null,
        };

        ArbiterHookRefresher refresher = _world.Refresher(environment, log.Add);
        LoopbackFinishingProcess loopback = _world.Loopback(
            handler,
            host,
            log.Add,
            environment,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: _world.Targets(environment));
        using SelfUpdater updater = _world.CreateUpdater(
            handler,
            host,
            environment,
            log.Add,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: _world.Targets(environment),
            finishingProcess: loopback.Invoke);
        SelfUpdateOutcome outcome = await updater.RunAsync(new SelfUpdateOptions("0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        string content = _world.Files.ReadAllText(hookPath);
        Assert.Contains("kyber-weave-arbiter hook --harness cursor --scope global", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/legacy/kyber-weave-arbiter hook", content, StringComparison.Ordinal);
        Assert.Equal("{\"user\":\"keep-me\"}", _world.Files.ReadAllText(foreign), StringComparer.Ordinal);
    }

    /// <summary>
    /// A global receipt that owns more than one block survives an update: both targets' hook files
    /// are refreshed and both recorded blocks are rewritten, so neither reads as drift afterwards.
    /// This is the path <c>kyber-weave update</c> itself takes, which is not the Squad lifecycle a
    /// project root's update runs. Before a global block's identity was qualified by its target,
    /// the receipt holding two <c>hooks.json</c> blocks could not be written back at all.
    /// </summary>
    [Fact]
    public async Task Update_RefreshesEveryGlobalBlockAndRewritesItsRecordedEntries()
    {
        InstallArbiter();
        using ReleaseMapHandler handler = MapRelease("0.2.0", "osx-arm64", windows: false, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0", "osx-arm64");

        FakeSquadUserPaths userPaths = new(Path.Combine(_world.Install, "global-block-home"));
        string home = userPaths.HomeDirectory;
        _world.Files.CreateDirectory(home);
        SquadGlobalRoots roots = new(_ => null, home);
        SquadStateStore store = _world.SquadStore(userPaths);
        SeedGlobalBlocks(store, roots, home);
        new FileUserHostConfigStore(_world.Files).WriteGlobalTargets(home, ["cursor", "codex"]);
        StaleHookTimeouts(GlobalHookFile(roots, SquadTarget.Cursor));
        StaleHookTimeouts(GlobalHookFile(roots, SquadTarget.Codex));

        List<string> log = [];
        Func<string, string?> environment = name => name switch
        {
            "HOME" => home,
            "APPDATA" => userPaths.ApplicationDataDirectory,
            _ => null,
        };

        ArbiterHookRefresher refresher = _world.Refresher(environment, log.Add);
        LoopbackFinishingProcess loopback = _world.Loopback(
            handler,
            host,
            log.Add,
            environment,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: _world.Targets(environment));
        using SelfUpdater updater = _world.CreateUpdater(
            handler,
            host,
            environment,
            log.Add,
            captureProcess: (_, _) => QuietSuccess,
            arbiterHookRefresher: refresher,
            arbiterTargets: _world.Targets(environment),
            finishingProcess: loopback.Invoke);
        SelfUpdateOutcome outcome = await updater.RunAsync(new SelfUpdateOptions("0.2.0", false, false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains(
            log,
            line => line.Contains("refreshed arbiter hooks for 2 project(s)", StringComparison.Ordinal));
        Assert.DoesNotContain("999", _world.Files.ReadAllText(GlobalHookFile(roots, SquadTarget.Cursor)), StringComparison.Ordinal);
        Assert.DoesNotContain("999", _world.Files.ReadAllText(GlobalHookFile(roots, SquadTarget.Codex)), StringComparison.Ordinal);

        SquadReceipt after = Assert.IsType<SquadReceipt>(store.ReadReceipt(home, SquadDeploymentScope.Global));
        Assert.Equal(["cursor", "codex"], after.Blocks.Select(block => block.Target).ToArray());
        AssertBlocksMatchTheirFiles(after, roots);
    }

    /// <summary>
    /// A global receipt records many files for one harness, so a refresh of that harness's hook
    /// file has to pick the hook entry out by path. Matching the target alone patched whichever
    /// file the receipt listed first — an agent file — and left the hook entry stale, which the
    /// next <c>squad status --global</c> reported as drift on a hook file the refresh had just
    /// rewritten.
    /// </summary>
    [Fact]
    public async Task Refresh_UpdatesTheHookEntryAndLeavesAnotherFileForTheSameTargetUntouched()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_world.Install, "home-with-a-sibling-file"));
        string home = userPaths.HomeDirectory;
        _world.Files.CreateDirectory(home);
        SquadGlobalRoots roots = new(_ => null, home);
        string root = roots.ResolveGlobalRoot(SquadTarget.Copilot);
        const string agentRelativePath = "agents/dev.md";
        const string hookRelativePath = "hooks/kyber-arbiter.json";
        string agentPath = Path.Combine(root, agentRelativePath);
        string hookPath = Path.Combine(root, hookRelativePath);
        _world.Files.CreateDirectory(Path.GetDirectoryName(agentPath)!);
        _world.Files.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        _world.Files.WriteAllText(agentPath, "agent body");
        _world.Files.WriteAllText(hookPath, "{\"version\":1,\"hooks\":{\"task\":[]}}");

        SquadStateStore store = _world.SquadStore(userPaths);
        SquadOwnedFile agentEntry = new(agentRelativePath, Digest("agent body"), "copilot", false);
        SquadOwnedFile hookEntry = new(
            hookRelativePath,
            Digest("{\"version\":1,\"hooks\":{\"task\":[]}}"),
            "copilot",
            false);
        string receiptPath = store.ResolveReceiptPath(home, SquadDeploymentScope.Global);
        _world.Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        _world.Files.WriteAllText(
            receiptPath,
            store.SerializeReceipt(new SquadReceipt(
                "kyber-squad.receipt/v2",
                SquadDeploymentScope.Global,
                ".",
                GlobalInstalledAt,
                [],
                [agentEntry, hookEntry])));
        new FileUserHostConfigStore(_world.Files).WriteGlobalTargets(home, ["copilot"]);

        Func<string, string?> environment = name => name switch
        {
            "HOME" => home,
            "APPDATA" => userPaths.ApplicationDataDirectory,
            _ => null,
        };

        ArbiterHookRefreshResult outcome = await _world.Refresher(environment, _ => { }).RefreshAsync([]);

        Assert.Equal(1, outcome.Refreshed);
        string refreshedHook = _world.Files.ReadAllText(hookPath);
        Assert.NotEqual("{\"version\":1,\"hooks\":{\"task\":[]}}", refreshedHook, StringComparer.Ordinal);

        SquadReceipt after = Assert.IsType<SquadReceipt>(store.ReadReceipt(home, SquadDeploymentScope.Global));
        Assert.Equal(agentRelativePath, after.Files[0].RelativePath);
        SquadOwnedFile agentAfter = after.Files.Single(file => file.RelativePath == agentRelativePath);
        SquadOwnedFile hookAfter = after.Files.Single(file => file.RelativePath == hookRelativePath);
        Assert.Equal(Digest("agent body"), agentAfter.Sha256);
        Assert.Equal(Digest(refreshedHook), hookAfter.Sha256);
    }

    private static string Digest(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string GlobalHookFile(SquadGlobalRoots roots, SquadTarget target) =>
        Path.Combine(roots.ResolveGlobalRoot(target), SharedHooksPath);

    /// <summary>
    /// Seeds one home with two global blocks whose recorded digests are those of the bytes just
    /// written, so the deployment starts consistent. The commands are the exact ownership
    /// signature, which is what lets a refresh replace the entries in place rather than append
    /// beside them.
    /// </summary>
    private void SeedGlobalBlocks(SquadStateStore store, SquadGlobalRoots roots, string home)
    {
        IReadOnlyList<SquadHookOwnedEntry> cursorOwned = new SquadHookJsonFile(_world.Files, _world.Environment()).SpliceFile(
            SquadHookBlockFormat.Cursor,
            GlobalHookFile(roots, SquadTarget.Cursor),
            [CursorHookEntry()],
            [CursorHookEntry()]);
        IReadOnlyList<SquadHookOwnedEntry> codexOwned = new SquadHookJsonFile(_world.Files, _world.Environment()).SpliceFile(
            SquadHookBlockFormat.Codex,
            GlobalHookFile(roots, SquadTarget.Codex),
            [MatcherGroupHookEntry()],
            [MatcherGroupHookEntry()]);

        string receiptPath = store.ResolveReceiptPath(home, SquadDeploymentScope.Global);
        _world.Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        _world.Files.WriteAllText(
            receiptPath,
            store.SerializeReceipt(new SquadReceipt(
                "kyber-squad.receipt/v3",
                SquadDeploymentScope.Global,
                ".",
                GlobalInstalledAt,
                [],
                [])
            {
                Blocks =
                [
                    OwnedBlock("cursor", cursorOwned),
                    OwnedBlock("codex", codexOwned),
                ],
            }));
    }

    private static SquadOwnedBlock OwnedBlock(string target, IReadOnlyList<SquadHookOwnedEntry> owned) =>
        new(
            SharedHooksPath,
            target,
            true,
            [.. owned.Select(entry => new SquadOwnedBlockEntry(entry.Location, entry.Digest))]);

    private static JsonObject CursorHookEntry() =>
        new()
        {
            ["command"] = ArbiterHookWiring.HookCommandLine(SquadTarget.Cursor, SquadDeploymentScope.Global),
            ["matcher"] = "Task",
            ["timeout"] = 5,
            ["failClosed"] = true,
        };

    private static JsonObject MatcherGroupHookEntry() =>
        new()
        {
            ["matcher"] = "Task",
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = ArbiterHookWiring.HookCommandLine(SquadTarget.Codex, SquadDeploymentScope.Global),
                    ["timeout"] = 5,
                }),
        };

    /// <summary>
    /// Ages every entry in a hook file without touching its command, so the recorded digests go
    /// stale while the entries remain Squad's own. A refresh then has real work to do: it replaces
    /// them and has to record the new digests, which is the update this test pins.
    /// </summary>
    private void StaleHookTimeouts(string path)
    {
        JsonNode root = JsonNode.Parse(_world.Files.ReadAllText(path))!;
        StaleTimeouts(root);
        _world.Files.WriteAllText(path, root.ToJsonString());
    }

    private static void StaleTimeouts(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(property => property.Key).ToArray())
            {
                if (string.Equals(key, "timeout", StringComparison.Ordinal)
                    && obj[key] is JsonValue value
                    && value.TryGetValue(out int _))
                {
                    obj[key] = 999;
                    continue;
                }

                StaleTimeouts(obj[key]);
            }

            return;
        }

        if (node is not JsonArray array)
        {
            return;
        }

        foreach (JsonNode? item in array)
        {
            StaleTimeouts(item);
        }
    }

    /// <summary>
    /// Asserts the refreshed receipt and the two files agree: every recorded entry digest still
    /// matches the node at its container. Without this the refresh could rewrite both files and
    /// leave a stale record behind, which reads as drift on the next status.
    /// </summary>
    private void AssertBlocksMatchTheirFiles(SquadReceipt receipt, SquadGlobalRoots roots)
    {
        Assert.Equal(2, receipt.Blocks.Count);
        foreach (SquadOwnedBlock block in receipt.Blocks)
        {
            Assert.True(
                SquadTargetCatalog.TryResolve(block.Target, out SquadTarget target),
                $"The receipt records block target '{block.Target}', which is not a canonical Squad target.");

            SquadHookBlockFormat format = target switch
            {
                SquadTarget.Cursor => SquadHookBlockFormat.Cursor,
                SquadTarget.Codex => SquadHookBlockFormat.Codex,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(receipt),
                    target,
                    "Only cursor and codex blocks are seeded here."),
            };

            Dictionary<string, string> expected = block.Entries
                .ToDictionary(entry => entry.Container, entry => entry.Sha256, StringComparer.Ordinal);
            IReadOnlyList<SquadHookDrift> drifts = SquadHookJsonBlock.FindDrift(
                format,
                _world.Files.ReadAllText(GlobalHookFile(roots, target)),
                expected);

            Assert.True(
                drifts.Count == 0,
                $"The {block.Target} block drifted after the refresh: "
                + string.Join("; ", drifts.Select(drift => $"{drift.Location} {drift.Reason}")));
        }
    }

    private string WriteLegacyGlobalHook(string home, SquadTarget target, string legacyCommand)
    {
        SquadGlobalRoots globalRoots = new(_ => null, home);
        string hookPath = Path.Combine(globalRoots.ResolveGlobalRoot(target), "hooks.json");
        _world.Files.CreateDirectory(Path.GetDirectoryName(hookPath)!);

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

        _world.Files.WriteAllText(hookPath, content);
        return hookPath;
    }

    private SelfUpdateHost CreateHost(string currentVersion, string rid, bool windows = false) =>
        _world.CreateHost(currentVersion, rid, windows);

    private string InstallArbiter(bool windows = false) => _world.InstallArbiter(windows);

    private static ReleaseMapHandler MapRelease(
        string version,
        string rid,
        bool windows,
        bool withKyberDash = false,
        bool withArbiter = false) =>
        UpdateRelease.Map(version, rid, windows, withKyberDash, withArbiter);

    private sealed class RecordingArbiterHookRefresher : IArbiterHookRefresher
    {
        public int Calls { get; private set; }

        public List<string> RefreshedRoots { get; } = [];

        /// <summary>When unset, every input root is reported as refreshed.</summary>
        public ArbiterHookRefreshResult? Result { get; set; }

        public Task<ArbiterHookRefreshResult> RefreshAsync(
            IReadOnlyList<string> projectRoots,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(projectRoots);
            Calls++;
            RefreshedRoots.AddRange(projectRoots);
            return Task.FromResult(Result ?? new ArbiterHookRefreshResult(projectRoots.Count, Failed: 0));
        }
    }
}
