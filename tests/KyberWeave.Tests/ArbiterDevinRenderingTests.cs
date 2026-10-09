using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter Devin owned block: when enabled at project scope the
/// <see cref="DevinRenderer"/> returns a <c>devin</c> <see cref="SquadRenderedBlock"/>
/// for <c>.devin/hooks.v1.json</c> holding <c>PreToolUse</c> and <c>PostToolUse</c>
/// matcher groups with the documented <c>run_subagent</c> matcher and one command hook
/// carrying a timeout ([F12]); it records <c>arbiter-not-enforced</c> with
/// <c>no-post-dispatch-feedback</c> because Devin documents no PostToolUse output field,
/// so post-dispatch outcomes are never delivered back; and nothing renders when disabled
/// or under Global scope (Req 6.3, 8.1, 8.2, 22.2).
/// </summary>
public sealed class ArbiterDevinRenderingTests : IDisposable
{
    private const string BlockPath = ".devin/hooks.v1.json";
    private const string Matcher = "^run_subagent$";
    private const string Command = "kyber-weave-arbiter hook --harness devin";
    private const int HookTimeoutSeconds = 5;

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_ReturnsDevinBlock()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.Equal("devin", block.Target);
        Assert.Equal(BlockPath, block.RelativePath);
        Assert.Equal(SquadHookBlockFormat.Devin, block.Format);

        Assert.Equal(
            ["PreToolUse", "PostToolUse"],
            block.Entries.Select(entry => entry.Container).ToArray());

        foreach (SquadRenderedBlockEntry entry in block.Entries)
        {
            JsonObject group = Assert.IsType<JsonObject>(entry.Entry);
            Assert.Equal(Matcher, group["matcher"]!.GetValue<string>());

            JsonArray hooks = Assert.IsType<JsonArray>(group["hooks"]);
            JsonObject hook = Assert.IsType<JsonObject>(Assert.Single(hooks));
            Assert.Equal("command", hook["type"]!.GetValue<string>());
            Assert.Equal(Command, hook["command"]!.GetValue<string>());
            Assert.Equal(HookTimeoutSeconds, hook["timeout"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_EmitsNoWholeFile()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        Assert.NotNull(result.Blocks);
        Assert.NotEmpty(result.Blocks);
        Assert.DoesNotContain(result.Files, f => f.RelativePath == BlockPath);
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
                SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Devin, entry.Entry),
                $"Entry in container '{entry.Container}' is not recognized as Squad-owned."));
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_EntriesCarryTheWiringTimeout()
    {
        const int timeoutSeconds = 17;
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.All(block.Entries, entry => Assert.Equal(
            timeoutSeconds,
            Assert.IsType<JsonObject>(entry.Entry)["hooks"]![0]!["timeout"]!.GetValue<int>()));
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_RecordsNoPostDispatchFeedbackDegradation()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        // The record is agent-scoped because the registry requires a renderer-emitted
        // degradation to reference a known agent: one per dispatcher, whose dispatches are
        // what loses post-dispatch feedback when PostToolUse has no output field.
        string[] expectedDispatchers =
        [
            ArbiterSquadFixture.Architect,
            ArbiterSquadFixture.CodeReviewer,
            ArbiterSquadFixture.Conductor,
            ArbiterSquadFixture.ProductOwner,
        ];
        SquadDegradationRecord[] records = result.Degradations
            .Where(d => d.Code == "arbiter-not-enforced")
            .ToArray();

        Assert.Equal(
            expectedDispatchers.Order(StringComparer.Ordinal).ToArray(),
            records.Select(d => d.CanonicalIdentity).Order(StringComparer.Ordinal).ToArray());
        foreach (SquadDegradationRecord record in records)
        {
            Assert.Equal("devin", record.Target);
            Assert.Equal(record.CanonicalIdentity, record.OutputIdentity);
            Assert.False(string.IsNullOrEmpty(record.InstructionDigest));
            Assert.Equal("no-post-dispatch-feedback", record.Details);
        }
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

        Assert.True(result.Blocks is null || result.Blocks.Count == 0, "Expected no owned blocks when the Arbiter block is not rendered.");
        Assert.DoesNotContain(result.Files, f => f.RelativePath == BlockPath);
        Assert.DoesNotContain(result.Degradations, d => d.Code == "arbiter-not-enforced");
    }

    [Fact]
    public async Task RenderAsync_EnabledArbiter_LeavesFilesByteForByte()
    {
        SquadRenderResult omitted = await RenderAsync(null, SquadDeploymentScope.Project);
        SquadRenderResult enabled = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        Assert.Equal(
            omitted.Files.Select(f => (f.Target, f.RelativePath)).Order().ToList(),
            enabled.Files.Select(f => (f.Target, f.RelativePath)).Order().ToList());
        foreach ((SquadDeploymentFile expected, SquadDeploymentFile actual) in omitted.Files
            .OrderBy(f => f.Target, StringComparer.Ordinal)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .Zip(enabled.Files
                .OrderBy(f => f.Target, StringComparer.Ordinal)
                .ThenBy(f => f.RelativePath, StringComparer.Ordinal)))
        {
            Assert.True(
                expected.Content.Span.SequenceEqual(actual.Content.Span),
                $"Rendered bytes differ for {actual.Target}/{actual.RelativePath} when the Arbiter block is added.");
        }
    }

    private async Task<SquadRenderResult> RenderAsync(SquadArbiterWiring? arbiter, SquadDeploymentScope scope)
    {
        SquadRendererRegistry registry = new([new DevinRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Devin],
            Scope: scope,
            Arbiter: arbiter));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }
}
