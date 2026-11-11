using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter Kilo plugin shim: when enabled at project scope the
/// <see cref="KiloRenderer"/> emits the owned file <c>.kilo/plugin/kyber-arbiter.ts</c>
/// from <see cref="ArbiterPluginShim"/> for harness <c>kilo</c>, spawning the binary by
/// argv with Bun, writing the <c>kyber-arbiter.plugin-event/v1</c> envelope, throwing on
/// block, assigning <c>output.args</c> and appending to <c>output.output</c>; nothing
/// renders when disabled or under Global scope (Req 6.3, 22.2, 25.1).
/// </summary>
public sealed class ArbiterKiloRenderingTests : IDisposable
{
    private const string ShimFilePath = ".kilo/plugin/kyber-arbiter.ts";
    private const int HookTimeoutSeconds = 5;

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_EmitsOwnedShimFromGenerator()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadDeploymentFile shim = Assert.Single(result.Files, f => f.RelativePath == ShimFilePath);
        Assert.Equal("kilo", shim.Target);

        string content = Encoding.UTF8.GetString(shim.Content.Span);
        Assert.Equal(ArbiterPluginShim.Render("kilo"), content);
    }

    [Fact]
    public async Task RenderAsync_Shim_SpawnsBinaryByArgvWithPipes()
    {
        string content = await ShimContentAsync();

        Assert.Contains("Bun.spawn", content, StringComparison.Ordinal);
        Assert.Contains("\"kyber-weave-arbiter\"", content, StringComparison.Ordinal);
        Assert.Contains("\"hook\"", content, StringComparison.Ordinal);
        Assert.Contains("\"--harness\"", content, StringComparison.Ordinal);
        Assert.Contains("\"kilo\"", content, StringComparison.Ordinal);
        Assert.Contains("stdin", content, StringComparison.Ordinal);
        Assert.Contains("stdout", content, StringComparison.Ordinal);
        Assert.Contains("\"pipe\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_Shim_SendsBeforeAndAfterEnvelopes()
    {
        string content = await ShimContentAsync();

        Assert.Contains("kyber-arbiter.plugin-event/v1", content, StringComparison.Ordinal);
        Assert.Contains("\"before\"", content, StringComparison.Ordinal);
        Assert.Contains("\"after\"", content, StringComparison.Ordinal);
        Assert.Contains("harness", content, StringComparison.Ordinal);
        Assert.Contains("call-id", content, StringComparison.Ordinal);
        Assert.Contains("session", content, StringComparison.Ordinal);
        Assert.Contains("cwd", content, StringComparison.Ordinal);
        Assert.Contains("args", content, StringComparison.Ordinal);
        Assert.Contains("result", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_Shim_ExportsPluginWithBothHooksBlockingOnBlock()
    {
        string content = await ShimContentAsync();

        Assert.Contains("tool.execute.before", content, StringComparison.Ordinal);
        Assert.Contains("tool.execute.after", content, StringComparison.Ordinal);
        Assert.Contains("\"block\"", content, StringComparison.Ordinal);
        Assert.Contains("decision", content, StringComparison.Ordinal);
        Assert.Contains("throw", content, StringComparison.Ordinal);
        Assert.Contains("Error", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Kilo_EmitsDefaultExportObjectWithServer()
    {
        // Kilo is an OpenCode fork whose loader reads the default export as
        // { id, server }, not OpenCode's { id, setup }.
        string content = ArbiterPluginShim.Render("kilo");

        Assert.Contains("export default", content, StringComparison.Ordinal);
        Assert.Contains("id: \"kyber-arbiter\"", content, StringComparison.Ordinal);
        Assert.Contains("server: KyberArbiter", content, StringComparison.Ordinal);
        Assert.DoesNotContain("setup: KyberArbiter", content, StringComparison.Ordinal);
        Assert.Contains("export const KyberArbiter", content, StringComparison.Ordinal);

        // Contains alone passes with two defaults (SyntaxError in the host) or
        // a default ahead of the named export it references (TDZ
        // ReferenceError at import). Pin exactly one default, after it.
        int defaultExport = content.IndexOf("export default", StringComparison.Ordinal);
        Assert.True(defaultExport >= 0, "shim must default-export the plugin for the Kilo loader");
        Assert.Equal(-1, content.IndexOf("export default", defaultExport + 1, StringComparison.Ordinal));
        int namedExport = content.IndexOf("export const KyberArbiter", StringComparison.Ordinal);
        Assert.True(
            namedExport >= 0 && defaultExport > namedExport,
            "default export must follow the named export it references");
    }

    [Fact]
    public void Render_Pi_ThrowsInsteadOfEmittingAnOpenCodeShapedBody()
    {
        // Pi's Arbiter is rendered by PiRenderer.RenderArbiterExtension (Pi API,
        // pi.on tool_call/tool_result), never by this Bun/OpenCode shaped shim.
        // Default-exporting the Plugin body for "pi" would bind directory to
        // undefined under the Pi host and register no Pi hooks: fail-open, the
        // same silent-never-loads class this generator exists to fix.
        Assert.Throws<ArgumentException>(() => ArbiterPluginShim.Render("pi"));
    }

    [Fact]
    public void Render_UnknownHarnessToken_ThrowsInsteadOfGuessingALoaderShape()
    {
        Assert.Throws<ArgumentException>(() => ArbiterPluginShim.Render("warp"));
    }

    [Fact]
    public async Task RenderAsync_Shim_AssignsArgsAndAppendsOutputOnBlock()
    {
        string content = await ShimContentAsync();

        Assert.Contains("output.args", content, StringComparison.Ordinal);
        Assert.Contains("output.output", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_Shim_ThrowsWhenSpawnFails()
    {
        string content = await ShimContentAsync();

        // A shim that cannot spawn the binary must block: the spawn sits inside a
        // try that rethrows, so a spawn failure surfaces as a thrown Error.
        Assert.Contains("try", content, StringComparison.Ordinal);
        Assert.Contains("catch", content, StringComparison.Ordinal);
        Assert.Contains("throw", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_Shim_TreatsEmptyStdoutAsAllowBeforeParsing()
    {
        string content = await ShimContentAsync();

        // Exit 0 with empty stdout is the host's allow (arbiter.enabled: false, or a
        // dispatch with no target). Parsing "" would throw and wrongly block. The parse
        // is the parseDecision call, which validates the shape before returning.
        int emptyCheck = content.IndexOf("trim() === \"\"", StringComparison.Ordinal);
        int parse = content.IndexOf("parseDecision(text)", StringComparison.Ordinal);
        Assert.True(emptyCheck >= 0, "shim must test for empty stdout");
        Assert.True(parse > emptyCheck, "the empty-stdout check must precede parsing the decision");
        Assert.Contains("{ decision: \"allow\" }", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_Shim_BlocksWhenNoRepositoryRootResolves()
    {
        // A host whose cwd is not the repo leaves the shim with no repository
        // root. The shim must throw a block with a clear reason instead of
        // spawning the hook, so the failure reads as a refusal rather than a
        // crash that fails closed with KW-ARB-HOOK-001.
        string content = await ShimContentAsync();

        Assert.Contains("throw", content, StringComparison.Ordinal);
        Assert.Contains("root", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".kyber-weave", content, StringComparison.Ordinal);

        int rootGuard = content.IndexOf("root", StringComparison.OrdinalIgnoreCase);
        int spawn = content.IndexOf("Bun.spawn", StringComparison.Ordinal);
        Assert.True(
            rootGuard >= 0 && spawn > rootGuard,
            "shim must resolve the repository root before spawning the hook so an unresolvable root blocks");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("disabled")]
    public async Task RenderAsync_WhenNotRendered_EmitsNoShim(string mode)
    {
        SquadArbiterWiring? wiring = mode switch
        {
            "disabled" => new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: HookTimeoutSeconds),
            _ => null,
        };
        SquadDeploymentScope scope = SquadDeploymentScope.Project;

        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Kilo],
            Scope: scope,
            Arbiter: wiring));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Files, f => f.RelativePath == ShimFilePath);
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
        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Kilo],
            Scope: scope,
            Arbiter: arbiter));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }

    private async Task<string> ShimContentAsync()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);
        SquadDeploymentFile shim = Assert.Single(result.Files, f => f.RelativePath == ShimFilePath);
        return Encoding.UTF8.GetString(shim.Content.Span);
    }
}
