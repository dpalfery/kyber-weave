using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

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

    private readonly InMemoryFileSystem _files = new();
    private readonly ArbiterSquadFixture _fixture;
    private readonly ArbiterGlobalHookScenario _scenario = ArbiterGlobalHookScenario.Create();

    public ArbiterGlobalHandbackTests() => _fixture = ArbiterSquadFixture.CreateInMemory(_files);

    public void Dispose()
    {
        _fixture.Dispose();
        _scenario.Dispose();
    }

    [Fact]
    public async Task RenderAsync_GlobalClaudeBlock_RecordsSubagentHandbackOnPreToolUseOnly()
    {
        string home = Path.Combine(_fixture.Path, "home");
        string claudeRoot = Path.Combine(home, "custom-claude");
        _files.CreateDirectory(claudeRoot);
        SquadGlobalRoots globalRoots = new(
            name => string.Equals(name, "CLAUDE_CONFIG_DIR", StringComparison.Ordinal) ? claudeRoot : null,
            home);

        SquadRenderResult result = await new ClaudeRenderer().RenderAsync(_fixture.Prepare(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Global,
            GlobalRoots: globalRoots,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5))));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadRenderedBlock block = Assert.Single(result.Blocks!, b => b.Target == "claude");
        string[] pre = block.Entries.Where(e => e.Container == "PreToolUse").Select(e => e.Entry["matcher"]!.GetValue<string>()).ToArray();
        string[] post = block.Entries.Where(e => e.Container == "PostToolUse").Select(e => e.Entry["matcher"]!.GetValue<string>()).ToArray();
        Assert.Contains(HandbackMatcher, pre);
        Assert.DoesNotContain(HandbackMatcher, post);
    }

    [Fact]
    public void GlobalScope_WhenNoProjectHookCoversTheHandback_ClaimsAndEvaluatesIt()
    {
        string payload = HandbackPayload();

        HookRun run = _scenario.RunGlobal(payload);

        Assert.Equal(0, run.Exit);
        Assert.True(_scenario.ClaimExists(ToolUseId, "handback"), $"The user-level hook must claim the hand-back. Stderr: {run.Stderr}");
        Assert.Equal("global", _scenario.ClaimOwner(ToolUseId, "handback"));
    }

    [Fact]
    public void GlobalScope_WhenTheAgentFileRecordsTheHandbackAndClaimedIt_DefersToIt()
    {
        _scenario.WriteProjectFile(
            AgentFile,
            ArbiterGlobalHookScenario.HookDocument(
                "csharp-dev",
                ArbiterGlobalHookScenario.CsharpDevCommand,
                ("PreToolUse", HandbackMatcher)));
        _scenario.WriteClaim(ToolUseId, "handback", "project");

        HookRun run = _scenario.RunGlobal(HandbackPayload());

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.Equal("project", _scenario.ClaimOwner(ToolUseId, "handback"));
    }

    private string HandbackPayload() => ArbiterGlobalHookScenario.Handback(_scenario.ProjectRoot);
}
