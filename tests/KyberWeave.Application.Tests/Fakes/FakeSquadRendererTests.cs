using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// The shared renderer fake answers from the configuration the test gave it and records every request and
/// answer, so no test needs a renderer double of its own.
/// </summary>
public sealed class FakeSquadRendererTests
{
    private static readonly SquadRenderRequest Request = new(
        "/source",
        [SquadTarget.Copilot, SquadTarget.Claude],
        SquadDeploymentScope.Project);

    [Fact]
    public async Task TheUnconfiguredFakeStillRendersTheCanonicalCatalog()
    {
        FakeSquadRenderer renderer = new();

        SquadRenderResult result = await renderer.RenderAsync(Request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(result.Files, file => file.RelativePath == ".github/agents/conductor.agent.md");
        Assert.Equal(SquadTargetCatalog.All, renderer.SupportedTargets);
        Assert.Equal(1, renderer.RenderCount);
    }

    [Fact]
    public async Task ScriptedAgentsRendersOneExactFilePerAgentPerTarget()
    {
        FakeSquadRenderer renderer = FakeSquadRenderer.ScriptedAgents("architect", "conductor");

        SquadRenderResult result = await renderer.RenderAsync(Request);

        Assert.True(result.Success);
        Assert.Equal(4, result.Files.Count);
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            candidate => candidate.RelativePath == "agents/architect.md" && candidate.Target == "copilot");
        Assert.Equal("canonical architect\n", Encoding.UTF8.GetString(file.Content.Span));
        Assert.Equal(
            ["agents/architect.md", "agents/conductor.md", "agents/architect.md", "agents/conductor.md"],
            result.Files.Select(static candidate => candidate.RelativePath));
    }

    [Fact]
    public async Task EmptySucceedsWithNoFilesAndKeepsTheTargetsItWasGiven()
    {
        FakeSquadRenderer renderer = FakeSquadRenderer.Empty(SquadTarget.Claude);

        SquadRenderResult result = await renderer.RenderAsync(Request);

        Assert.True(result.Success);
        Assert.Empty(result.Files);
        Assert.Equal([SquadTarget.Claude], renderer.SupportedTargets);
    }

    [Fact]
    public async Task FailingReportsFailureWithTheErrorsItWasGiven()
    {
        FakeSquadRenderer renderer = FakeSquadRenderer.Failing("The render failed for the test.");

        SquadRenderResult result = await renderer.RenderAsync(Request);

        Assert.False(result.Success);
        Assert.Equal(["The render failed for the test."], result.Errors);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task WithBlocksReturnsTheBlocksAndNoFiles()
    {
        SquadRenderedBlock block = new("factory", ".factory/hooks.json", SquadHookBlockFormat.Factory, []);
        FakeSquadRenderer renderer = FakeSquadRenderer.WithBlocks(SquadTarget.Factory, block);

        SquadRenderResult result = await renderer.RenderAsync(Request);

        Assert.True(result.Success);
        Assert.Empty(result.Files);
        Assert.Equal([block], result.Blocks);
        Assert.Equal([SquadTarget.Factory], renderer.SupportedTargets);
    }

    [Fact]
    public async Task EmittingReturnsTheFilesUnchanged()
    {
        SquadDeploymentFile file = new(
            ".github/agents/shared.agent.md",
            Encoding.UTF8.GetBytes("shared projection"),
            "copilot");
        FakeSquadRenderer renderer = FakeSquadRenderer.Emitting([file], SquadTarget.Copilot);

        SquadRenderResult result = await renderer.RenderAsync(Request);

        Assert.Equal([file], result.Files);
        Assert.Equal([SquadTarget.Copilot], renderer.SupportedTargets);
    }

    [Fact]
    public async Task WrappingDelegatesItsAnswerAndRecordsTheWarningsItCarried()
    {
        SquadRenderWarning warning = new("agent-body-too-long", "The conductor body is too long.", "copilot");
        FakeSquadRenderer inner = FakeSquadRenderer.Responding(
            _ => new SquadRenderResult(true, [], [], [warning], []),
            [SquadTarget.Copilot]);
        FakeSquadRenderer renderer = FakeSquadRenderer.Wrapping(inner);

        SquadRenderResult result = await renderer.RenderAsync(Request);

        Assert.NotSame(inner, renderer);
        Assert.Equal([SquadTarget.Copilot], renderer.SupportedTargets);
        Assert.Equal(["agent-body-too-long"], renderer.WarningCodes);
        Assert.Same(warning, Assert.Single(Assert.Single(renderer.Results).Warnings));
        Assert.Same(result, renderer.Results[0]);
        Assert.Single(inner.RenderRequests);
    }

    [Fact]
    public async Task RespondingSeesTheRequestAndItsAnswerIsRecorded()
    {
        FakeSquadRenderer renderer = FakeSquadRenderer.Responding(
            request => new SquadRenderResult(
                true,
                [new SquadDeploymentFile(
                    "agents/architect.md",
                    Encoding.UTF8.GetBytes($"targets: {request.Targets.Count}\n"),
                    "claude")],
                [],
                [],
                []),
            [SquadTarget.Claude]);

        SquadRenderResult result = await renderer.RenderAsync(Request);

        Assert.Equal("targets: 2\n", Encoding.UTF8.GetString(result.Files[0].Content.Span));
        Assert.Same(Request, renderer.RenderRequests[0]);
        Assert.Equal([SquadTarget.Claude], renderer.SupportedTargets);
    }

    [Fact]
    public async Task AFaultIsRaisedInsteadOfAnsweringAndNoAnswerIsRecorded()
    {
        InvalidOperationException fault = new("the renderer crashed");
        FakeSquadRenderer renderer = new() { Fault = fault };

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => renderer.RenderAsync(Request));

        Assert.Same(fault, thrown);
        Assert.Equal(1, renderer.RenderCount);
        Assert.Empty(renderer.Results);
    }

    [Fact]
    public async Task ACancelledRequestIsRefusedBeforeItIsRecorded()
    {
        FakeSquadRenderer renderer = FakeSquadRenderer.Empty();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => renderer.RenderAsync(Request, cancellation.Token));

        Assert.Empty(renderer.RenderRequests);
    }
}
