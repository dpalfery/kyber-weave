using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the global-scope Arbiter install for the plugin-folder harnesses: they write owned files
/// only under the harness's real global root, the generated shim blocks on the plugin hook, and the
/// generated command line carries <c>--scope global</c>. OpenCode is degraded with a recorded
/// <c>dispatch-unconfirmed</c> reason; Pi keeps the <c>partial</c> tier, and the Kilo path notes that
/// <c>KILO_PURE=1</c> skips plugin loading.
/// </summary>
public sealed class ArbiterGlobalPluginDirRenderingTests : IDisposable
{
    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _fixture.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task RenderAsync_GlobalScope_OpenCode_EmitsOwnedAutoLoadedPluginFileUnderGlobalRoot()
    {
        string content = await RenderGlobalPluginAsync(
            new OpenCodeRenderer(),
            SquadTarget.OpenCode,
            root => Path.Combine(root, ".config", "opencode"),
            "plugins/kyber-arbiter.ts");

        Assert.Contains("tool.execute.before", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", content, StringComparison.Ordinal);
        Assert.Contains("--scope global", content, StringComparison.Ordinal);
        Assert.Equal("dispatch-unconfirmed", Assert.Single(
            ArbiterHookWiring.TargetDegradations([SquadTarget.OpenCode], SquadDeploymentScope.Global)).Details);
    }

    [Fact]
    public async Task RenderAsync_GlobalScope_Kilo_EmitsOwnedAutoRegisteredPluginFileUnderGlobalRoot()
    {
        string content = await RenderGlobalPluginAsync(
            new KiloRenderer(),
            SquadTarget.Kilo,
            root => Path.Combine(root, ".config", "kilo"),
            "plugin/kyber-arbiter.ts");

        Assert.Contains("tool.execute.before", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", content, StringComparison.Ordinal);
        Assert.Contains("--scope global", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_GlobalScope_Pi_EmitsOwnedExtensionFileUnderGlobalRootAndKeepsTierPartial()
    {
        string content = await RenderGlobalPluginAsync(
            new PiRenderer(),
            SquadTarget.Pi,
            root => Path.Combine(root, ".pi", "agent"),
            "extensions/kyber-arbiter.ts");

        Assert.Contains("export default function", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", content, StringComparison.Ordinal);
        Assert.Contains("--scope global", content, StringComparison.Ordinal);
        Assert.Equal("partial", Assert.Single(
            ArbiterHookWiring.TargetDegradations([SquadTarget.Pi], SquadDeploymentScope.Global)).Details);
    }

    private async Task<string> RenderGlobalPluginAsync(
        SquadHarnessRenderer renderer,
        SquadTarget target,
        Func<string, string> globalRootFactory,
        string relativePath)
    {
        string home = Path.Combine(_temp.Path, "home");
        Directory.CreateDirectory(home);

        SquadRenderResult result = await new SquadRendererRegistry([renderer]).RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [target],
            Scope: SquadDeploymentScope.Global,
            GlobalRoots: new FixedGlobalRoots(home, globalRootFactory),
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 10)));

        Assert.True(result.Success, string.Join(';', result.Errors));

        SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == relativePath);
        Assert.Equal(SquadTargetCatalog.GetToken(target), file.Target);
        return Encoding.UTF8.GetString(file.Content.Span);
    }

    private sealed class FixedGlobalRoots(string home, Func<string, string> rootFactory) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) => rootFactory(home);
    }
}
