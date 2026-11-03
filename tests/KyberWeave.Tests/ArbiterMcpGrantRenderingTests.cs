using System.IO;
using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter MCP grants on the ZCode and Devin renderers (design §10.6, review
/// 20.1 Major 3). The <c>kyber-weave-arbiter</c> server is excluded from the standard MCP
/// grant on both targets. On ZCode its three tools are granted only when the render
/// request carries an enabled, project-scope Arbiter wiring, and only to the orchestrator
/// and the reviewer — the two agents whose contracts call <c>arbiter_evaluate</c>. With
/// the wiring null, disabled or global the render is byte-identical to a corpus with no
/// Arbiter support at all. Devin never grants the server. The fixture corpus is overlaid
/// with a <c>decision.query</c> vocabulary entry and an Arbiter roster so the grant is
/// exercised against real parsing rather than a stub.
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
        WriteCapabilities(_fixture);
        WriteToolchain(_fixture, withArbiter: true);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ZCode_EnabledProjectWiring_GrantsArbiterToolsOnlyToTheOrchestratorAndReviewer()
    {
        SquadRenderResult result = await RenderAsync(
            new ZCodeRenderer(), SquadTarget.ZCode, new SquadArbiterWiring(true, 30));

        string conductor = ContentOf(result, ".zcode/agents/conductor.md");
        string codeReviewer = ContentOf(result, ".zcode/agents/code-reviewer.md");

        foreach (string tool in ArbiterTools)
        {
            Assert.Contains($"  - {tool}\n", conductor, StringComparison.Ordinal);
            Assert.Contains($"  - {tool}\n", codeReviewer, StringComparison.Ordinal);
        }

        foreach (string agent in new[] { "architect", "product-owner", "csharp-dev", "test-dev", "github-devops", "docs-dev", "research-agent" })
        {
            Assert.DoesNotContain(
                ArbiterPrefix,
                ContentOf(result, $".zcode/agents/{agent}.md"),
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ZCode_WithoutAnEnabledProjectWiring_RendersByteIdenticalToNoArbiterSupport(int wiringKind)
    {
        // wiringKind: 0 = no wiring, 1 = disabled wiring, 2 = enabled wiring, global scope.
        using ArbiterSquadFixture bare = ArbiterSquadFixture.Create();
        WriteCapabilities(bare);
        WriteToolchain(bare, withArbiter: false);
        SquadArbiterWiring? wiring = wiringKind switch
        {
            1 => new SquadArbiterWiring(false, 30),
            2 => new SquadArbiterWiring(true, 30),
            _ => null,
        };
        SquadDeploymentScope scope = wiringKind == 2
            ? SquadDeploymentScope.Global
            : SquadDeploymentScope.Project;
        SquadRendererRegistry registry = new([new ZCodeRenderer()]);
        SquadRenderResult baseline = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: bare.Path,
            Targets: [SquadTarget.ZCode],
            Scope: scope));
        Assert.True(baseline.Success, string.Join("; ", baseline.Errors));

        SquadRenderResult gated = await RenderAsync(new ZCodeRenderer(), SquadTarget.ZCode, wiring, scope);

        Assert.Equal(
            baseline.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray(),
            gated.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray());
        foreach (SquadDeploymentFile expected in baseline.Files)
        {
            SquadDeploymentFile actual = Assert.Single(
                gated.Files,
                file => file.RelativePath == expected.RelativePath);
            Assert.True(
                expected.Content.Span.SequenceEqual(actual.Content.Span),
                $"{expected.RelativePath} must render byte-identically without an enabled project wiring.");
        }

        Assert.Equal(
            baseline.Degradations.Select(record => record.ToString()).Order(StringComparer.Ordinal).ToArray(),
            gated.Degradations.Select(record => record.ToString()).Order(StringComparer.Ordinal).ToArray());
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

    private static void WriteCapabilities(ArbiterSquadFixture fixture)
    {
        File.WriteAllText(Path.Combine(fixture.Path, "profiles/capabilities.yml"), """
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
    }

    private static void WriteToolchain(ArbiterSquadFixture fixture, bool withArbiter)
    {
        string arbiterEntry = withArbiter
            ? "  kyber-weave-arbiter:\n    - arbiter_evaluate\n    - arbiter_rules\n    - arbiter_status\n"
            : string.Empty;
        File.WriteAllText(
            Path.Combine(fixture.Path, "toolchain.yml"),
            "schema: kyber-squad.toolchain/v1\n" +
            "required-features:\n" +
            "  - agent-ir/v1\n" +
            "required-mcp-tools:\n" +
            "  context7:\n" +
            "    - query-docs\n" +
            arbiterEntry +
            "validated-release: null\n");
    }

    private async Task<SquadRenderResult> RenderAsync(
        ISquadRenderer renderer,
        SquadTarget target,
        SquadArbiterWiring? arbiter = null,
        SquadDeploymentScope scope = SquadDeploymentScope.Project)
    {
        SquadRendererRegistry registry = new([renderer]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [target],
            Scope: scope,
            Arbiter: arbiter));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }

    private static string ContentOf(SquadRenderResult result, string relativePath)
    {
        SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == relativePath);
        return Encoding.UTF8.GetString(file.Content.Span);
    }
}
