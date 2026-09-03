using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real, checked-in canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="SquadRendererRegistry"/> and pins the result against GitHub Copilot's documented
/// custom-agent and agent-skill contract.
/// </summary>
/// <remarks>
/// This replaces the former APM contract suite, which asserted a hand-rolled fake against
/// itself — every render request and response in that suite was fabricated by
/// <c>FakeApmRunner</c>, so nothing in it ever touched a real renderer or real canonical
/// content. Rendering the actual shipped corpus here means a broken renderer, a stale
/// path convention, or drift between the canonical source and Copilot's contract is a test
/// failure instead of invisible.
/// </remarks>
public sealed class SquadRenderingContractTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    [Fact]
    public void SupportedTargets_IsExactlyCopilotToday()
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);

        Assert.Equal([SquadTarget.Copilot], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude, SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("claude", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Copilot_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));

        // C3: every rendered owner also projects its validated resource closure beside its
        // principal output, so the corpus count is principals plus emitted closures.
        int expectedFileCount =
            source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + source.Skills.Count - suppressedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);
        Assert.Equal(expectedFileCount, result.Files.Count);
        Assert.All(result.Files, f => Assert.Equal("copilot", f.Target));

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".github/agents/{agent.Name}.agent.md");

            YamlMappingNode frontmatter = ReadFrontmatter(file);
            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name"));
            Assert.Equal(agent.Description, RequireScalar(frontmatter, "description"));

            if (agent.Invocation == SquadInvocation.Subagent)
            {
                Assert.Equal("false", RequireScalar(frontmatter, "user-invocable"));
            }
            else
            {
                Assert.False(frontmatter.Children.ContainsKey(new YamlScalarNode("user-invocable")));
            }

            IReadOnlyList<string> tools = RequireSequence(frontmatter, "tools");
            Assert.Equal(CopilotToolCatalog.Normalize(agent.CopilotTools), tools);
            Assert.Equal(tools.Distinct(StringComparer.Ordinal).Count(), tools.Count);

            string body = ReadBody(file);
            Assert.True(body.Length <= 30_000, $"'{agent.Name}' body is {body.Length} characters; Copilot caps agent files at 30,000.");
            Assert.Contains(agent.InstructionBody.Trim(), body, StringComparison.Ordinal);
        }

        foreach (string sharedIdentity in sharedIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".github/agents/{sharedIdentity}.agent.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".github/skills/{sharedIdentity}/SKILL.md");
        }

        foreach (SquadSkill skill in source.Skills)
        {
            bool isSharedIdentity = sharedIdentities.Contains(skill.Name);
            string path = $".github/skills/{skill.Name}/SKILL.md";
            if (isSharedIdentity)
            {
                Assert.DoesNotContain(result.Files, f => f.RelativePath == path);
                continue;
            }

            SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == path);
            YamlMappingNode frontmatter = ReadFrontmatter(file);
            Assert.Equal(skill.Name, RequireScalar(frontmatter, "name"));
            Assert.Equal(skill.Description, RequireScalar(frontmatter, "description"));
            Assert.Equal("MIT", RequireScalar(frontmatter, "license"));
        }

        // 'deny' is now enforced by omission from the allow-list, so it needs no record.
        // Only 'ask' still loses meaning — Copilot has no per-tool confirmation gate — and
        // exactly the agents on an ask-bearing profile carry a safety-narrowed degradation.
        string[] expectedNarrowed = source.Agents
            .Where(a => source.CapabilityProfiles.Profiles[a.CopilotCapabilityProfile ?? a.CapabilityProfile]
                .Permissions.Values.Any(d => d == SquadPermissionDecision.Ask))
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedNarrowed);
        Assert.Equal(
            expectedNarrowed,
            result.Degradations.Select(d => d.CanonicalIdentity).OrderBy(n => n, StringComparer.Ordinal));

        foreach (SquadDegradationRecord degradation in result.Degradations)
        {
            Assert.Equal("copilot", degradation.Target);
            Assert.Equal("safety-narrowed", degradation.Code);
            Assert.Equal(degradation.CanonicalIdentity, degradation.OutputIdentity);
            SquadAgent agent = agentsByName[degradation.CanonicalIdentity];
            Assert.Equal(agent.BodyDigest, degradation.InstructionDigest);
        }

        // Model resolution: conductor uses a profile with no copilot-specific entry and
        // default: inherit, so its agent file omits 'model' entirely.
        foreach (string orchestrator in new[] { "conductor" })
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".github/agents/{orchestrator}.agent.md");
            YamlMappingNode frontmatter = ReadFrontmatter(file);
            Assert.False(frontmatter.Children.ContainsKey(new YamlScalarNode("model")));
        }

        // Every other agent's profile does carry a copilot-specific model — resolved from
        // the real profiles/models.yml, not a hardcoded expectation here.
        foreach (SquadAgent agent in source.Agents.Where(a => a.Name != "conductor"))
        {
            SquadModelProfile profile = source.ModelProfiles.Profiles[agent.ModelProfile];
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".github/agents/{agent.Name}.agent.md");
            YamlMappingNode frontmatter = ReadFrontmatter(file);
            Assert.Equal(profile.HarnessModels["copilot"], RequireScalar(frontmatter, "model"));
        }
    }

    [Fact]
    public async Task RenderAsync_Copilot_ProjectsLinkedAgentAndSkillResourcesWithoutRewritingLinks()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new CopilotRenderer(),
            SquadTarget.Copilot,
            ".github/agents/bug-crusher-investigator.agent.md",
            ".github/agents",
            ".github/skills");
    }

    [Fact]
    public async Task RenderAsync_WhenResourceAliasesAnotherPrincipalOutput_RejectsTheCollision()
    {
        using ResourceBearingSquadFixture fixture = ResourceBearingSquadFixture.Create();
        fixture.AppendAgentLink("[Collision](architect.md)");
        SquadRendererRegistry registry = new([new CopilotRenderer()]);

        SquadRenderValidationException exception = await Assert.ThrowsAsync<SquadRenderValidationException>(() =>
            registry.RenderAsync(new SquadRenderRequest(
                fixture.ProductRoot,
                [SquadTarget.Copilot],
                SquadDeploymentScope.Project)));

        Assert.Contains("collision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".github/agents/architect.agent.md", exception.Message, StringComparison.Ordinal);
    }

    private static (YamlMappingNode Frontmatter, string Body) SplitFrontmatter(string text)
    {
        const string delimiter = "---\n";
        Assert.StartsWith(delimiter, text, StringComparison.Ordinal);
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, "Expected a closing '---' frontmatter delimiter.");

        string yaml = text[delimiter.Length..(end + 1)];
        string body = text[(end + 5)..];

        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        YamlMappingNode root = Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
        return (root, body);
    }

    private static IReadOnlyList<string> RequireSequence(YamlMappingNode node, string key)
    {
        YamlNode value = node.Children[new YamlScalarNode(key)];
        YamlSequenceNode sequence = Assert.IsType<YamlSequenceNode>(value);
        return sequence.Children
            .Select(child => Assert.IsType<YamlScalarNode>(child).Value
                ?? throw new InvalidOperationException($"Key '{key}' has a null sequence entry."))
            .ToArray();
    }

    private static YamlMappingNode ReadFrontmatter(SquadDeploymentFile file) =>
        SplitFrontmatter(System.Text.Encoding.UTF8.GetString(file.Content.Span)).Frontmatter;

    private static string ReadBody(SquadDeploymentFile file) =>
        SplitFrontmatter(System.Text.Encoding.UTF8.GetString(file.Content.Span)).Body;

    private static string RequireScalar(YamlMappingNode node, string key)
    {
        YamlNode value = node.Children[new YamlScalarNode(key)];
        return Assert.IsType<YamlScalarNode>(value).Value
            ?? throw new InvalidOperationException($"Key '{key}' has a null scalar value.");
    }
}

/// <summary>
/// Shared C3 fixture and assertions for recursive agent/skill resources. The fixture copies
/// the canonical source into a disposable tree so the tests never mutate product content.
/// </summary>
internal sealed class ResourceBearingSquadFixture : IDisposable
{
    internal const string AgentName = "bug-crusher-investigator";
    internal const string SkillName = "azure-cli";
    internal const string AgentLink = "[Agent runbook](bug-crusher-investigator/references/runbook.md)";
    internal const string SkillLink = "[Skill guide](references/guide.md)";
    internal const string AgentRunbook = "[Payload](../assets/agent-payload.txt)\nAgent runbook.\n";
    internal const string AgentPayload = "agent payload\n";
    internal const string SkillGuide = "[Snippet](../assets/skill-snippet.txt)\nSkill guide.\n";
    internal const string SkillSnippet = "skill snippet\n";

    private readonly TempDirectory _temp = new();

    private ResourceBearingSquadFixture()
    {
        ProductRoot = Path.Combine(_temp.Path, "kyber-squad");
    }

    internal string ProductRoot { get; }

    internal static ResourceBearingSquadFixture Create()
    {
        ResourceBearingSquadFixture fixture = new();
        string canonicalRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
        CopyDirectory(canonicalRoot, fixture.ProductRoot);

        fixture.AppendAgentLink(AgentLink);
        fixture.Append(
            $"skills/{SkillName}/SKILL.md",
            "\n" + SkillLink + "\n");
        fixture.Write($"agents/{AgentName}/references/runbook.md", AgentRunbook);
        fixture.Write($"agents/{AgentName}/assets/agent-payload.txt", AgentPayload);
        fixture.Write($"skills/{SkillName}/references/guide.md", SkillGuide);
        fixture.Write($"skills/{SkillName}/assets/skill-snippet.txt", SkillSnippet);
        return fixture;
    }

    internal void AppendAgentLink(string link) =>
        Append($"agents/{AgentName}.md", "\n" + link + "\n");

    public void Dispose() => _temp.Dispose();

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            string destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    private void Append(string relativePath, string content) =>
        File.AppendAllText(
            Path.Combine(ProductRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            content,
            new System.Text.UTF8Encoding(false));

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(ProductRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }
}

/// <summary>Shared exact-path and byte assertions for C3 native and fallback renderers.</summary>
internal static class SquadResourceRenderingContract
{
    internal static async Task AssertNativeProjectionAsync(
        ISquadRenderer renderer,
        SquadTarget target,
        string agentPrincipalPath,
        string agentsDirectory,
        string skillsDirectory)
    {
        using ResourceBearingSquadFixture fixture = ResourceBearingSquadFixture.Create();
        SquadRendererRegistry registry = new([renderer]);
        SquadRenderRequest request = new(
            fixture.ProductRoot,
            [target],
            SquadDeploymentScope.Project);

        SquadRenderResult first = await registry.RenderAsync(request);
        SquadRenderResult second = await registry.RenderAsync(request);

        Assert.True(first.Success, string.Join("; ", first.Errors));
        Assert.True(second.Success, string.Join("; ", second.Errors));
        AssertProjection(
            first,
            second,
            agentPrincipalPath,
            [
                ($"{agentsDirectory}/{ResourceBearingSquadFixture.AgentName}/assets/agent-payload.txt", ResourceBearingSquadFixture.AgentPayload),
                ($"{agentsDirectory}/{ResourceBearingSquadFixture.AgentName}/references/runbook.md", ResourceBearingSquadFixture.AgentRunbook),
                ($"{skillsDirectory}/{ResourceBearingSquadFixture.SkillName}/assets/skill-snippet.txt", ResourceBearingSquadFixture.SkillSnippet),
                ($"{skillsDirectory}/{ResourceBearingSquadFixture.SkillName}/references/guide.md", ResourceBearingSquadFixture.SkillGuide)
            ]);
    }

    internal static async Task AssertFallbackProjectionAsync(ISquadRenderer renderer, SquadTarget target)
    {
        using ResourceBearingSquadFixture fixture = ResourceBearingSquadFixture.Create();
        SquadRendererRegistry registry = new([renderer]);
        SquadRenderRequest request = new(
            fixture.ProductRoot,
            [target],
            SquadDeploymentScope.Project);

        SquadRenderResult first = await registry.RenderAsync(request);
        SquadRenderResult second = await registry.RenderAsync(request);

        Assert.True(first.Success, string.Join("; ", first.Errors));
        Assert.True(second.Success, string.Join("; ", second.Errors));
        AssertProjection(
            first,
            second,
            $".agents/skills/{ResourceBearingSquadFixture.AgentName}/SKILL.md",
            [
                ($".agents/skills/{ResourceBearingSquadFixture.AgentName}/{ResourceBearingSquadFixture.AgentName}/assets/agent-payload.txt", ResourceBearingSquadFixture.AgentPayload),
                ($".agents/skills/{ResourceBearingSquadFixture.AgentName}/{ResourceBearingSquadFixture.AgentName}/references/runbook.md", ResourceBearingSquadFixture.AgentRunbook),
                ($".agents/skills/{ResourceBearingSquadFixture.SkillName}/assets/skill-snippet.txt", ResourceBearingSquadFixture.SkillSnippet),
                ($".agents/skills/{ResourceBearingSquadFixture.SkillName}/references/guide.md", ResourceBearingSquadFixture.SkillGuide)
            ]);
    }

    private static void AssertProjection(
        SquadRenderResult first,
        SquadRenderResult second,
        string agentPrincipalPath,
        IReadOnlyList<(string Path, string Content)> expectedResources)
    {
        Assert.Equal(first.Files.Select(file => file.RelativePath), second.Files.Select(file => file.RelativePath));
        Assert.Equal(first.Files.Count, second.Files.Count);
        for (int i = 0; i < first.Files.Count; i++)
        {
            Assert.True(
                first.Files[i].Content.Span.SequenceEqual(second.Files[i].Content.Span),
                $"Repeated render changed bytes for '{first.Files[i].RelativePath}'.");
        }

        string[] duplicatePaths = first.Files
            .GroupBy(file => file.RelativePath, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.True(duplicatePaths.Length == 0, $"Duplicate output paths: {string.Join(", ", duplicatePaths)}");

        SquadDeploymentFile agentPrincipal = Assert.Single(
            first.Files,
            file => string.Equals(file.RelativePath, agentPrincipalPath, StringComparison.Ordinal));
        string agentBody = System.Text.Encoding.UTF8.GetString(agentPrincipal.Content.Span);
        Assert.Contains(ResourceBearingSquadFixture.AgentLink, agentBody, StringComparison.Ordinal);

        string[] actualResourceOrder = first.Files
            .Where(file => expectedResources.Any(expected =>
                string.Equals(expected.Path, file.RelativePath, StringComparison.Ordinal)))
            .Select(file => file.RelativePath)
            .ToArray();
        Assert.Equal(expectedResources.Select(resource => resource.Path), actualResourceOrder);

        foreach ((string expectedPath, string expectedContent) in expectedResources)
        {
            SquadDeploymentFile resource = Assert.Single(
                first.Files,
                file => string.Equals(file.RelativePath, expectedPath, StringComparison.Ordinal));
            Assert.True(
                System.Text.Encoding.UTF8.GetBytes(expectedContent).AsSpan().SequenceEqual(resource.Content.Span),
                $"Resource '{expectedPath}' did not preserve its exact UTF-8 bytes.");
        }
    }
}
