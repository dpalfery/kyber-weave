using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

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
        using ResourceBearingSquadFixture fixture = ResourceBearingSquadFixture.Create(inMemory: true);
        SquadRendererRegistry registry = fixture.Registry([renderer]);
        SquadRenderRequest request = fixture.Prepare(new SquadRenderRequest(
            fixture.ProductRoot,
            [target],
            SquadDeploymentScope.Project));

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

    /// <summary>
    /// Discovery-guarded projection for targets whose agent discovery registers nested
    /// markdown beside an agent principal (opencode, kilo): an agent-owned <c>.md</c> resource
    /// deploys beside its principal with a <c>.markdown</c> extension so the harness scanner
    /// ignores it, while skill resources keep verbatim projection beside their principal.
    /// </summary>
    /// <remarks>
    /// Fails against renderers that still project agent resources verbatim: the guarded path
    /// is missing, the authored link is unrewritten, a nested <c>.md</c> remains under the
    /// agents directory, and no <c>resource-links-rewritten</c> degradation is recorded.
    /// </remarks>
    internal static async Task AssertDiscoveryGuardedProjectionAsync(
        ISquadRenderer renderer,
        SquadTarget target,
        string agentPrincipalPath,
        string agentsDirectory,
        string skillsDirectory)
    {
        using ResourceBearingSquadFixture fixture = ResourceBearingSquadFixture.Create(inMemory: true);
        SquadSource source = fixture.Load();
        SquadAgent agent = Assert.Single(
            source.Agents,
            a => string.Equals(a.Name, ResourceBearingSquadFixture.AgentName, StringComparison.Ordinal));
        SquadRendererRegistry registry = fixture.Registry([renderer]);
        SquadRenderRequest request = fixture.Prepare(new SquadRenderRequest(
            fixture.ProductRoot,
            [target],
            SquadDeploymentScope.Project));

        SquadRenderResult first = await registry.RenderAsync(request);
        SquadRenderResult second = await registry.RenderAsync(request);

        Assert.True(first.Success, string.Join("; ", first.Errors));
        Assert.True(second.Success, string.Join("; ", second.Errors));

        // Render-twice determinism: same order, same bytes, same degradations.
        Assert.Equal(first.Files.Select(file => file.RelativePath), second.Files.Select(file => file.RelativePath));
        for (int i = 0; i < first.Files.Count; i++)
        {
            Assert.True(
                first.Files[i].Content.Span.SequenceEqual(second.Files[i].Content.Span),
                $"Repeated render changed bytes for '{first.Files[i].RelativePath}'.");
        }

        Assert.Equal(first.Degradations.Count, second.Degradations.Count);
        for (int i = 0; i < first.Degradations.Count; i++)
        {
            Assert.Equal(first.Degradations[i], second.Degradations[i]);
        }

        string targetToken = SquadTargetCatalog.GetToken(target);
        Assert.All(first.Files, file => Assert.Equal(targetToken, file.Target));

        string[] duplicatePaths = first.Files
            .GroupBy(file => file.RelativePath, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.True(duplicatePaths.Length == 0, $"Duplicate output paths: {string.Join(", ", duplicatePaths)}");

        string expectedRunbookPath = $"{agentsDirectory}/{ResourceBearingSquadFixture.AgentName}/references/runbook.markdown";
        string expectedPayloadPath = $"{agentsDirectory}/{ResourceBearingSquadFixture.AgentName}/assets/agent-payload.txt";
        string expectedGuidePath = $"{skillsDirectory}/{ResourceBearingSquadFixture.SkillName}/references/guide.md";
        string expectedSnippetPath = $"{skillsDirectory}/{ResourceBearingSquadFixture.SkillName}/assets/skill-snippet.txt";

        string[] expectedResourceOrder = [expectedPayloadPath, expectedRunbookPath, expectedSnippetPath, expectedGuidePath];
        string[] actualResourceOrder = first.Files
            .Where(file => expectedResourceOrder.Any(expected =>
                string.Equals(expected, file.RelativePath, StringComparison.Ordinal)))
            .Select(file => file.RelativePath)
            .ToArray();
        Assert.Equal(expectedResourceOrder, actualResourceOrder);

        // The agent-owned .md resource deploys with the discovery-guard extension, byte-identical
        // to the authored file: no frontmatter is injected.
        SquadDeploymentFile runbook = Assert.Single(
            first.Files,
            file => string.Equals(file.RelativePath, expectedRunbookPath, StringComparison.Ordinal));
        Assert.True(
            System.Text.Encoding.UTF8.GetBytes(ResourceBearingSquadFixture.AgentRunbook).AsSpan().SequenceEqual(runbook.Content.Span),
            $"Resource '{expectedRunbookPath}' did not preserve its exact UTF-8 bytes.");

        // The agent-owned non-.md resource projects verbatim.
        SquadDeploymentFile payload = Assert.Single(
            first.Files,
            file => string.Equals(file.RelativePath, expectedPayloadPath, StringComparison.Ordinal));
        Assert.True(
            System.Text.Encoding.UTF8.GetBytes(ResourceBearingSquadFixture.AgentPayload).AsSpan().SequenceEqual(payload.Content.Span),
            $"Resource '{expectedPayloadPath}' did not preserve its exact UTF-8 bytes.");

        // Skill resources stay beside the skill principal with verbatim extensions.
        Assert.Single(
            first.Files,
            file => string.Equals(
                file.RelativePath,
                $"{skillsDirectory}/{ResourceBearingSquadFixture.SkillName}/SKILL.md",
                StringComparison.Ordinal));
        SquadDeploymentFile guide = Assert.Single(
            first.Files,
            file => string.Equals(file.RelativePath, expectedGuidePath, StringComparison.Ordinal));
        Assert.True(
            System.Text.Encoding.UTF8.GetBytes(ResourceBearingSquadFixture.SkillGuide).AsSpan().SequenceEqual(guide.Content.Span),
            $"Resource '{expectedGuidePath}' did not preserve its exact UTF-8 bytes.");
        SquadDeploymentFile snippet = Assert.Single(
            first.Files,
            file => string.Equals(file.RelativePath, expectedSnippetPath, StringComparison.Ordinal));
        Assert.True(
            System.Text.Encoding.UTF8.GetBytes(ResourceBearingSquadFixture.SkillSnippet).AsSpan().SequenceEqual(snippet.Content.Span),
            $"Resource '{expectedSnippetPath}' did not preserve its exact UTF-8 bytes.");

        // The agent principal path and frontmatter shape are unchanged; its authored resource
        // link is rewritten to the discovery-guard path.
        SquadDeploymentFile agentPrincipal = Assert.Single(
            first.Files,
            file => string.Equals(file.RelativePath, agentPrincipalPath, StringComparison.Ordinal));
        (YamlMappingNode frontmatter, string agentBody) = SplitPrincipal(agentPrincipal);
        Assert.Equal(
            ResourceBearingSquadFixture.AgentName,
            Assert.IsType<YamlScalarNode>(frontmatter.Children[new YamlScalarNode("name")]).Value);
        // The principal's authored link is rewritten to the deployment path as the worker's tools
        // resolve it: from the project (or harness-root) directory, beside the agent principal.
        string expectedRewrittenLink =
            $"[Agent runbook]({agentsDirectory}/{ResourceBearingSquadFixture.AgentName}/references/runbook.markdown)";
        Assert.Contains(expectedRewrittenLink, agentBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ResourceBearingSquadFixture.AgentLink, agentBody, StringComparison.Ordinal);

        // No emitted path under the agents directory other than an agent principal may end in
        // .md: anything else would register as a phantom agent.
        foreach (SquadDeploymentFile file in first.Files.Where(file =>
            file.RelativePath.StartsWith(agentsDirectory + "/", StringComparison.Ordinal)))
        {
            string remainder = file.RelativePath[(agentsDirectory.Length + 1)..];
            bool isPrincipal = !remainder.Contains('/', StringComparison.Ordinal) && remainder.EndsWith(".md", StringComparison.Ordinal);
            if (!isPrincipal)
            {
                Assert.False(
                    file.RelativePath.EndsWith(".md", StringComparison.Ordinal),
                    $"Resource '{file.RelativePath}' would register as a phantom agent.");
            }
        }

        // Exactly one resource-links-rewritten degradation for the resource-owning agent.
        SquadDegradationRecord degradation = Assert.Single(
            first.Degradations,
            record => string.Equals(record.Code, "resource-links-rewritten", StringComparison.Ordinal) &&
                string.Equals(record.CanonicalIdentity, ResourceBearingSquadFixture.AgentName, StringComparison.Ordinal));
        Assert.Equal(targetToken, degradation.Target);
        Assert.Equal(ResourceBearingSquadFixture.AgentName, degradation.OutputIdentity);
        Assert.Equal(agent.BodyDigest, degradation.InstructionDigest);
        Assert.False(
            string.IsNullOrWhiteSpace(degradation.Details),
            "The resource-links-rewritten degradation must explain the discovery guard.");
        Assert.Contains(".markdown", degradation.Details, StringComparison.Ordinal);

        // Skill-owned resources need no link rewrite and record no guard degradation.
        Assert.DoesNotContain(
            first.Degradations,
            record => string.Equals(record.Code, "resource-links-rewritten", StringComparison.Ordinal) &&
                string.Equals(record.CanonicalIdentity, ResourceBearingSquadFixture.SkillName, StringComparison.Ordinal));
    }

    private static (YamlMappingNode Frontmatter, string Body) SplitPrincipal(SquadDeploymentFile principal)
    {
        const string delimiter = "---\n";
        string text = System.Text.Encoding.UTF8.GetString(principal.Content.Span);
        Assert.StartsWith(delimiter, text, StringComparison.Ordinal);
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, $"Expected a closing '---' frontmatter delimiter in '{principal.RelativePath}'.");

        string yaml = text[delimiter.Length..(end + 1)];
        string body = text[(end + 5)..];

        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        YamlMappingNode root = Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
        return (root, body);
    }

    internal static async Task AssertFallbackProjectionAsync(
        ISquadRenderer renderer,
        SquadTarget target,
        string skillsDirectory = ".agents/skills")
    {
        using ResourceBearingSquadFixture fixture = ResourceBearingSquadFixture.Create(inMemory: true);
        SquadRendererRegistry registry = fixture.Registry([renderer]);
        SquadRenderRequest request = fixture.Prepare(new SquadRenderRequest(
            fixture.ProductRoot,
            [target],
            SquadDeploymentScope.Project));

        SquadRenderResult first = await registry.RenderAsync(request);
        SquadRenderResult second = await registry.RenderAsync(request);

        Assert.True(first.Success, string.Join("; ", first.Errors));
        Assert.True(second.Success, string.Join("; ", second.Errors));
        AssertProjection(
            first,
            second,
            $"{skillsDirectory}/{ResourceBearingSquadFixture.AgentName}/SKILL.md",
            [
                ($"{skillsDirectory}/{ResourceBearingSquadFixture.AgentName}/{ResourceBearingSquadFixture.AgentName}/assets/agent-payload.txt", ResourceBearingSquadFixture.AgentPayload),
                ($"{skillsDirectory}/{ResourceBearingSquadFixture.AgentName}/{ResourceBearingSquadFixture.AgentName}/references/runbook.md", ResourceBearingSquadFixture.AgentRunbook),
                ($"{skillsDirectory}/{ResourceBearingSquadFixture.SkillName}/assets/skill-snippet.txt", ResourceBearingSquadFixture.SkillSnippet),
                ($"{skillsDirectory}/{ResourceBearingSquadFixture.SkillName}/references/guide.md", ResourceBearingSquadFixture.SkillGuide)
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
