using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Verifies rendering behavior and contract invariants when model presets are applied.
///     Acceptance criteria:
///     - 2.2: Squad SHALL NOT modify a deployed file outside that transaction.
///     - 2.3: A preset SHALL change only the rendered model value; tools, permissions and bodies
///     SHALL be byte-identical to a render without it.
/// </summary>
public sealed class SquadModelPresetRenderTests
{
    private const string ProbeModel = "preset-probe-model";

    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    public static TheoryData<SquadTarget> AllTargets()
    {
        TheoryData<SquadTarget> data = [];
        data.Add(SquadTarget.Antigravity);
        data.Add(SquadTarget.Claude);
        data.Add(SquadTarget.CommandCode);
        data.Add(SquadTarget.Codex);
        data.Add(SquadTarget.Copilot);
        data.Add(SquadTarget.Cursor);
        data.Add(SquadTarget.Devin);
        data.Add(SquadTarget.Factory);
        data.Add(SquadTarget.Kilo);
        data.Add(SquadTarget.OpenCode);
        data.Add(SquadTarget.Pi);
        data.Add(SquadTarget.Warp);
        data.Add(SquadTarget.ZCode);
        return data;
    }

    /// <summary>
    ///     Builds the renderer registry matching all 13 supported targets.
    /// </summary>
    private static SquadRendererRegistry CreateRegistry()
    {
        return new SquadRendererRegistry(
        [
            new AntigravityRenderer(),
            new ClaudeRenderer(),
            new CommandCodeRenderer(),
            new CodexRenderer(),
            new CopilotRenderer(),
            new CursorRenderer(),
            new DevinRenderer(),
            new FactoryRenderer(),
            new KiloRenderer(),
            new OpenCodeRenderer(),
            new PiRenderer(),
            new WarpRenderer(),
            new ZCodeRenderer()
        ]);
    }

    /// <summary>
    ///     Creates a universal probe preset where every category and target maps to <see cref="ProbeModel" />.
    /// </summary>
    private static SquadModelPreset CreateUniversalProbePreset(SquadSource source)
    {
        Dictionary<string, SquadModelProfile> profiles = new(StringComparer.Ordinal);
        foreach (string category in source.ModelProfiles.Profiles.Keys)
        {
            Dictionary<string, string> harnessModels = new(StringComparer.Ordinal);
            foreach (SquadTarget target in SquadTargetCatalog.All)
                harnessModels[SquadTargetCatalog.GetToken(target)] = ProbeModel;

            profiles[category] = new SquadModelProfile(ProbeModel, harnessModels);
        }

        return new SquadModelPreset(
            "probe-preset",
            "presets/probe-preset.yml",
            "probe-digest",
            profiles);
    }

    /// <summary>
    ///     Drops lines starting with 'model' or '"model"' (or YAML/JSON/TOML model keys),
    ///     as well as model companion lines such as 'thinking:' and 'reasoningEffort:',
    ///     to normalize files for byte-identical comparison of non-model content.
    /// </summary>
    private static byte[] DropModelLines(ReadOnlyMemory<byte> content)
    {
        string text = Encoding.UTF8.GetString(content.Span);
        string[] lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        List<string> filtered = new();
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("model:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("\"model\"", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("'model'", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("model =", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("thinking:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("\"thinking\"", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("'thinking'", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("thinking =", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("reasoningEffort:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("\"reasoningEffort\"", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("'reasoningEffort'", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("reasoningEffort =", StringComparison.OrdinalIgnoreCase))
                continue;

            filtered.Add(line);
        }

        return Encoding.UTF8.GetBytes(string.Join("\n", filtered));
    }

    /// <summary>
    ///     Matches a deployed relative path to its corresponding agent definition if applicable.
    /// </summary>
    private static SquadAgent? FindAgentForFile(string relativePath, SquadTarget target, IEnumerable<SquadAgent> agents)
    {
        if (target == SquadTarget.Warp) return null;

        // ZCode renders conductor as a lowered command (.zcode/commands/conductor.md) rather than under agents/.
        if (target == SquadTarget.ZCode
            && (string.Equals(relativePath, ".zcode/commands/conductor.md", StringComparison.OrdinalIgnoreCase)
                || relativePath.EndsWith("/conductor.md", StringComparison.OrdinalIgnoreCase)))
            return agents.FirstOrDefault(agent =>
                string.Equals(agent.Name, "conductor", StringComparison.OrdinalIgnoreCase));

        if (!relativePath.Contains("/agents/", StringComparison.OrdinalIgnoreCase)
            && !relativePath.Contains("/droids/", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (SquadAgent agent in agents)
            if (relativePath.EndsWith($"/{agent.Name}.agent.md", StringComparison.OrdinalIgnoreCase)
                || relativePath.EndsWith($"/{agent.Name}.md", StringComparison.OrdinalIgnoreCase)
                || relativePath.EndsWith($"/{agent.Name}.toml", StringComparison.OrdinalIgnoreCase)
                || relativePath.EndsWith($"/{agent.Name}/agent.md", StringComparison.OrdinalIgnoreCase))
                return agent;

        return null;
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public async Task RenderWithPresetPreservesIdenticalRelativePathsAcrossAllTargets(SquadTarget target)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadModelPreset probePreset = CreateUniversalProbePreset(source);
        SquadRendererRegistry registry = CreateRegistry();

        SquadRenderRequest requestWithout = new(
            ProductRoot,
            [target],
            SquadDeploymentScope.Project,
            null);

        SquadRenderRequest requestWith = new(
            ProductRoot,
            [target],
            SquadDeploymentScope.Project,
            probePreset);

        SquadRenderResult resultWithout = await registry.RenderAsync(requestWithout);
        SquadRenderResult resultWith = await registry.RenderAsync(requestWith);

        Assert.True(resultWithout.Success, string.Join("; ", resultWithout.Errors));
        Assert.True(resultWith.Success, string.Join("; ", resultWith.Errors));

        string[] pathsWithout = resultWithout.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal).ToArray();
        string[] pathsWith = resultWith.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(pathsWithout, pathsWith);
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public async Task RenderWithPresetPreservesByteIdenticalContentOutsideModelLines(SquadTarget target)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadModelPreset probePreset = CreateUniversalProbePreset(source);
        SquadRendererRegistry registry = CreateRegistry();

        SquadRenderRequest requestWithout = new(
            ProductRoot,
            [target],
            SquadDeploymentScope.Project,
            null);

        SquadRenderRequest requestWith = new(
            ProductRoot,
            [target],
            SquadDeploymentScope.Project,
            probePreset);

        SquadRenderResult resultWithout = await registry.RenderAsync(requestWithout);
        SquadRenderResult resultWith = await registry.RenderAsync(requestWith);

        Assert.True(resultWithout.Success, string.Join("; ", resultWithout.Errors));
        Assert.True(resultWith.Success, string.Join("; ", resultWith.Errors));

        Dictionary<string, SquadDeploymentFile> filesWithByPath =
            resultWith.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);

        foreach (SquadDeploymentFile fileWithout in resultWithout.Files)
        {
            Assert.True(filesWithByPath.TryGetValue(fileWithout.RelativePath, out SquadDeploymentFile? fileWith));
            byte[] normalizedWithout = DropModelLines(fileWithout.Content);
            byte[] normalizedWith = DropModelLines(fileWith!.Content);

            Assert.True(
                normalizedWithout.SequenceEqual(normalizedWith),
                $"File '{fileWithout.RelativePath}' on target '{SquadTargetCatalog.GetToken(target)}' differs outside its model definition.");
        }
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public async Task EveryTargetExceptWarpChangesAtLeastOneRenderedFileWithPreset(SquadTarget target)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadModelPreset probePreset = CreateUniversalProbePreset(source);
        SquadRendererRegistry registry = CreateRegistry();

        SquadRenderRequest requestWithout = new(
            ProductRoot,
            [target],
            SquadDeploymentScope.Project,
            null);

        SquadRenderRequest requestWith = new(
            ProductRoot,
            [target],
            SquadDeploymentScope.Project,
            probePreset);

        SquadRenderResult resultWithout = await registry.RenderAsync(requestWithout);
        SquadRenderResult resultWith = await registry.RenderAsync(requestWith);

        Assert.True(resultWithout.Success, string.Join("; ", resultWithout.Errors));
        Assert.True(resultWith.Success, string.Join("; ", resultWith.Errors));

        Dictionary<string, SquadDeploymentFile> filesWithByPath =
            resultWith.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        int changedFiles = 0;

        foreach (SquadDeploymentFile fileWithout in resultWithout.Files)
        {
            SquadDeploymentFile fileWith = filesWithByPath[fileWithout.RelativePath];
            if (!fileWithout.Content.Span.SequenceEqual(fileWith.Content.Span)) changedFiles++;
        }

        if (target == SquadTarget.Warp)
            Assert.Equal(0, changedFiles);
        else
            Assert.True(
                changedFiles > 0,
                $"Expected target '{SquadTargetCatalog.GetToken(target)}' to change at least one file when preset was applied, but changed count was 0.");
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public async Task RenderedFileContainsProbeModelExactlyWhenModelResolutionReturnsIt(SquadTarget target)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadModelPreset probePreset = CreateUniversalProbePreset(source);
        SquadRendererRegistry registry = CreateRegistry();

        SquadRenderRequest requestWith = new(
            ProductRoot,
            [target],
            SquadDeploymentScope.Project,
            probePreset);

        SquadRenderResult resultWith = await registry.RenderAsync(requestWith);
        Assert.True(resultWith.Success, string.Join("; ", resultWith.Errors));

        SquadModelProfiles effectiveProfiles = probePreset.ApplyTo(source.ModelProfiles);
        string targetToken = SquadTargetCatalog.GetToken(target);

        foreach (SquadDeploymentFile file in resultWith.Files)
        {
            string content = Encoding.UTF8.GetString(file.Content.Span);
            bool containsProbe = content.Contains(ProbeModel, StringComparison.Ordinal);

            SquadAgent? matchingAgent = FindAgentForFile(file.RelativePath, target, source.Agents);

            if (matchingAgent is not null)
            {
                string? resolvedModel =
                    SquadModelResolution.ResolveToken(effectiveProfiles, matchingAgent.ModelProfile, targetToken);
                bool expectedProbe = string.Equals(resolvedModel, ProbeModel, StringComparison.Ordinal);

                Assert.True(
                    containsProbe == expectedProbe,
                    $"File '{file.RelativePath}' on target '{targetToken}' contains probe={containsProbe}, but ResolveToken was '{resolvedModel}' (expected probe={expectedProbe}).");
            }
            else
            {
                Assert.False(
                    containsProbe,
                    $"Non-agent file '{file.RelativePath}' on target '{targetToken}' unexpectedly contains the probe model.");
            }
        }
    }

    [Fact]
    public async Task RenderWithSelectivePresetOmitsModelWhenResolutionReturnsNull()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = CreateRegistry();

        Dictionary<string, SquadModelProfile> selectiveProfiles = new(StringComparer.Ordinal)
        {
            ["fast"] = new SquadModelProfile(ProbeModel,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["copilot"] = ProbeModel }),
            ["architect"] = new SquadModelProfile("inherit",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["copilot"] = "inherit" })
        };
        SquadModelPreset selectivePreset = new("selective", "selective.yml", "digest", selectiveProfiles);

        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project,
            selectivePreset);

        SquadRenderResult result = await registry.RenderAsync(request);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadModelProfiles effective = selectivePreset.ApplyTo(source.ModelProfiles);
        string? architectModel = SquadModelResolution.ResolveToken(effective, "architect", "copilot");
        string? fastModel = SquadModelResolution.ResolveToken(effective, "fast", "copilot");

        Assert.Null(architectModel);
        Assert.Equal(ProbeModel, fastModel);

        SquadDeploymentFile architectFile = Assert.Single(
            result.Files,
            f => f.RelativePath == ".github/agents/architect.agent.md");
        string architectContent = Encoding.UTF8.GetString(architectFile.Content.Span);
        Assert.DoesNotContain(ProbeModel, architectContent, StringComparison.Ordinal);

        SquadDeploymentFile csharpDevFile = Assert.Single(
            result.Files,
            f => f.RelativePath == ".github/agents/csharp-dev.agent.md");
        string csharpDevContent = Encoding.UTF8.GetString(csharpDevFile.Content.Span);
        Assert.Contains(ProbeModel, csharpDevContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderExecutionDoesNotMutateCanonicalCorpusOnDisk()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadModelPreset probePreset = CreateUniversalProbePreset(source);
        SquadRendererRegistry registry = CreateRegistry();

        string modelsPath = Path.Combine(ProductRoot, "profiles", "models.yml");
        byte[] beforeBytes = await File.ReadAllBytesAsync(modelsPath);

        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project,
            probePreset);

        SquadRenderResult result = await registry.RenderAsync(request);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        byte[] afterBytes = await File.ReadAllBytesAsync(modelsPath);
        Assert.Equal(beforeBytes, afterBytes);
    }
}
