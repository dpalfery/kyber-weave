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
/// <see cref="SquadRendererRegistry"/> with <see cref="CursorRenderer"/> and validates the result
/// against Cursor's documented subagent and skill contracts.
/// </summary>
/// <remarks>
/// Validates that canonical agents lower to Cursor subagents at <c>.cursor/agents/&lt;name&gt;.md</c>
/// and skills lower to <c>.cursor/skills/&lt;name&gt;/SKILL.md</c>. Verifies single-projection suppression
/// for primary conductors, permission lowering to Cursor's <c>readonly</c> boolean flag, model resolution
/// from <c>models.yml</c>, and structured degradation accounting.
/// </remarks>
public sealed class CursorRendererContractTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Mirrors CursorRenderer's governed capability set so the degradation oracle applies
    /// the same rule the renderer does; kept local so a renderer change must touch this
    /// suite deliberately.
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
    /// Primary agents are emitted as native subagents, so their same-named canonical skills
    /// are suppressed by the single-projection rule. Keep the set in one place for both the
    /// expected file count and per-skill assertions.
    /// </summary>
    private static readonly HashSet<string> SuppressedSkillNames = ["conductor", "conductor-v3"];

    [Fact]
    public void SupportedTargets_IsExactlyCursor()
    {
        SquadRendererRegistry registry = new([new CursorRenderer()]);

        Assert.Equal([SquadTarget.Cursor], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new CursorRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude, SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("claude", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonCursorTarget()
    {
        CursorRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Cursor_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new CursorRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Native primary agents suppress their same-named skills; derive the expected count
        // from the centralized suppression set rather than a literal subtraction.
        int expectedFileCount = source.Agents.Count + source.Skills.Count - SuppressedSkillNames.Count;
        Assert.Equal(expectedFileCount, result.Files.Count);
        Assert.All(result.Files, f => Assert.Equal("cursor", f.Target));

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".cursor/agents/{agent.Name}.md");

            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(Encoding.UTF8.GetString(file.Content.Span));
            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name"));
            Assert.True(
                string.Equals(agent.Description, RequireScalar(frontmatter, "description"), StringComparison.Ordinal),
                $"Agent '{agent.Name}' description mismatch.");

            // Model resolution: verify against loaded ModelProfiles. Every assertion names
            // the agent so a failure identifies the offender out of the 22-agent corpus.
            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            if (modelProfile.HarnessModels.TryGetValue("cursor", out string? expectedModel))
            {
                Assert.True(
                    string.Equals(expectedModel, RequireScalar(frontmatter, "model"), StringComparison.Ordinal),
                    $"Agent '{agent.Name}' model mismatch: expected '{expectedModel}'.");
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal))
            {
                Assert.True(
                    string.Equals(modelProfile.Default, RequireScalar(frontmatter, "model"), StringComparison.Ordinal),
                    $"Agent '{agent.Name}' model mismatch: expected '{modelProfile.Default}'.");
            }
            else
            {
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                    $"Agent '{agent.Name}' should omit 'model' (profile default is inherit).");
            }

            // Permission lowering: readonly is emitted only when both write and execute are withheld
            SquadCapabilityProfile capProfile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            bool allowsWrite = capProfile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision) &&
                               writeDecision == SquadPermissionDecision.Allow;
            bool allowsExecute = capProfile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision executeDecision) &&
                                 executeDecision == SquadPermissionDecision.Allow;
            bool expectedReadOnly = !allowsWrite && !allowsExecute;

            if (expectedReadOnly)
            {
                Assert.True(
                    string.Equals("true", RequireScalar(frontmatter, "readonly"), StringComparison.Ordinal),
                    $"Agent '{agent.Name}' should carry readonly: true.");
            }
            else
            {
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("readonly")),
                    $"Agent '{agent.Name}' should omit readonly.");
            }

            // Exact comparison: the renderer appends the normalized body verbatim, so a
            // duplicated or padded body must fail, and the message names the offender.
            string expectedAgentBody = agent.InstructionBody.Replace("\r\n", "\n");
            if (!expectedAgentBody.EndsWith('\n'))
            {
                expectedAgentBody += "\n";
            }

            Assert.True(
                string.Equals(expectedAgentBody, body, StringComparison.Ordinal),
                $"Agent '{agent.Name}' body mismatch.");
        }

        // Concrete lowerings verification against loaded profiles
        SquadCapabilityProfile architectProfile = source.CapabilityProfiles.Profiles["architect"];
        Assert.Equal(SquadPermissionDecision.Ask, architectProfile.Permissions["filesystem.write"]);
        Assert.Equal(SquadPermissionDecision.Ask, architectProfile.Permissions["process.execute"]);
        AssertReadOnly(result, "architect", expectedReadOnly: true);

        SquadCapabilityProfile docProfile = source.CapabilityProfiles.Profiles["documentation"];
        Assert.Equal(SquadPermissionDecision.Allow, docProfile.Permissions["filesystem.write"]);
        AssertReadOnly(result, "docs-dev", expectedReadOnly: false);

        SquadCapabilityProfile investigatorProfile = source.CapabilityProfiles.Profiles["investigator"];
        Assert.Equal(SquadPermissionDecision.Allow, investigatorProfile.Permissions["process.execute"]);
        AssertReadOnly(result, "bug-crusher-investigator", expectedReadOnly: false);

        // Conductor and conductor-v3 are native primary agents on Cursor: present as
        // .cursor/agents/<name>.md and never duplicated as skills.
        foreach (string conductor in new[] { "conductor", "conductor-v3" })
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".cursor/agents/{conductor}.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".cursor/skills/{conductor}/SKILL.md");
        }

        // Skills verification
        foreach (SquadSkill skill in source.Skills)
        {
            bool isConductor = SuppressedSkillNames.Contains(skill.Name);
            string path = $".cursor/skills/{skill.Name}/SKILL.md";
            if (isConductor)
            {
                Assert.DoesNotContain(result.Files, f => f.RelativePath == path);
                continue;
            }

            SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == path);
            (YamlMappingNode frontmatter, string skillBody) = SplitFrontmatter(Encoding.UTF8.GetString(file.Content.Span));
            Assert.Equal(skill.Name, RequireScalar(frontmatter, "name"));
            string expectedDescription = string.Join(" ", skill.Description.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.True(
                string.Equals(expectedDescription, RequireScalar(frontmatter, "description"), StringComparison.Ordinal),
                $"Skill '{skill.Name}' description mismatch.");
            Assert.True(
                string.Equals("MIT", RequireScalar(frontmatter, "license"), StringComparison.Ordinal),
                $"Skill '{skill.Name}' license mismatch.");

            // Exact comparison with the normalized canonical body: the renderer appends it
            // verbatim, so duplication or padding must fail, naming the offender.
            string expectedSkillBody = skill.InstructionBody.Replace("\r\n", "\n");
            if (!expectedSkillBody.EndsWith('\n'))
            {
                expectedSkillBody += "\n";
            }

            Assert.True(
                string.Equals(expectedSkillBody, skillBody, StringComparison.Ordinal),
                $"Skill '{skill.Name}' body mismatch.");
        }

        // Degradations: exactly the non-all-deny agents carry 'permission-not-expressible'
        string[] expectedDegraded = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                // Mirror the renderer's governed set exactly: the renderer decides on
                // CursorRenderer's seven capabilities, not on every key the profile
                // happens to declare, so a corpus capability outside that set cannot
                // break this oracle for a reason unrelated to the renderer.
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
            Assert.True(
                string.Equals("cursor", degradation.Target, StringComparison.Ordinal),
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
        SquadRendererRegistry registry = new([new CursorRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Cursor],
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

    private static void AssertReadOnly(SquadRenderResult result, string agentName, bool expectedReadOnly)
    {
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == $".cursor/agents/{agentName}.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(Encoding.UTF8.GetString(file.Content.Span));
        if (expectedReadOnly)
        {
            Assert.True(
                string.Equals("true", RequireScalar(frontmatter, "readonly"), StringComparison.Ordinal),
                $"Agent '{agentName}' should carry readonly: true.");
        }
        else
        {
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("readonly")),
                $"Agent '{agentName}' should omit readonly.");
        }
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

    private static string RequireScalar(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            throw new InvalidOperationException($"Frontmatter is missing required key '{key}'.");
        }

        return Assert.IsType<YamlScalarNode>(value).Value
            ?? throw new InvalidOperationException($"Key '{key}' has a null scalar value.");
    }
}
