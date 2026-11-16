using System.Text;
using KyberWeave.Tests.Fakes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the global-scope Arbiter contract: the request is enabled at Global scope, hook command
/// lines carry the global scope flag, and global-scope target degradation reasons switch from the
/// old blanket <c>global-scope</c> record to the tiered reason codes that distinguish enforced,
/// degraded, partial and fallback-only targets.
/// </summary>
public sealed class ArbiterGlobalWiringTests : IDisposable
{
    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void EnabledArbiter_GlobalScope_WithEnabledRequest_IsNotNull()
    {
        SquadRenderRequest request = new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Global,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 30)).Prepared();

        SquadHarnessRenderContext context = new(
            new SquadHarnessSpec(
                SquadTarget.Claude,
                "Claude",
                ".claude",
                "agents",
                SquadModelPolicy.NotRendered),
            request,
            null!);

        Assert.NotNull(context.EnabledArbiter);
        Assert.Equal(30, context.EnabledArbiter!.HookTimeoutSeconds);
    }

    [Fact]
    public async Task RenderAsync_EnabledArbiterUnderGlobalScope_RendersGlobalScopeFlagInHookCommandLines()
    {
        SquadRendererRegistry registry = SquadTestRegistry.Create([new CopilotRenderer()]);
        SquadRenderRequest projectRequest = new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project,
            GlobalRoots: new FixedGlobalRoots(_fixture.Path),
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)).Prepared();
        SquadRenderRequest globalRequest = new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Global,
            GlobalRoots: new FixedGlobalRoots(_fixture.Path),
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)).Prepared();

        SquadRenderResult project = await registry.RenderAsync(projectRequest);
        SquadRenderResult global = await registry.RenderAsync(globalRequest);

        string projectText = string.Join("\n", project.Files.Select(file => Encoding.UTF8.GetString(file.Content.Span)));
        string globalText = string.Join("\n", global.Files.Select(file => Encoding.UTF8.GetString(file.Content.Span)));

        Assert.DoesNotContain("--scope global", projectText, StringComparison.Ordinal);
        Assert.Contains("--scope global", globalText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SquadTarget.Claude, "enforced-global")]
    [InlineData(SquadTarget.Codex, "enforced-global")]
    [InlineData(SquadTarget.Copilot, "enforced-global")]
    [InlineData(SquadTarget.OpenCode, "dispatch-unconfirmed")]
    [InlineData(SquadTarget.Pi, "partial")]
    [InlineData(SquadTarget.Warp, "fallback-only")]
    public void TargetDegradations_GlobalScope_UsesTieredReasonCodes(SquadTarget target, string expectedReason)
    {
        SquadDegradationRecord record = Assert.Single(
            ArbiterHookWiring.TargetDegradations([target], SquadDeploymentScope.Global));

        Assert.Equal("arbiter-not-enforced", record.Code);
        Assert.Equal(expectedReason, record.Details);
    }

    [Fact]
    public async Task RenderAsync_DisabledArbiterUnderGlobalScope_RendersByteForByteAsOmittingTheWiring()
    {
        SquadRendererRegistry registry = SquadTestRegistry.Create([new CopilotRenderer()]);

        SquadRenderResult omitted = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Global,
            GlobalRoots: new FixedGlobalRoots(_fixture.Path),
            Arbiter: null).Prepared());

        SquadRenderResult disabled = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Global,
            GlobalRoots: new FixedGlobalRoots(_fixture.Path),
            Arbiter: new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: 5)).Prepared());

        Assert.Equal(
            omitted.Files.Select(file => (file.Target, file.RelativePath)).OrderBy(pair => pair.Target, StringComparer.Ordinal)
                .ThenBy(pair => pair.RelativePath, StringComparer.Ordinal).ToList(),
            disabled.Files.Select(file => (file.Target, file.RelativePath)).OrderBy(pair => pair.Target, StringComparer.Ordinal)
                .ThenBy(pair => pair.RelativePath, StringComparer.Ordinal).ToList());

        foreach ((SquadDeploymentFile expected, SquadDeploymentFile actual) in omitted.Files
            .OrderBy(file => file.Target, StringComparer.Ordinal)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .Zip(disabled.Files
                .OrderBy(file => file.Target, StringComparer.Ordinal)
                .ThenBy(file => file.RelativePath, StringComparer.Ordinal)))
        {
            Assert.True(
                expected.Content.Span.SequenceEqual(actual.Content.Span),
                $"Global-scope render differs for {actual.Target}/{actual.RelativePath} when Arbiter is disabled.");
        }
    }

    private sealed class FixedGlobalRoots(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) => Path.Combine(root, SquadTargetCatalog.GetToken(target));
    }
}
