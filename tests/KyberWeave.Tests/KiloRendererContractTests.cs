using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="SquadRendererRegistry"/> with <see cref="KiloRenderer"/> and validates the result
/// against Kilo's native agent and skill contracts.
/// </summary>
/// <remarks>
/// Validates that canonical agents lower to Kilo agents at <c>.kilo/agents/&lt;name&gt;.md</c>
/// and skills lower to <c>.kilo/skills/&lt;name&gt;/SKILL.md</c>. Verifies profile-declared
/// shared-identity suppression, model resolution from <c>models.yml</c>, deterministic output,
/// and structured degradation accounting (<c>permission-not-expressible</c> with matching
/// <c>InstructionDigest</c>).
/// </remarks>
public sealed class KiloRendererContractTests : IDisposable
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
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SupportedTargets_IsExactlyKilo()
    {
        SquadRendererRegistry registry = new([new KiloRenderer()]);

        Assert.Equal([SquadTarget.Kilo], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude, SquadTarget.Kilo],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("claude", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonKiloTarget()
    {
        KiloRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Kilo_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = SharedIdentities(source);
        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Kilo],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));
        int expectedSkillCount = source.Skills.Count - suppressedSkillCount;

        // C3: every rendered owner also projects its validated resource closure beside its
        // principal output, so the corpus count is principals plus emitted closures.
        int expectedFileCount =
            source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + expectedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);
        Assert.Equal(expectedFileCount, result.Files.Count);

        Assert.All(result.Files, f =>
        {
            Assert.Equal("kilo", f.Target);
            Assert.True(
                f.RelativePath.StartsWith(".kilo/agents/", StringComparison.Ordinal) ||
                f.RelativePath.StartsWith(".kilo/skills/", StringComparison.Ordinal),
                $"File '{f.RelativePath}' does not start with expected Kilo directory.");
        });

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".kilo/agents/{agent.Name}.md");

            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent.Name);
            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name", agent.Name));
            Assert.True(
                string.Equals(agent.Description, RequireScalar(frontmatter, "description", agent.Name), StringComparison.Ordinal),
                $"Agent '{agent.Name}' description mismatch.");

            // Model resolution: verify against loaded ModelProfiles.
            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            if (modelProfile.HarnessModels.TryGetValue("kilo", out string? expectedModel))
            {
                if (string.Equals(expectedModel, "inherit", StringComparison.Ordinal))
                {
                    Assert.False(
                        frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                        $"Agent '{agent.Name}' should omit 'model' when resolved to inherit.");
                }
                else
                {
                    Assert.True(
                        string.Equals(expectedModel, RequireScalar(frontmatter, "model", agent.Name), StringComparison.Ordinal),
                        $"Agent '{agent.Name}' model mismatch: expected '{expectedModel}'.");
                }
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal))
            {
                Assert.True(
                    string.Equals(modelProfile.Default, RequireScalar(frontmatter, "model", agent.Name), StringComparison.Ordinal),
                    $"Agent '{agent.Name}' model mismatch: expected '{modelProfile.Default}'.");
            }
            else
            {
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                    $"Agent '{agent.Name}' should omit 'model' (profile default is inherit).");
            }

            // Exact comparison with normalized body
            string expectedAgentBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedAgentBody.EndsWith('\n'))
            {
                expectedAgentBody += "\n";
            }

            Assert.True(
                string.Equals(expectedAgentBody, body, StringComparison.Ordinal),
                $"Agent '{agent.Name}' body mismatch.");
        }

        // Shared identities suppression verification
        foreach (string sharedIdentity in sharedIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".kilo/agents/{sharedIdentity}.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".kilo/skills/{sharedIdentity}/SKILL.md");
        }

        // Skills verification
        foreach (SquadSkill skill in source.Skills)
        {
            bool isSharedIdentity = sharedIdentities.Contains(skill.Name);
            string path = $".kilo/skills/{skill.Name}/SKILL.md";
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
            Assert.True(
                string.Equals(expectedDescription, RequireScalar(frontmatter, "description", skill.Name), StringComparison.Ordinal),
                $"Skill '{skill.Name}' description mismatch.");
            Assert.True(
                string.Equals("MIT", RequireScalar(frontmatter, "license", skill.Name), StringComparison.Ordinal),
                $"Skill '{skill.Name}' license mismatch.");

            string expectedSkillBody = skill.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedSkillBody.EndsWith('\n'))
            {
                expectedSkillBody += "\n";
            }

            Assert.True(
                string.Equals(expectedSkillBody, skillBody, StringComparison.Ordinal),
                $"Skill '{skill.Name}' body mismatch.");
        }

        // Degradations: exactly the non-all-deny agents carry 'permission-not-expressible'
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
            Assert.True(
                string.Equals("kilo", degradation.Target, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong target.");
            Assert.True(
                string.Equals("permission-not-expressible", degradation.Code, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong code.");
            Assert.True(
                string.Equals(degradation.CanonicalIdentity, degradation.OutputIdentity, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong output identity.");
            SquadAgent agent = agentsByName[degradation.CanonicalIdentity];
            Assert.True(
                string.Equals(agent.BodyDigest, degradation.InstructionDigest, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong instruction digest.");
        }
    }

    [Fact]
    public async Task RenderAsync_IsDeterministic()
    {
        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Kilo],
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
    }

    [Fact]
    public async Task RenderAsync_Kilo_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new KiloRenderer(),
            SquadTarget.Kilo,
            $".kilo/agents/{ResourceBearingSquadFixture.AgentName}.md",
            ".kilo/agents",
            ".kilo/skills");
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
