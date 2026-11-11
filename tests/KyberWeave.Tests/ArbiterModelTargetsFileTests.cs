using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for task 1.13: the Arbiter model-targets table a Squad render writes beside the hooks.
/// With the Arbiter enabled, project scope writes <c>artifacts/arbiter/model-targets.&lt;harness&gt;.json</c>
/// per hooked target, global scope writes it under the user's home, and instance scope writes none.
/// <c>profiles</c> equals the resolved token per bundled profile, <c>agents</c> records each canonical
/// agent's profile and the token it was rendered with, and the file round-trips through
/// <see cref="ArbiterModelTargets.TryRead"/>. With the Arbiter disabled the render is byte-identical.
/// RED: no model-targets table is emitted yet, so every table assertion fails.
/// </summary>
public sealed class ArbiterModelTargetsFileTests : IDisposable
{
    private const string ProjectTableFile = "artifacts/arbiter/model-targets.claude.json";
    private const string ProjectOpenCodeTableFile = "artifacts/arbiter/model-targets.opencode.json";
    private const string GlobalTableSuffix = ".kyber-weave/arbiter/model-targets.claude.json";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string ProjectRoot => Path.Combine(_temp.Path, "project");

    private string Home => Path.Combine(_temp.Path, "home");

    private static SquadSource Canonical => TestSquadCorpus.CanonicalSource;

    private static string CorpusRoot => Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static SquadRendererRegistry Registry() =>
        new([new ClaudeRenderer(), new OpenCodeRenderer()]);

    private static SquadModelPreset CreatePreset() => new(
        "work",
        "work.yml",
        new string('a', 64),
        new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
        {
            ["general"] = new SquadModelProfile(
                "gpt-4o-mini",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "claude-custom-model" }),
        });

    private static Task<SquadRenderResult> RenderAsync(
        SquadDeploymentScope scope,
        bool arbiterEnabled,
        SquadModelPreset? preset = null,
        string? userScopeDirectory = null)
    {
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? presets = preset is null
            ? null
            : new Dictionary<SquadTarget, SquadModelPreset> { [SquadTarget.Claude] = preset };
        SquadRenderRequest request = new(
            SourceDirectory: CorpusRoot,
            Targets: [SquadTarget.Claude, SquadTarget.OpenCode],
            Scope: scope,
            UserScopeDirectory: userScopeDirectory,
            ModelPresets: presets,
            PreloadedSource: Canonical,
            Arbiter: new SquadArbiterWiring(Enabled: arbiterEnabled, HookTimeoutSeconds: 45));
        return Registry().RenderAsync(request);
    }

    private static SquadModelProfiles Merged(SquadModelPreset? preset) =>
        preset is null ? Canonical.ModelProfiles : preset.ApplyTo(Canonical.ModelProfiles);

    private static List<(string Target, string RelativePath, string Content)> Snapshot(SquadRenderResult result) =>
        result.Files
            .OrderBy(file => file.Target, StringComparer.Ordinal)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file => (file.Target, file.RelativePath, Convert.ToBase64String(file.Content.Span)))
            .ToList();

    /// <summary>Writes the rendered arbiter files under the project root and reads the claude table back.</summary>
    private ArbiterModelTargets? WriteAndReadClaudeTable(SquadRenderResult result)
    {
        foreach (SquadDeploymentFile file in result.Files.Where(file =>
                     file.RelativePath.StartsWith("artifacts/arbiter/", StringComparison.Ordinal)))
        {
            string path = Path.Combine(ProjectRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Content.ToArray());
        }

        return ArbiterModelTargets.TryRead(Path.Combine(ProjectRoot, "artifacts", "arbiter"), "claude");
    }

    [Fact]
    public async Task ProjectScopeWithArbiterEnabled_WritesTablePerHookedTarget()
    {
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Project, arbiterEnabled: true);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile claude = Assert.Single(result.Files, file => file.RelativePath == ProjectTableFile);
        Assert.Equal("claude", claude.Target);
        Assert.Contains(result.Files, file => file.RelativePath == ProjectOpenCodeTableFile);
    }

    [Fact]
    public async Task GlobalScopeWithArbiterEnabled_WritesTableUnderInjectedHome()
    {
        SquadRenderResult result = await RenderAsync(
            SquadDeploymentScope.Global,
            arbiterEnabled: true,
            userScopeDirectory: Home);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(
            result.Files,
            file => file.RelativePath.Replace('\\', '/').EndsWith(GlobalTableSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstanceScopeWritesNoTable()
    {
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Instance, arbiterEnabled: true);

        Assert.DoesNotContain(result.Files, file => file.RelativePath.Contains("model-targets", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfilesEqualResolvedTokenForEveryBundledProfile(bool withPreset)
    {
        SquadModelPreset? preset = withPreset ? CreatePreset() : null;
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Project, true, preset);
        ArbiterModelTargets table = Assert.IsType<ArbiterModelTargets>(WriteAndReadClaudeTable(result));

        SquadModelProfiles merged = Merged(preset);
        foreach (string profile in Canonical.ModelProfiles.Profiles.Keys)
        {
            Assert.Equal(
                SquadModelResolution.ResolveToken(merged, profile, SquadTarget.Claude),
                table.Profiles[profile]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentsRecordProfileAndRenderedTokenForEveryCanonicalAgent(bool withPreset)
    {
        SquadModelPreset? preset = withPreset ? CreatePreset() : null;
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Project, true, preset);
        ArbiterModelTargets table = Assert.IsType<ArbiterModelTargets>(WriteAndReadClaudeTable(result));

        SquadModelProfiles merged = Merged(preset);
        foreach (SquadAgent agent in Canonical.Agents)
        {
            Assert.True(table.Agents.ContainsKey(agent.Name), $"agents table is missing '{agent.Name}'.");
            Assert.Equal(agent.ModelProfile, table.Agents[agent.Name].Profile);
            Assert.Equal(
                SquadModelResolution.ResolveToken(merged, agent.ModelProfile, SquadTarget.Claude),
                table.Agents[agent.Name].Token);
        }
    }

    [Fact]
    public async Task ArbiterDisabled_NoTableAndByteIdenticalRender()
    {
        SquadRenderResult disabled = await RenderAsync(SquadDeploymentScope.Project, arbiterEnabled: false);
        SquadRenderResult withoutWiring = await Registry().RenderAsync(new SquadRenderRequest(
            SourceDirectory: CorpusRoot,
            Targets: [SquadTarget.Claude, SquadTarget.OpenCode],
            Scope: SquadDeploymentScope.Project,
            PreloadedSource: Canonical));

        Assert.True(disabled.Success, string.Join("; ", disabled.Errors));
        Assert.DoesNotContain(disabled.Files, file => file.RelativePath.Contains("model-targets", StringComparison.Ordinal));
        Assert.Equal(Snapshot(withoutWiring), Snapshot(disabled));
    }

    [Fact]
    public async Task WrittenTableRoundTripsThroughTryRead()
    {
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Project, arbiterEnabled: true);

        ArbiterModelTargets table = Assert.IsType<ArbiterModelTargets>(WriteAndReadClaudeTable(result));

        Assert.Equal("claude", table.Harness);
        Assert.Equal(Canonical.ModelProfiles.Profiles.Count, table.Profiles.Count);
        Assert.Equal(Canonical.Agents.Count, table.Agents.Count);
    }
}
