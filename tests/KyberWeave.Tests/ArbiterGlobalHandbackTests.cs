using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A background sub-agent reports back through <c>SubagentHandback</c>, not through the dispatcher's
/// <c>PostToolUse</c>. The user-level Claude hook must see that return, once, or the next dispatch
/// is refused as if the earlier step never finished.
/// </summary>
public sealed class ArbiterGlobalHandbackTests : IDisposable
{
    private const string HandbackMatcher = "^SubagentHandback$";
    private const string ToolUseId = "toolu-handback-1";

    private static readonly string AgentFile = Path.Combine(".claude", "agents", "csharp-dev.md");

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_GlobalClaudeBlock_RecordsSubagentHandbackOnPreToolUseOnly()
    {
        string home = Path.Combine(_fixture.Path, "home");
        string claudeRoot = Path.Combine(home, "custom-claude");
        Directory.CreateDirectory(claudeRoot);
        SquadGlobalRoots globalRoots = new(
            name => string.Equals(name, "CLAUDE_CONFIG_DIR", StringComparison.Ordinal) ? claudeRoot : null,
            home);

        SquadRenderResult result = await new ClaudeRenderer().RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Global,
            GlobalRoots: globalRoots,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadRenderedBlock block = Assert.Single(result.Blocks!, b => b.Target == "claude");
        string[] pre = block.Entries.Where(e => e.Container == "PreToolUse").Select(e => e.Entry["matcher"]!.GetValue<string>()).ToArray();
        string[] post = block.Entries.Where(e => e.Container == "PostToolUse").Select(e => e.Entry["matcher"]!.GetValue<string>()).ToArray();
        Assert.Contains(HandbackMatcher, pre);
        Assert.DoesNotContain(HandbackMatcher, post);
    }

    [Fact]
    public async Task GlobalScope_WhenNoProjectHookCoversTheHandback_ClaimsAndEvaluatesIt()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = Handback(scenario.ProjectRoot);

        HookRun run = await scenario.RunGlobal(payload);

        Assert.Equal(0, run.Exit);
        string claim = ArbiterGlobalHookScenario.ClaimPath(scenario.ProjectRoot, ToolUseId, "handback");
        Assert.True(File.Exists(claim), $"The user-level hook must claim the hand-back: missing {claim}. Stderr: {run.Stderr}");
        Assert.Equal("global", (await File.ReadAllTextAsync(claim)).Trim());
    }

    [Fact]
    public async Task GlobalScope_WhenTheAgentFileRecordsTheHandbackAndClaimedIt_DefersToIt()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(
            AgentFile,
            ArbiterGlobalHookScenario.HookDocument(
                "csharp-dev",
                ArbiterGlobalHookScenario.CsharpDevCommand,
                ("PreToolUse", HandbackMatcher)));
        ArbiterGlobalHookScenario.WriteClaim(scenario.ProjectRoot, ToolUseId, "handback", "project");

        HookRun run = await scenario.RunGlobal(Handback(scenario.ProjectRoot));

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        string claim = ArbiterGlobalHookScenario.ClaimPath(scenario.ProjectRoot, ToolUseId, "handback");
        Assert.Equal("project", (await File.ReadAllTextAsync(claim)).Trim());
    }

    private static string Handback(string root)
    {
        string fixturePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests", "Fixtures", "arbiter-hooks", "claude", "pre-subagent-handback.json");
        JsonObject payload = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
        payload["cwd"] = root;
        payload["transcript_path"] = Path.Combine(root, "transcript.jsonl");
        return payload.ToJsonString();
    }
}
