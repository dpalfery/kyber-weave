using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Where the Arbiter's hooks are rendered or reported by scope: the Cursor and Claude global files, the Copilot CLI hook
/// file with no wired agent, and the enforcement class a Copilot global install reports. Regression tests for Kilo review
/// comments 1 through 4 on PR #527.
/// </summary>
public sealed class ArbiterHookRenderScopeTests : IDisposable
{
    private readonly InMemoryFileSystem _files = new();
    private readonly ArbiterSquadFixture _fixture;

    public ArbiterHookRenderScopeTests()
    {
        _fixture = ArbiterSquadFixture.CreateInMemory(_files);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Item1_CursorRenderer_GlobalScope_EmitsHooksJsonDirectlyUnderGlobalRoot()
    {
        SquadRendererRegistry registry = _fixture.Registry([new CursorRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Global,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)).Prepared(_fixture));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.Equal("cursor", block.Target);
        Assert.Equal("hooks.json", block.RelativePath);
    }

    [Fact]
    public void Item2_ArbiterHookWiring_GlobalScope_CopilotReportsEnforcedGlobal()
    {
        IReadOnlyList<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations(
            [SquadTarget.Copilot],
            SquadDeploymentScope.Global);

        SquadDegradationRecord record = Assert.Single(records);
        Assert.Equal("copilot", record.Target);
        Assert.Equal("enforced-global", record.Details);

        string? hypotheticalReason = ArbiterHookWiring.DegradationReason(
            "copilot-vscode",
            SquadDeploymentScope.Global,
            ArbiterHookWiring.HookedTargets,
            ArbiterHookWiring.FallbackTargets);
        Assert.Equal("hooks-disabled-by-harness", hypotheticalReason);
    }

    [Fact]
    public async Task Item3_CopilotRenderer_ProjectScope_OmitsCliHooksWhenNoAgentsWired()
    {
        string agentsDir = Path.Combine(_fixture.Path, "agents");
        foreach (string file in _files.EnumerateFiles(agentsDir, "*.md", recursive: false))
        {
            if (Path.GetFileName(file) != "docs-dev.md")
            {
                _files.DeleteFile(file);
            }
        }

        _files.WriteAllText(Path.Combine(_fixture.Path, "bundles", "full.yml"), """
            schema: kyber-squad.bundle/v1
            name: full
            agents:
              - docs-dev
            skills: []
            """);

        SquadRendererRegistry registry = _fixture.Registry([new CopilotRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)).Prepared(_fixture));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Files, f => f.RelativePath == ".github/hooks/kyber-arbiter.json");
    }

    [Fact]
    public async Task Item4_ClaudeRenderer_GlobalScope_OmitsFrontmatterHooksOnAgents()
    {
        SquadRendererRegistry registry = _fixture.Registry([new ClaudeRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Global,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)).Prepared(_fixture));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        foreach (SquadDeploymentFile file in result.Files.Where(f => f.RelativePath.EndsWith(".md", StringComparison.Ordinal)))
        {
            string text = Encoding.UTF8.GetString(file.Content.Span);

            // The architect is the one agent that keeps a frontmatter hook: its shell-guard entry (KW-ARB-SHELL-001),
            // which is the only hook it carries at global scope. Every other agent file stays hook-free.
            if (file.RelativePath == "agents/architect.md")
            {
                Assert.Contains("hooks:", text, StringComparison.Ordinal);
                Assert.Contains("^(Bash|PowerShell)$", text, StringComparison.Ordinal);
                Assert.DoesNotContain("^(Agent|Task)$", text, StringComparison.Ordinal);
                continue;
            }

            Assert.DoesNotContain("hooks:", text, StringComparison.Ordinal);
        }

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.Equal("claude", block.Target);
        Assert.Equal("settings.json", block.RelativePath);
    }
}
