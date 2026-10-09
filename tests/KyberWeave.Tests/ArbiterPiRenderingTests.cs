using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter Pi extension: when enabled at project scope the
/// <see cref="PiRenderer"/> emits the owned file
/// <c>.pi/extensions/kyber-arbiter.ts</c>, default-exporting a function that
/// bridges Pi tool calls to the binary via <c>node:child_process</c>
/// <c>spawnSync</c>; nothing renders when disabled or under Global scope
/// (Req 6.2, 22.2, 25.1).
/// </summary>
public sealed class ArbiterPiRenderingTests : IDisposable
{
    private const string ExtensionFilePath = ".pi/extensions/kyber-arbiter.ts";
    private const int HookTimeoutSeconds = 5;

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_EmitsOwnedExtension()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadDeploymentFile extension = Assert.Single(result.Files, f => f.RelativePath == ExtensionFilePath);
        Assert.Equal("pi", extension.Target);
    }

    [Fact]
    public async Task RenderAsync_Extension_DefaultExportsFunctionWithNoPackageImport()
    {
        string content = await ExtensionContentAsync();

        Assert.Contains("export default", content, StringComparison.Ordinal);
        Assert.Contains("function", content, StringComparison.Ordinal);
        Assert.Contains("node:child_process", content, StringComparison.Ordinal);
        Assert.Contains("spawnSync", content, StringComparison.Ordinal);
        Assert.DoesNotContain("@opencode-ai/plugin", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Bun.spawn", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_Extension_HandlesAgentToolCallWithBinaryByArgv()
    {
        string content = await ExtensionContentAsync();

        Assert.Contains("pi.on", content, StringComparison.Ordinal);
        Assert.Contains("tool_call", content, StringComparison.Ordinal);
        Assert.Contains("toolName", content, StringComparison.Ordinal);
        Assert.Contains("\"Agent\"", content, StringComparison.Ordinal);
        Assert.Contains("\"kyber-weave-arbiter\"", content, StringComparison.Ordinal);
        Assert.Contains("\"hook\"", content, StringComparison.Ordinal);
        Assert.Contains("\"--harness\"", content, StringComparison.Ordinal);
        Assert.Contains("\"pi\"", content, StringComparison.Ordinal);
        Assert.Contains("input", content, StringComparison.Ordinal);
        Assert.Contains("timeout", content, StringComparison.Ordinal);
        Assert.Contains("kyber-arbiter.plugin-event/v1", content, StringComparison.Ordinal);
        Assert.Contains("\"before\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_Extension_BlocksAndAssignsArgsBeforeCall()
    {
        string content = await ExtensionContentAsync();

        Assert.Contains("block", content, StringComparison.Ordinal);
        Assert.Contains("reason", content, StringComparison.Ordinal);
        Assert.Contains("event.input", content, StringComparison.Ordinal);
        Assert.Contains("decision", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_Extension_BlocksOnSpawnFailureOrNonZeroStatus()
    {
        string content = await ExtensionContentAsync();

        Assert.Contains("error", content, StringComparison.Ordinal);
        Assert.Contains("status", content, StringComparison.Ordinal);
        Assert.Contains("block", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_Extension_AppendsReasonAfterCall()
    {
        string content = await ExtensionContentAsync();

        Assert.Contains("tool_result", content, StringComparison.Ordinal);
        Assert.Contains("\"after\"", content, StringComparison.Ordinal);
        Assert.Contains("...event.content", content, StringComparison.Ordinal);
        Assert.Contains("content", content, StringComparison.Ordinal);
        Assert.Contains("\"text\"", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("disabled")]
    [InlineData("global")]
    public async Task RenderAsync_WhenNotRendered_EmitsNoExtension(string mode)
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

        SquadRendererRegistry registry = new([new PiRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Pi],
            Scope: scope,
            Arbiter: wiring));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Files, f => f.RelativePath == ExtensionFilePath);
        Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith("kyber-arbiter.ts", StringComparison.Ordinal));
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

    private async Task<SquadRenderResult> RenderAsync(SquadArbiterWiring? arbiter, SquadDeploymentScope scope)
    {
        SquadRendererRegistry registry = new([new PiRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Pi],
            Scope: scope,
            Arbiter: arbiter));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }

    private async Task<string> ExtensionContentAsync()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);
        SquadDeploymentFile extension = Assert.Single(result.Files, f => f.RelativePath == ExtensionFilePath);
        return Encoding.UTF8.GetString(extension.Content.Span);
    }
}
