using System.Text;
using KyberWeave.Tests.Fakes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the global-scope Arbiter install for the plugin-folder harnesses: they write owned files
/// only under the harness's real global root, the generated shim blocks on the plugin hook, and the
/// generated command line carries <c>--scope global</c>. OpenCode is degraded with a recorded
/// <c>dispatch-unconfirmed</c> reason; Pi keeps the <c>partial</c> tier, and the Kilo path notes that
/// <c>KILO_PURE=1</c> skips plugin loading. Command Code writes its mod into
/// <c>~/.commandcode/mods/</c>, which loads without a trust prompt.
/// </summary>
public sealed class ArbiterGlobalPluginDirRenderingTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly ArbiterSquadFixture _fixture;

    public ArbiterGlobalPluginDirRenderingTests()
    {
        _fixture = ArbiterSquadFixture.CreateInMemory(_files);
    }

    [Fact]
    public async Task RenderAsync_GlobalScope_OpenCode_EmitsOwnedAutoLoadedPluginFileUnderGlobalRoot()
    {
        string content = await RenderGlobalPluginAsync(
            new OpenCodeRenderer(),
            SquadTarget.OpenCode,
            root => Path.Combine(root, ".config", "opencode"),
            "plugins/kyber-arbiter.ts");

        Assert.Contains("ctx.tool.hook(\"execute.before\"", content, StringComparison.Ordinal);
        Assert.Contains("ctx.location?.directory", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", content, StringComparison.Ordinal);
        Assert.Contains("--scope global", content, StringComparison.Ordinal);

        // The plugin file lives under the harness's user-level directory, so walking up
        // from it would pick ~/.kyber-weave/kyber-weave.yml over the repository.
        Assert.DoesNotContain("currentFileDirectory", content, StringComparison.Ordinal);
        Assert.Contains("findNearestRepositoryRoot", content, StringComparison.Ordinal);
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
        Assert.Contains("server: KyberArbiter", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", content, StringComparison.Ordinal);
        Assert.Contains("--scope global", content, StringComparison.Ordinal);

        // The global root rule applies to Kilo too, even though its body stays 1.x.
        Assert.DoesNotContain("currentFileDirectory", content, StringComparison.Ordinal);
        Assert.Contains("findNearestRepositoryRoot", content, StringComparison.Ordinal);
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

    [Fact]
    public async Task RenderAsync_GlobalScope_CommandCode_EmitsOwnedAutoLoadedModFileUnderGlobalRoot()
    {
        string content = await RenderGlobalPluginAsync(
            new CommandCodeRenderer(),
            SquadTarget.CommandCode,
            root => Path.Combine(root, ".commandcode"),
            "mods/kyber-arbiter.ts");

        Assert.Contains("export default function", content, StringComparison.Ordinal);
        Assert.Contains("cmd.hooks({", content, StringComparison.Ordinal);
        Assert.Contains("node:child_process", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", content, StringComparison.Ordinal);
        // The mod spawns through an argv array with no shell, so the global flag lands as
        // two array elements rather than the "--scope global" command-line form the
        // plugin shims carry.
        Assert.Contains("\", \"--scope\", \"global\"", content, StringComparison.Ordinal);

        // A user-scope mod loads without a trust prompt, so the global install enforces the
        // same gate the project mod does.
        Assert.Equal("enforced-global", Assert.Single(
            ArbiterHookWiring.TargetDegradations([SquadTarget.CommandCode], SquadDeploymentScope.Global)).Details);
    }

    private async Task<string> RenderGlobalPluginAsync(
        SquadHarnessRenderer renderer,
        SquadTarget target,
        Func<string, string> globalRootFactory,
        string relativePath)
    {
        string home = ArbiterTestRoots.Under("home");
        _files.CreateDirectory(home);

        SquadRenderResult result = await _fixture.Registry([renderer]).RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [target],
            Scope: SquadDeploymentScope.Global,
            GlobalRoots: new FixedGlobalRoots(home, globalRootFactory),
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 10)).Prepared(_fixture));

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
