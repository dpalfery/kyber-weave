using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

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
        using TempDirectory fixture = new();
        string home = Path.Combine(fixture.Path, "home");
        Directory.CreateDirectory(home);
        SquadGlobalRoots globalRoots = new(_ => null, home);

        SquadRenderedBlock block = CreateBlock(target, format);

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            globalRoots: globalRoots,
            blocks: [block]);

        new SquadTransaction(Store(fixture.Path)).Execute(plan);

        string actualPath = Path.Combine(
            globalRoots.ResolveGlobalRoot(target),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(actualPath), $"Expected global hook file '{actualPath}' to exist.");

        string content = File.ReadAllText(actualPath);
        Assert.Contains($"kyber-weave-arbiter hook --harness {SquadTargetCatalog.GetToken(target)} --scope global", content, StringComparison.Ordinal);
        Assert.DoesNotContain("project-only", content, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInstall_GlobalScope_KeepsUserHooksAndOnlyOwnsTheManagedEntries()
    {
        using TempDirectory fixture = new();
        string home = Path.Combine(fixture.Path, "home");
        Directory.CreateDirectory(home);
        SquadGlobalRoots globalRoots = new(_ => null, home);

        string hookPath = Path.Combine(globalRoots.ResolveGlobalRoot(SquadTarget.Cursor), "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        File.WriteAllText(hookPath, "{ \"hooks\": { \"preToolUse\": [{ \"command\": \"user-own-lint\", \"matcher\": \"Edit\", \"timeout\": 7, \"failClosed\": false }], \"postToolUse\": [] } }");

        SquadRenderedBlock block = CreateBlock(SquadTarget.Cursor, SquadHookBlockFormat.Cursor);

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            globalRoots: globalRoots,
            blocks: [block]);

        new SquadTransaction(Store(fixture.Path)).Execute(plan);

        string content = File.ReadAllText(hookPath);
        Assert.Contains("user-own-lint", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter hook --harness cursor --scope global", content, StringComparison.Ordinal);
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
                        MatcherGroup($"kyber-weave-arbiter hook --harness {SquadTargetCatalog.GetToken(target)} --scope global", "probe-1"))
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

    private static JsonObject MatcherGroup(string command, string matcher = "Task") =>
        new()
        {
            ["matcher"] = matcher,
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 5,
                })
        };

    private static SquadStateStore Store(string applicationData) =>
        new(new FakeSquadUserPaths(applicationData));

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
