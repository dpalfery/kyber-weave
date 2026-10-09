using System.IO;
using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter MCP grants on the ZCode and Devin renderers (design §10.6). The
/// <c>kyber-weave-arbiter</c> server is excluded from the standard MCP grant on both targets.
/// Its three tools are granted on ZCode to every agent whose profile allows
/// <c>decision.query</c>, the pure orchestrator included, and on Devin never. The fixture
/// corpus is overlaid with a <c>decision.query</c> vocabulary entry and an Arbiter roster so
/// the grant is exercised against real parsing rather than a stub.
/// </summary>
public sealed class ArbiterMcpGrantRenderingTests : IDisposable
{
    private const string ArbiterServer = "kyber-weave-arbiter";
    private const string ArbiterPrefix = "mcp__kyber-weave-arbiter__";
    private const string StandardTool = "mcp__context7__query-docs";

    private static readonly string[] ArbiterTools =
    [
        "mcp__kyber-weave-arbiter__arbiter_evaluate",
        "mcp__kyber-weave-arbiter__arbiter_rules",
        "mcp__kyber-weave-arbiter__arbiter_status",
    ];

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public ArbiterMcpGrantRenderingTests()
    {
        WriteOverlay();
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ZCode_GrantsArbiterToolsToEveryDecisionQueryAllowAgent_IncludingPureOrchestrator()
    {
        SquadRenderResult result = await RenderAsync(new ZCodeRenderer(), SquadTarget.ZCode);

        string conductor = ContentOf(result, ".zcode/agents/conductor.md");
        string codeReviewer = ContentOf(result, ".zcode/agents/code-reviewer.md");

        foreach (string tool in ArbiterTools)
        {
            Assert.Contains($"  - {tool}\n", conductor, StringComparison.Ordinal);
            Assert.Contains($"  - {tool}\n", codeReviewer, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ZCode_WithholdsArbiterToolsFromEveryDecisionQueryDenyAgent()
    {
        SquadRenderResult result = await RenderAsync(new ZCodeRenderer(), SquadTarget.ZCode);

        foreach (string agent in new[] { "architect", "csharp-dev", "docs-dev", "research-agent" })
        {
            string content = ContentOf(result, $".zcode/agents/{agent}.md");
            Assert.DoesNotContain(ArbiterPrefix, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ZCode_StandardGrantExcludesTheArbiterServer()
    {
        SquadRenderResult result = await RenderAsync(new ZCodeRenderer(), SquadTarget.ZCode);

        string csharpDev = ContentOf(result, ".zcode/agents/csharp-dev.md");
        Assert.Contains($"  - {StandardTool}\n", csharpDev, StringComparison.Ordinal);
        Assert.DoesNotContain(ArbiterPrefix, csharpDev, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZCode_DoesNotReportTheArbiterServerAsWithheldMcp()
    {
        SquadRenderResult result = await RenderAsync(new ZCodeRenderer(), SquadTarget.ZCode);

        Assert.DoesNotContain(
            result.Degradations,
            record => (record.Details ?? string.Empty).Contains(ArbiterServer, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Devin_ExcludesTheArbiterServerFromEveryAgent()
    {
        SquadRenderResult result = await RenderAsync(new DevinRenderer(), SquadTarget.Devin);

        foreach (SquadDeploymentFile file in result.Files)
        {
            Assert.DoesNotContain(
                ArbiterPrefix,
                Encoding.UTF8.GetString(file.Content.Span),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Devin_StillGrantsTheStandardServerToReadingAgents()
    {
        SquadRenderResult result = await RenderAsync(new DevinRenderer(), SquadTarget.Devin);

        Assert.Contains(
            StandardTool,
            ContentOf(result, ".devin/agents/code-reviewer/AGENT.md"),
            StringComparison.Ordinal);
    }

    private void WriteOverlay()
    {
        File.WriteAllText(Path.Combine(_fixture.Path, "profiles/capabilities.yml"), """
            schema: kyber-squad.capability-profiles/v1
            capabilities:
              - filesystem.read
              - delegate
              - decision.query
            profiles:
              orchestrator:
                permissions:
                  filesystem.read: allow
                  delegate: allow
                  decision.query: allow
              architect:
                permissions:
                  filesystem.read: allow
                  delegate: allow
                  decision.query: deny
              product-planning:
                permissions:
                  filesystem.read: allow
                  delegate: allow
                  decision.query: deny
              reviewer:
                permissions:
                  filesystem.read: allow
                  delegate: allow
                  decision.query: allow
              worker:
                permissions:
                  filesystem.read: allow
                  delegate: deny
                  decision.query: deny
              publishing-worker:
                permissions:
                  filesystem.read: allow
                  delegate: deny
                  decision.query: deny
              documentation:
                permissions:
                  filesystem.read: allow
                  delegate: deny
                  decision.query: deny
              read-only:
                permissions:
                  filesystem.read: allow
                  delegate: deny
                  decision.query: deny
            """);

        File.WriteAllText(Path.Combine(_fixture.Path, "toolchain.yml"), """
            schema: kyber-squad.toolchain/v1
            required-features:
              - agent-ir/v1
            required-mcp-tools:
              context7:
                - query-docs
              kyber-weave-arbiter:
                - arbiter_evaluate
                - arbiter_rules
                - arbiter_status
            validated-release: null
            """);
    }

    private async Task<SquadRenderResult> RenderAsync(ISquadRenderer renderer, SquadTarget target)
    {
        SquadRendererRegistry registry = new([renderer]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [target],
            Scope: SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }

    private static string ContentOf(SquadRenderResult result, string relativePath)
    {
        SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == relativePath);
        return Encoding.UTF8.GetString(file.Content.Span);
    }
}
