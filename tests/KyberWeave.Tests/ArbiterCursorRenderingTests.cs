using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter Cursor owned block: when enabled at project scope the
/// <see cref="CursorRenderer"/> returns a <c>cursor</c> <see cref="SquadRenderedBlock"/>
/// for <c>.cursor/hooks.json</c> holding <c>preToolUse</c> and <c>postToolUse</c> entries
/// <c>{command: "kyber-weave-arbiter hook --harness cursor", matcher: "Task", timeout,
/// failClosed: true}</c> [F6]; nothing renders when disabled or under Global scope
/// (Req 5.3, 6.2, 8.1).
/// </summary>
public sealed class ArbiterCursorRenderingTests : IDisposable
{
    private const string BlockPath = ".cursor/hooks.json";
    private const string ExpectedCommand = "kyber-weave-arbiter hook --harness cursor";
    private const int HookTimeoutSeconds = 5;

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_ReturnsCursorBlock()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.Equal("cursor", block.Target);
        Assert.Equal(BlockPath, block.RelativePath);
        Assert.Equal(SquadHookBlockFormat.Cursor, block.Format);
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_HoldsPreAndPostTaskEntries()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);

        Assert.Equal(2, block.Entries.Count);
        SquadRenderedBlockEntry pre = Assert.Single(block.Entries, e => e.Container == "preToolUse");
        SquadRenderedBlockEntry post = Assert.Single(block.Entries, e => e.Container == "postToolUse");

        foreach (SquadRenderedBlockEntry entry in new[] { pre, post })
        {
            JsonObject payload = Assert.IsType<JsonObject>(entry.Entry);
            Assert.Equal(ExpectedCommand, payload["command"]?.GetValue<string>());
            Assert.Equal("Task", payload["matcher"]?.GetValue<string>());
            Assert.Equal(HookTimeoutSeconds, payload["timeout"]?.GetValue<int>());
            Assert.True(payload["failClosed"]?.GetValue<bool>());
        }
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_EntriesCarryTheWiringTimeout()
    {
        const int timeoutSeconds = 17;
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.All(block.Entries, entry =>
            Assert.Equal(timeoutSeconds, Assert.IsType<JsonObject>(entry.Entry)["timeout"]?.GetValue<int>()));
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_EntriesAreRecognizedAsSquadOwned()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.All(block.Entries, entry =>
            Assert.True(
                SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Cursor, entry.Entry),
                $"Entry in container '{entry.Container}' is not recognized as Squad-owned."));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("disabled")]
    [InlineData("global")]
    public async Task RenderAsync_WhenNotRendered_EmitsNoBlock(string mode)
    {
        SquadArbiterWiring? wiring = mode switch
        {
            "disabled" => new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: HookTimeoutSeconds),
            "global" => new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            _ => null,
        };
        SquadDeploymentScope scope = string.Equals(mode, "global", StringComparison.Ordinal)
            ? SquadDeploymentScope.Global
            : SquadDeploymentScope.Project;

        SquadRenderResult result = await RenderAsync(wiring, scope);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(result.Blocks is null || result.Blocks.Count == 0, "Expected no rendered blocks.");
    }

    [Fact]
    public async Task RenderAsync_DisabledArbiter_RendersByteForByteAsOmittingTheWiring()
    {
        SquadRenderResult omitted = await RenderAsync(null, SquadDeploymentScope.Project);
        SquadRenderResult disabled = await RenderAsync(
            new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        Assert.Equal(
            omitted.Files.Select(f => (f.Target, f.RelativePath)).Order().ToList(),
            disabled.Files.Select(f => (f.Target, f.RelativePath)).Order().ToList());
        foreach ((SquadDeploymentFile expected, SquadDeploymentFile actual) in omitted.Files
            .OrderBy(f => f.Target, StringComparer.Ordinal)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .Zip(disabled.Files
                .OrderBy(f => f.Target, StringComparer.Ordinal)
                .ThenBy(f => f.RelativePath, StringComparer.Ordinal)))
        {
            Assert.True(
                expected.Content.Span.SequenceEqual(actual.Content.Span),
                $"Rendered bytes differ for {actual.Target}/{actual.RelativePath} when the Arbiter wiring is disabled.");
        }
    }

    [Fact]
    public async Task RenderAsync_EnabledBlock_LeavesOwnedFilesUndisturbed()
    {
        SquadRenderResult without = await RenderAsync(null, SquadDeploymentScope.Project);
        SquadRenderResult with = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        Assert.Equal(
            without.Files.Select(f => (f.Target, f.RelativePath)).Order().ToList(),
            with.Files.Select(f => (f.Target, f.RelativePath)).Order().ToList());
        foreach ((SquadDeploymentFile expected, SquadDeploymentFile actual) in without.Files
            .OrderBy(f => f.Target, StringComparer.Ordinal)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .Zip(with.Files
                .OrderBy(f => f.Target, StringComparer.Ordinal)
                .ThenBy(f => f.RelativePath, StringComparer.Ordinal)))
        {
            Assert.True(
                expected.Content.Span.SequenceEqual(actual.Content.Span),
                $"Owned file {actual.Target}/{actual.RelativePath} changed when the Arbiter block was added.");
        }
    }

    private async Task<SquadRenderResult> RenderAsync(SquadArbiterWiring? arbiter, SquadDeploymentScope scope)
    {
        SquadRendererRegistry registry = new([new CursorRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Cursor],
            Scope: scope,
            Arbiter: arbiter));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }
}
