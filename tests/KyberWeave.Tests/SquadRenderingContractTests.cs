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

    /// <summary>
    /// Copilot's built-in tool vocabulary, transcribed from GitHub's custom-agent
    /// configuration reference. A tool outside this set would be silently ignored by the
    /// harness, turning an intended grant into a missing capability at runtime.
    /// </summary>
    private static readonly string[] DocumentedCopilotTools =
        ["execute", "read", "edit", "search", "agent", "web", "todo"];

    /// <summary>
    /// The capability→tool lowering this suite pins. Declared independently of the renderer
    /// so a change to either side has to be made deliberately in both.
    /// <c>network.publish</c> is absent because no built-in tool publishes.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityToolContract =
    [
        ("process.execute", ["execute"]),
        ("filesystem.read", ["read"]),
        ("filesystem.search", ["search"]),
        ("filesystem.write", ["edit"]),
        ("delegate", ["agent"]),
        ("network.read", ["web"]),
    ];

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
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // 20 agents + (25 skills - conductor - conductor-v3, suppressed by the native
        // single-projection rule) = 43.
        Assert.Equal(source.Agents.Count + source.Skills.Count - 2, result.Files.Count);
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

            // 'tools' is where the capability lattice actually lands on Copilot. Omitting the
            // key means "all available tools", so an absent or over-full list is a silent
            // grant of everything — assert presence *and* both directions of the mapping.
            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            IReadOnlyList<string> tools = RequireSequence(frontmatter, "tools");
            Assert.All(tools, tool => Assert.Contains(tool, DocumentedCopilotTools));
            Assert.Equal(tools.Distinct(StringComparer.Ordinal).Count(), tools.Count);

            foreach ((string capability, string[] mapped) in CapabilityToolContract)
            {
                bool allowed = profile.Permissions[capability] == SquadPermissionDecision.Allow;
                foreach (string tool in mapped)
                {
                    Assert.Equal(allowed, tools.Contains(tool, StringComparer.Ordinal));
                }
            }

            // 'todo' lowers from no capability, so every agent keeps it. Together with the
            // two checks above this pins the emitted set exactly: it is the only documented
            // built-in that CapabilityToolContract does not cover.
            Assert.Contains("todo", tools);

            string body = ReadBody(file);
            Assert.True(body.Length <= 30_000, $"'{agent.Name}' body is {body.Length} characters; Copilot caps agent files at 30,000.");
            Assert.Contains(agent.InstructionBody.Trim(), body, StringComparison.Ordinal);
        }

        // Concrete lowerings, so a capability→tool map change is caught here even if the
        // renderer and the profiles drift together.
        AssertTools(result, "research-agent", ["read", "search", "web", "todo"]);
        // The orchestrator profile is the reason filesystem.search exists separately: the
        // conductor may open a plan it is pointed at, but never sweep the tree for one.
        AssertTools(result, "conductor", ["read", "agent", "todo"]);
        AssertTools(result, "conductor-v3", ["read", "agent", "todo"]);
        AssertTools(result, "github-devops", ["execute", "read", "edit", "search", "web", "todo"]);

        // Conductor and conductor-v3 are native primary agents on Copilot: present as
        // .agent.md, and never duplicated as a skill (the single-projection rule).
        foreach (string conductor in new[] { "conductor", "conductor-v3" })
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".github/agents/{conductor}.agent.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".github/skills/{conductor}/SKILL.md");
        }

        foreach (SquadSkill skill in source.Skills)
        {
            bool isConductor = skill.Name is "conductor" or "conductor-v3";
            string path = $".github/skills/{skill.Name}/SKILL.md";
            if (isConductor)
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
            .Where(a => source.CapabilityProfiles.Profiles[a.CapabilityProfile]
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

        // Model resolution: conductor/conductor-v3 use profiles with no copilot-specific
        // entry and default: inherit, so their agent files omit 'model' entirely.
        foreach (string orchestrator in new[] { "conductor", "conductor-v3" })
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".github/agents/{orchestrator}.agent.md");
            YamlMappingNode frontmatter = ReadFrontmatter(file);
            Assert.False(frontmatter.Children.ContainsKey(new YamlScalarNode("model")));
        }

        // Every other agent's profile does carry a copilot-specific model — resolved from
        // the real profiles/models.yml, not a hardcoded expectation here.
        foreach (SquadAgent agent in source.Agents.Where(a => a.Name is not ("conductor" or "conductor-v3")))
        {
            SquadModelProfile profile = source.ModelProfiles.Profiles[agent.ModelProfile];
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".github/agents/{agent.Name}.agent.md");
            YamlMappingNode frontmatter = ReadFrontmatter(file);
            Assert.Equal(profile.HarnessModels["copilot"], RequireScalar(frontmatter, "model"));
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

    private static IReadOnlyList<string> RequireSequence(YamlMappingNode node, string key)
    {
        YamlNode value = node.Children[new YamlScalarNode(key)];
        YamlSequenceNode sequence = Assert.IsType<YamlSequenceNode>(value);
        return sequence.Children
            .Select(child => Assert.IsType<YamlScalarNode>(child).Value
                ?? throw new InvalidOperationException($"Key '{key}' has a null sequence entry."))
            .ToArray();
    }

    private static void AssertTools(SquadRenderResult result, string agent, string[] expected)
    {
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == $".github/agents/{agent}.agent.md");

        Assert.Equal(expected, RequireSequence(ReadFrontmatter(file), "tools"));
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
