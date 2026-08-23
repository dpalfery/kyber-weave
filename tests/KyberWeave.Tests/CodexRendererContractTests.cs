using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="SquadRendererRegistry"/> with <see cref="CodexRenderer"/> and validates the result
/// against Codex's native agent TOML and skill contracts.
/// </summary>
/// <remarks>
/// Validates that canonical agents render as Codex native agent TOML primitives at
/// <c>.codex/agents/&lt;name&gt;.toml</c> and skills render at <c>.codex/skills/&lt;name&gt;/SKILL.md</c>.
/// Verifies single-projection suppression for primary conductors (<c>conductor</c> and <c>conductor-v3</c>),
/// model resolution from <c>models.yml</c>, TOML structure and instruction encoding,
/// and structured degradation accounting (<c>permission-not-expressible</c> with matching <c>InstructionDigest</c>).
/// </remarks>
public sealed class CodexRendererContractTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Governed capabilities expected to trigger degradation records when non-deny.
    /// </summary>
    private static readonly string[] GovernedCapabilities =
    [
        "filesystem.read",
        "filesystem.search",
        "filesystem.write",
        "process.execute",
        "network.read",
        "network.publish",
        "delegate"
    ];

    /// <summary>
    /// Primary agents emitted as native agents whose same-named canonical skills are suppressed
    /// per the single-projection rule.
    /// </summary>
    private static readonly HashSet<string> SuppressedSkillNames = ["conductor", "conductor-v3"];

    [Fact]
    public void SupportedTargets_IsExactlyCodex()
    {
        SquadRendererRegistry registry = new([new CodexRenderer()]);

        Assert.Equal([SquadTarget.Codex], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new CodexRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude, SquadTarget.Codex],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("claude", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonCodexTarget()
    {
        CodexRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Codex_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new CodexRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Codex],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Derive expected counts dynamically from loaded SquadSource:
        // 22 agents + (26 skills - conductor - conductor-v3 suppressed) = 46 files total
        int expectedSkillCount = source.Skills.Count - SuppressedSkillNames.Count;
        int expectedFileCount = source.Agents.Count + expectedSkillCount;
        Assert.Equal(expectedFileCount, result.Files.Count);

        Assert.All(result.Files, f =>
        {
            Assert.Equal("codex", f.Target);
            Assert.True(
                f.RelativePath.StartsWith(".codex/agents/", StringComparison.Ordinal) ||
                f.RelativePath.StartsWith(".codex/skills/", StringComparison.Ordinal),
                $"File '{f.RelativePath}' does not start with expected Codex directory.");
        });

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);

        // Agents verification
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".codex/agents/{agent.Name}.toml");

            string tomlContent = Encoding.UTF8.GetString(file.Content.Span);
            ParsedTomlAgent parsed = ParseAgentToml(tomlContent, agent.Name);

            Assert.Equal(agent.Name, parsed.Name);
            Assert.Equal(agent.Description, parsed.Description);

            // Model resolution verification against loaded ModelProfiles
            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            if (modelProfile.HarnessModels.TryGetValue("codex", out string? expectedModel))
            {
                Assert.Equal(expectedModel, parsed.Model);
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal))
            {
                Assert.Equal(modelProfile.Default, parsed.Model);
            }
            else
            {
                Assert.Null(parsed.Model);
            }

            // Exact instructions body check
            string expectedBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedBody.EndsWith('\n'))
            {
                expectedBody += "\n";
            }

            Assert.Equal(expectedBody, parsed.Instructions);
        }

        // Single-projection rule for primary orchestrators:
        // conductor and conductor-v3 must exist as native agents and NOT as skills
        foreach (string conductor in SuppressedSkillNames)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".codex/agents/{conductor}.toml");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".codex/skills/{conductor}/SKILL.md");
        }

        // Skills verification
        foreach (SquadSkill skill in source.Skills)
        {
            bool isConductor = SuppressedSkillNames.Contains(skill.Name);
            string path = $".codex/skills/{skill.Name}/SKILL.md";
            if (isConductor)
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

        // Degradations: every agent with non-deny capability profile must carry 'permission-not-expressible'
        string[] expectedDegraded = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                return GovernedCapabilities.Any(cap =>
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
            Assert.Equal("codex", degradation.Target);
            Assert.Equal("permission-not-expressible", degradation.Code);
            Assert.Equal(degradation.CanonicalIdentity, degradation.OutputIdentity);

            SquadAgent agent = agentsByName[degradation.CanonicalIdentity];
            Assert.Equal(agent.BodyDigest, degradation.InstructionDigest);
            Assert.NotNull(degradation.Details);
            Assert.Contains(agent.CapabilityProfile, degradation.Details, StringComparison.Ordinal);
        }

        // No permission widening detected
        Assert.DoesNotContain(
            result.Degradations,
            d => d.Code.Contains("widen", StringComparison.OrdinalIgnoreCase) ||
                 (d.Details is not null && d.Details.Contains("widening", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task RenderAsync_IsDeterministic()
    {
        SquadRendererRegistry registry = new([new CodexRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Codex],
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
    public async Task RenderSkill_FrontmatterKeyOrderIsStable()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string probe = source.Skills
            .Select(skill => skill.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Corpus at '{ProductRoot}' declares no skills.");

        SquadRenderResult result = await new CodexRenderer().RenderAsync(new SquadRenderRequest(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Codex],
            Scope: SquadDeploymentScope.Project));

        SquadDeploymentFile rendered = Assert.Single(
            result.Files,
            f => f.RelativePath == $".codex/skills/{probe}/SKILL.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(rendered.Content.Span),
            probe);

        string[] keys = frontmatter.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(key => key.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(["name", "description", "license"], keys);
    }

    private sealed record ParsedTomlAgent(
        string Name,
        string Description,
        string? Model,
        string Instructions);

    private static ParsedTomlAgent ParseAgentToml(string toml, string identity)
    {
        string? name = null;
        string? description = null;
        string? model = null;
        string? instructions = null;

        // Parse multiline instructions block: instructions = """\n ... \n"""
        int multilineIdx = toml.IndexOf("instructions = \"\"\"\n", StringComparison.Ordinal);
        string singleLinesPart = toml;
        if (multilineIdx >= 0)
        {
            singleLinesPart = toml[..multilineIdx];
            string afterOpen = toml[(multilineIdx + "instructions = \"\"\"\n".Length)..];
            int closeIdx = afterOpen.LastIndexOf("\"\"\"", StringComparison.Ordinal);
            Assert.True(closeIdx >= 0, $"Agent '{identity}' instructions multiline block is missing closing quotes.");
            string rawInstructions = afterOpen[..closeIdx];
            instructions = rawInstructions.Replace("\\\"\\\"\\\"", "\"\"\"", StringComparison.Ordinal);
        }

        string[] lines = singleLinesPart.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            Match match = Regex.Match(trimmed, @"^(?<key>[A-Za-z0-9_\-]+)\s*=\s*""(?<val>.*)""$");
            if (!match.Success)
            {
                continue;
            }

            string key = match.Groups["key"].Value;
            string val = UnescapeTomlString(match.Groups["val"].Value);

            switch (key)
            {
                case "name":
                    name = val;
                    break;
                case "description":
                    description = val;
                    break;
                case "model":
                    model = val;
                    break;
            }
        }

        Assert.NotNull(name);
        Assert.NotNull(description);
        Assert.NotNull(instructions);

        return new ParsedTomlAgent(name, description, model, instructions);
    }

    private static string UnescapeTomlString(string value) =>
        value.Replace("\\\\", "\\", StringComparison.Ordinal)
             .Replace("\\\"", "\"", StringComparison.Ordinal)
             .Replace("\\r", "\r", StringComparison.Ordinal)
             .Replace("\\n", "\n", StringComparison.Ordinal)
             .Replace("\\t", "\t", StringComparison.Ordinal);

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
