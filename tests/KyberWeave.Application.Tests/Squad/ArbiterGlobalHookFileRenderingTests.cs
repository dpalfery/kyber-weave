using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>
/// Pins the Global-scope Arbiter hook contract: an enabled Arbiter render at global scope
/// writes an owned block into the target's real global hook file beneath the injected
/// <see cref="SquadGlobalRoots" /> root, and every command in the block carries the global
/// <c>--scope global</c> flag. This is the shared-file block case already represented by the
/// project-scope renderer contract, not the ZCode or CommandCode surfaces that are not a
/// shared hook block in the current model.
/// </summary>
public sealed class ArbiterGlobalHookFileRenderingTests
{
    private static readonly DateTimeOffset InstalledAt = new(2026, 11, 05, 12, 00, 00, TimeSpan.Zero);

    private readonly InMemorySquadState _state = new();

    [Theory]
    [InlineData(SquadTarget.Codex, SquadHookBlockFormat.Codex, "hooks.json")]
    [InlineData(SquadTarget.Cursor, SquadHookBlockFormat.Cursor, "hooks.json")]
    [InlineData(SquadTarget.Antigravity, SquadHookBlockFormat.Antigravity, "hooks.json")]
    [InlineData(SquadTarget.Factory, SquadHookBlockFormat.Factory, "hooks.json")]
    [InlineData(SquadTarget.Devin, SquadHookBlockFormat.Devin, "config.json")]
    public void CreateInstall_WhenGlobalScopeHasEnabledArbiter_WritesOwnedBlockUnderInjectedGlobalRoot(
        SquadTarget target,
        SquadHookBlockFormat format,
        string relativePath)
    {
        string root = _state.CreateRoot("project");
        string home = _state.Layout.PhysicalOf(_state.Layout.Home);
        SquadGlobalRoots globalRoots = new(_ => null, home);

        SquadRenderedBlock block = CreateBlock(target, format);

        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            globalRoots: globalRoots,
            blocks: [block]);

        _state.Transaction().Execute(plan);

        string actualPath = Path.Combine(
            globalRoots.ResolveGlobalRoot(target),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(_state.Files.FileExists(actualPath), $"Expected global hook file '{actualPath}' to exist.");

        string content = _state.Files.ReadAllText(actualPath);
        Assert.Contains($"kyber-weave-arbiter hook --harness {SquadTargetCatalog.GetToken(target)} --scope global", content, StringComparison.Ordinal);
        Assert.DoesNotContain("project-only", content, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInstall_GlobalScope_KeepsUserHooksAndOnlyOwnsTheManagedEntries()
    {
        string root = _state.CreateRoot("project");
        string home = _state.Layout.PhysicalOf(_state.Layout.Home);
        SquadGlobalRoots globalRoots = new(_ => null, home);

        string hookPath = Path.Combine(globalRoots.ResolveGlobalRoot(SquadTarget.Cursor), "hooks.json");
        _state.Files.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        _state.Files.WriteAllText(hookPath, "{ \"hooks\": { \"preToolUse\": [{ \"command\": \"user-own-lint\", \"matcher\": \"Edit\", \"timeout\": 7, \"failClosed\": false }], \"postToolUse\": [] } }");

        SquadRenderedBlock block = CreateBlock(SquadTarget.Cursor, SquadHookBlockFormat.Cursor);

        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            globalRoots: globalRoots,
            blocks: [block]);

        _state.Transaction().Execute(plan);

        string content = _state.Files.ReadAllText(hookPath);
        Assert.Contains("user-own-lint", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter hook --harness cursor --scope global", content, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInstallAndUninstall_GlobalFactory_OwnsSubagentStopAndKeepsTheUsersOwn()
    {
        string home = _state.Layout.PhysicalOf(_state.Layout.Home);
        SquadGlobalRoots globalRoots = new(_ => null, home);
        SquadStateStore store = _state.Store;

        string hookPath = Path.Combine(globalRoots.ResolveGlobalRoot(SquadTarget.Factory), "hooks.json");
        _state.Files.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        _state.Files.WriteAllText(hookPath, """
            {"SubagentStop": [{"hooks": [{"type": "command", "command": "user-own-stop-notifier"}]}]}
            """);

        _state.Transaction().Execute(_state.Planner.CreateInstall(
            home,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            globalRoots: globalRoots,
            blocks: [CreateBlock(SquadTarget.Factory, SquadHookBlockFormat.Factory)]));

        JsonObject installed = Assert.IsType<JsonObject>(JsonNode.Parse(_state.Files.ReadAllText(hookPath)));
        JsonArray stop = Assert.IsType<JsonArray>(installed["SubagentStop"]);
        Assert.Equal(2, stop.Count);
        JsonObject squadStop = Assert.IsType<JsonObject>(stop[1]);
        Assert.Equal(["hooks"], squadStop.Select(property => property.Key).ToArray());
        Assert.Contains("kyber-weave-arbiter hook --harness factory --scope global", squadStop.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("user-own-stop-notifier", stop.ToJsonString(), StringComparison.Ordinal);

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(home, SquadDeploymentScope.Global));
        _state.Transaction().Execute(_state.Planner.CreateUninstall(
            home,
            SquadDeploymentScope.Global,
            receipt,
            globalRoots));

        string remaining = _state.Files.ReadAllText(hookPath);
        Assert.DoesNotContain("kyber-weave-arbiter hook --harness factory", remaining, StringComparison.Ordinal);
        Assert.Contains("user-own-stop-notifier", remaining, StringComparison.Ordinal);
    }

    /// <summary>
    /// Before the global path was fixed, the installer recorded the Antigravity block at
    /// <c>.agents/hooks.json</c> beneath the global root, a file Antigravity never reads. That
    /// stale record must stay removable, so <c>arbiter uninstall --target antigravity</c> clears
    /// it and the operator can reinstall; no migration code repairs it.
    /// </summary>
    [Fact]
    public void CreateUninstall_StaleAntigravityRecordAtAgentsHooksJson_RemovesTheStaleGroup()
    {
        string home = _state.Layout.PhysicalOf(_state.Layout.Home);
        SquadGlobalRoots globalRoots = new(_ => null, home);
        SquadStateStore store = _state.Store;

        SquadRenderedBlock current = CreateBlock(SquadTarget.Antigravity, SquadHookBlockFormat.Antigravity);
        SquadRenderedBlock stale = current with { RelativePath = ".agents/hooks.json" };
        _state.Transaction().Execute(_state.Planner.CreateInstall(
            home,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            globalRoots: globalRoots,
            blocks: [stale]));

        string staleFile = Path.Combine(globalRoots.ResolveGlobalRoot(SquadTarget.Antigravity), ".agents", "hooks.json");
        Assert.Contains("kyber-arbiter", _state.Files.ReadAllText(staleFile), StringComparison.Ordinal);
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(home, SquadDeploymentScope.Global));
        Assert.Equal(".agents/hooks.json", Assert.Single(receipt.Blocks).RelativePath);

        _state.Transaction().Execute(_state.Planner.CreateUninstall(
            home,
            SquadDeploymentScope.Global,
            receipt,
            globalRoots));

        Assert.False(_state.Files.FileExists(staleFile), "Uninstall left the stale kyber-arbiter group behind.");
        Assert.Null(store.ReadReceipt(home, SquadDeploymentScope.Global));
    }

    private static SquadRenderedBlock CreateBlock(SquadTarget target, SquadHookBlockFormat format) =>
        format switch
        {
            SquadHookBlockFormat.Cursor => new SquadRenderedBlock(
                SquadTargetCatalog.GetToken(target),
                "hooks.json",
                format,
                [
                    new SquadRenderedBlockEntry(
                        "preToolUse",
                        CursorEntry($"kyber-weave-arbiter hook --harness {SquadTargetCatalog.GetToken(target)} --scope global", "probe-1"))
                ]),
            SquadHookBlockFormat.Codex => new SquadRenderedBlock(
                SquadTargetCatalog.GetToken(target),
                "hooks.json",
                format,
                [
                    new SquadRenderedBlockEntry(
                        "PreToolUse",
                        MatcherGroup($"kyber-weave-arbiter hook --harness {SquadTargetCatalog.GetToken(target)} --scope global", "probe-1"))
                ]),
            SquadHookBlockFormat.Antigravity => new SquadRenderedBlock(
                SquadTargetCatalog.GetToken(target),
                "hooks.json",
                format,
                [
                    new SquadRenderedBlockEntry(
                        "kyber-arbiter",
                        new JsonObject
                        {
                            ["PreToolUse"] = MatcherGroup($"kyber-weave-arbiter hook --harness {SquadTargetCatalog.GetToken(target)} --scope global", "probe-1"),
                            ["PostToolUse"] = MatcherGroup($"kyber-weave-arbiter hook --harness {SquadTargetCatalog.GetToken(target)} --scope global", "probe-1"),
                        })
                ]),
            SquadHookBlockFormat.Factory => new SquadRenderedBlock(
                SquadTargetCatalog.GetToken(target),
                "hooks.json",
                format,
                [
                    new SquadRenderedBlockEntry(
                        "PreToolUse",
                        MatcherGroup($"kyber-weave-arbiter hook --harness {SquadTargetCatalog.GetToken(target)} --scope global", "probe-1")),
                    new SquadRenderedBlockEntry(
                        "SubagentStop",
                        MatcherGroup($"kyber-weave-arbiter hook --harness {SquadTargetCatalog.GetToken(target)} --scope global", matcher: null)),
                ]),
            SquadHookBlockFormat.Devin => new SquadRenderedBlock(
                SquadTargetCatalog.GetToken(target),
                "config.json",
                format,
                [
                    new SquadRenderedBlockEntry(
                        "PreToolUse",
                        MatcherGroup($"kyber-weave-arbiter hook --harness {SquadTargetCatalog.GetToken(target)} --scope global", "probe-1"))
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown hook block format."),
        };

    private static JsonObject CursorEntry(string command, string matcher = "Task") =>
        new()
        {
            ["command"] = command,
            ["matcher"] = matcher,
            ["timeout"] = 5,
            ["failClosed"] = true,
        };

    /// <summary>One matcher group; a null <paramref name="matcher"/> omits the key, as Factory's SubagentStop group does.</summary>
    private static JsonObject MatcherGroup(string command, string? matcher = "Task")
    {
        JsonObject group = new();
        if (matcher is not null)
        {
            group["matcher"] = matcher;
        }

        group["hooks"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["timeout"] = 5,
            });
        return group;
    }

    private static SquadLock Lock(string version = "1.2.3") =>
        new(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["cursor"],
            [],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
