using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="SquadRendererRegistry"/> with <see cref="FactoryRenderer"/> and validates the
/// result against Factory Droids' native agent and skill path contracts.
/// </summary>
/// <remarks>
/// Validates that canonical agents render at <c>.factory/agents/&lt;name&gt;.md</c> and skills
/// at <c>.factory/skills/&lt;name&gt;/SKILL.md</c>. Asserts against the loaded
/// <see cref="SquadSource"/> model rather than hardcoded roster literals. Verifies
/// profile-declared shared-identity suppression, model resolution from <c>models.yml</c>,
/// and structured <c>permission-not-expressible</c> degradation when Factory's permission
/// mapping cannot be verified.
/// </remarks>
public sealed class FactoryRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Shared identities are read from the loaded fallback profile so the test follows the
    /// same generic single-projection contract as the renderer.
    /// </summary>
    private static HashSet<string> SharedIdentities(SquadSource source) =>
        source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

    public void Dispose()
    {
        // No disposable state: suite reads canonical checked-in corpus.
        // Implements IDisposable per repository test coding standard.
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SupportedTargets_IsExactlyFactory()
    {
        SquadRendererRegistry registry = new([new FactoryRenderer()]);

        Assert.Equal([SquadTarget.Factory], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude, SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("claude", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonFactoryTarget()
    {
        FactoryRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Factory_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = SharedIdentities(source);
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));
        int expectedSkillCount = source.Skills.Count - suppressedSkillCount;

        int expectedFileCount = source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + expectedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);
        Assert.Equal(expectedFileCount, result.Files.Count);

        Assert.All(result.Files, f =>
        {
            Assert.Equal("factory", f.Target);
            Assert.True(
                f.RelativePath.StartsWith(".factory/agents/", StringComparison.Ordinal) ||
                f.RelativePath.StartsWith(".factory/skills/", StringComparison.Ordinal),
                $"File '{f.RelativePath}' does not start with expected Factory directory.");
        });

        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("role-", StringComparison.OrdinalIgnoreCase));

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".factory/agents/{agent.Name}.md");

            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent.Name);

            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name", agent.Name));
            Assert.Equal(agent.Description, RequireScalar(frontmatter, "description", agent.Name));

            // No invented permission field: Factory's mapping is unverified.
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("tools")),
                $"Agent '{agent.Name}' should not emit 'tools'.");
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("permission")),
                $"Agent '{agent.Name}' should not emit 'permission'.");
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("permissions")),
                $"Agent '{agent.Name}' should not emit 'permissions'.");

            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            if (modelProfile.HarnessModels.TryGetValue("factory", out string? factoryHarnessModel))
            {
                if (string.Equals(factoryHarnessModel, "inherit", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(factoryHarnessModel))
                {
                    Assert.False(
                        frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                        $"Agent '{agent.Name}' should omit 'model' (factory harness override is inherit/empty).");
                }
                else
                {
                    Assert.Equal(factoryHarnessModel, RequireScalar(frontmatter, "model", agent.Name));
                }
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal) &&
                     !string.IsNullOrWhiteSpace(modelProfile.Default))
            {
                Assert.Equal(modelProfile.Default, RequireScalar(frontmatter, "model", agent.Name));
            }
            else
            {
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                    $"Agent '{agent.Name}' should omit 'model' (profile default is inherit).");
            }

            string expectedBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedBody.EndsWith('\n'))
            {
                expectedBody += "\n";
            }

            Assert.Equal(expectedBody, body);
        }

        foreach (string sharedIdentity in sharedIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".factory/agents/{sharedIdentity}.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".factory/skills/{sharedIdentity}/SKILL.md");
        }

        foreach (SquadSkill skill in source.Skills)
        {
            bool isSharedIdentity = sharedIdentities.Contains(skill.Name);
            string path = $".factory/skills/{skill.Name}/SKILL.md";
            if (isSharedIdentity)
            {
                Assert.DoesNotContain(result.Files, f => f.RelativePath == path);
                continue;
            }

            SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == path);
            (YamlMappingNode frontmatter, string skillBody) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                skill.Name);

            Assert.Equal(skill.Name, RequireScalar(frontmatter, "name", skill.Name));

            string expectedDescription = string.Join(" ", skill.Description.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.Equal(expectedDescription, RequireScalar(frontmatter, "description", skill.Name));
            Assert.Equal("MIT", RequireScalar(frontmatter, "license", skill.Name));

            string expectedSkillBody = skill.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedSkillBody.EndsWith('\n'))
            {
                expectedSkillBody += "\n";
            }

            Assert.Equal(expectedSkillBody, skillBody);
        }

        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];
        string[] expectedDegraded = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                return capabilityVocabulary.Any(cap =>
                    prof.Permissions.TryGetValue(cap, out SquadPermissionDecision decision) &&
                    decision != SquadPermissionDecision.Deny);
            })
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedDegraded);
        Assert.Equal(
            expectedDegraded,
            result.Degradations.Select(d => d.CanonicalIdentity).OrderBy(n => n, StringComparer.Ordinal));

        foreach (SquadDegradationRecord degradation in result.Degradations)
        {
            Assert.Equal("factory", degradation.Target);
            Assert.Equal("permission-not-expressible", degradation.Code);
            Assert.Equal(degradation.CanonicalIdentity, degradation.OutputIdentity);

            SquadAgent agent = agentsByName[degradation.CanonicalIdentity];
            Assert.Equal(agent.BodyDigest, degradation.InstructionDigest);
            Assert.NotNull(degradation.Details);
            Assert.Contains(agent.CapabilityProfile, degradation.Details, StringComparison.Ordinal);
            Assert.Contains("no verified permission mapping", degradation.Details, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RenderAsync_IsDeterministic()
    {
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult first = await registry.RenderAsync(request);
        SquadRenderResult second = await registry.RenderAsync(request);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Files.Count, second.Files.Count);

        for (int i = 0; i < first.Files.Count; i++)
        {
            SquadDeploymentFile file1 = first.Files[i];
            SquadDeploymentFile file2 = second.Files[i];

            Assert.Equal(file1.RelativePath, file2.RelativePath);
            Assert.Equal(file1.Target, file2.Target);
            Assert.True(file1.Content.Span.SequenceEqual(file2.Content.Span), $"Content differed for file '{file1.RelativePath}'.");
        }

        Assert.Equal(first.Degradations.Count, second.Degradations.Count);
        for (int i = 0; i < first.Degradations.Count; i++)
        {
            Assert.Equal(first.Degradations[i], second.Degradations[i]);
        }
    }

    [Fact]
    public async Task RenderAsync_Factory_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new FactoryRenderer(),
            SquadTarget.Factory,
            ".factory/agents/bug-crusher-investigator.md",
            ".factory/agents",
            ".factory/skills");
    }

    [Fact]
    public async Task RenderSkill_FrontmatterKeyOrderIsStable()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = SharedIdentities(source);
        string probe = source.Skills
            .Select(skill => skill.Name)
            .Where(name => !sharedIdentities.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Corpus at '{ProductRoot}' declares no non-suppressed skills.");

        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project));

        SquadDeploymentFile rendered = Assert.Single(
            result.Files,
            f => f.RelativePath == $".factory/skills/{probe}/SKILL.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(rendered.Content.Span),
            probe);

        string[] keys = frontmatter.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(key => key.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(["name", "description", "license"], keys);
    }

    private static (YamlMappingNode Frontmatter, string Body) SplitFrontmatter(string text, string identity)
    {
        const string delimiter = "---\n";
        Assert.True(
            text.StartsWith(delimiter, StringComparison.Ordinal),
            $"'{identity}' is missing the opening frontmatter delimiter.");
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, $"'{identity}' is missing a closing '---' frontmatter delimiter.");

        string yaml = text[delimiter.Length..(end + 1)];
        string body = text[(end + 5)..];

        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        YamlMappingNode root = Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
        return (root, body);
    }

    private static string RequireScalar(YamlMappingNode node, string key, string identity)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"'{identity}' frontmatter is missing required key '{key}'. Present keys: {presentKeys}.");
        }

        return Assert.IsType<YamlScalarNode>(value).Value
            ?? throw new InvalidOperationException($"'{identity}' key '{key}' has a null scalar value.");
    }
}
