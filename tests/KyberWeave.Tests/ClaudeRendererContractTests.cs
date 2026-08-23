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
/// <see cref="SquadRendererRegistry"/> with <see cref="ClaudeRenderer"/> and validates the result
/// against Claude Code's documented subagent and skill contracts.
/// </summary>
/// <remarks>
/// Validates that canonical agents lower to Claude subagents at <c>.claude/agents/&lt;name&gt;.md</c>
/// and skills lower to <c>.claude/skills/&lt;name&gt;/SKILL.md</c>. Verifies single-projection suppression
/// for primary conductors, explicit tool allow-listing, model resolution from <c>models.yml</c>,
/// and structured degradation accounting for <c>safety-narrowed</c> and
/// <c>permission-not-expressible</c> codes.
/// </remarks>
public sealed class ClaudeRendererContractTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Claude Code's built-in tool vocabulary, transcribed from code.claude.com/docs/en/sub-agents
    /// on 2026-08-23. A tool outside this set would be silently ignored by the harness, turning
    /// an intended grant into a missing capability at runtime. <c>Agent(roster)</c> forms are
    /// validated separately because the roster is agent-specific.
    /// </summary>
    private static readonly string[] DocumentedClaudeTools =
    [
        "TodoWrite",
        "Skill",
        "Read",
        "Grep",
        "Glob",
        "Edit",
        "Write",
        "NotebookEdit",
        "Bash",
        "PowerShell",
        "WebFetch",
        "WebSearch",
        "Agent",
        "mcp__codegraph__*",
        "mcp__kyber-weave__*",
        "mcp__context7__*"
    ];

    /// <summary>
    /// The capability→tool lowering this suite pins. Declared independently of the renderer
    /// so a change to either side has to be made deliberately in both.
    /// <c>network.publish</c> is absent because no built-in publish tool exists.
    /// <c>delegate</c> is handled separately so a non-empty
    /// <see cref="SquadAgent.DelegatesTo"/> can emit <c>Agent(roster)</c>.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityToolContract =
    [
        ("filesystem.read", ["Read"]),
        ("filesystem.search", ["Grep", "Glob"]),
        ("filesystem.write", ["Edit", "Write", "NotebookEdit"]),
        ("process.execute", ["Bash", "PowerShell"]),
        ("network.read", ["WebFetch", "WebSearch"]),
    ];

    /// <summary>
    /// Primary agents are emitted as native subagents, so their same-named canonical skills
    /// are suppressed by the single-projection rule. Keep the set in one place for both the
    /// expected file count and per-skill assertions.
    /// </summary>
    private static readonly HashSet<string> SuppressedSkillNames = ["conductor", "conductor-v3"];

    [Fact]
    public void SupportedTargets_IsExactlyClaude()
    {
        SquadRendererRegistry registry = new([new ClaudeRenderer()]);

        Assert.Equal([SquadTarget.Claude], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new ClaudeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude, SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("cursor", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonClaudeTarget()
    {
        ClaudeRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Claude_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new ClaudeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Native primary agents suppress their same-named skills; derive the expected count
        // from the centralized suppression set rather than a literal subtraction.
        int expectedFileCount = source.Agents.Count + source.Skills.Count - SuppressedSkillNames.Count;
        Assert.Equal(expectedFileCount, result.Files.Count);
        Assert.All(result.Files, f => Assert.Equal("claude", f.Target));

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".claude/agents/{agent.Name}.md");

            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent.Name);
            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name", agent.Name));
            Assert.True(
                string.Equals(agent.Description, RequireScalar(frontmatter, "description", agent.Name), StringComparison.Ordinal),
                $"Agent '{agent.Name}' description mismatch.");

            // Model resolution: verify against loaded ModelProfiles. Every assertion names
            // the agent so a failure identifies the offender out of the 22-agent corpus.
            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            if (modelProfile.HarnessModels.TryGetValue("claude", out string? expectedModel))
            {
                Assert.True(
                    string.Equals(expectedModel, RequireScalar(frontmatter, "model", agent.Name), StringComparison.Ordinal),
                    $"Agent '{agent.Name}' model mismatch: expected '{expectedModel}'.");
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

            // 'tools' is where the capability lattice lands on Claude. Omitting the key
            // inherits every subagent tool, so assert presence and both directions of the mapping.
            SquadCapabilityProfile capProfile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            IReadOnlyList<string> tools = RequireSequence(frontmatter, "tools", agent.Name);
            Assert.All(tools, tool => Assert.True(IsDocumentedClaudeTool(tool), $"Agent '{agent.Name}' tool '{tool}' is not in the documented vocabulary."));
            Assert.Equal(tools.Distinct(StringComparer.Ordinal).Count(), tools.Count);

            foreach ((string capability, string[] mapped) in CapabilityToolContract)
            {
                bool allowed = capProfile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                               decision == SquadPermissionDecision.Allow;
                foreach (string tool in mapped)
                {
                    Assert.Equal(allowed, tools.Contains(tool, StringComparer.Ordinal));
                }
            }

            // TodoWrite and Skill lower from no capability, so every agent keeps them.
            Assert.Contains("TodoWrite", tools);
            Assert.Contains("Skill", tools);

            bool delegateAllowed = capProfile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                                   delegateDecision == SquadPermissionDecision.Allow;
            if (delegateAllowed)
            {
                if (agent.DelegatesTo.Count > 0)
                {
                    string expectedAgentTool = $"Agent({string.Join(", ", agent.DelegatesTo)})";
                    Assert.Contains(
                        tools,
                        tool => string.Equals(tool, expectedAgentTool, StringComparison.Ordinal));
                }
                else
                {
                    Assert.Contains("Agent", tools);
                }
            }
            else
            {
                Assert.DoesNotContain(tools, tool => tool.StartsWith("Agent", StringComparison.Ordinal));
            }

            // MCP wildcards are granted if filesystem.read is allowed AND the agent is not a pure orchestrator.
            bool isPureOrchestrator =
                string.Equals(agent.CapabilityProfile, "orchestrator", StringComparison.Ordinal) ||
                SuppressedSkillNames.Contains(agent.Name);
            bool hasRead = capProfile.Permissions.TryGetValue("filesystem.read", out SquadPermissionDecision readDecision) &&
                           readDecision == SquadPermissionDecision.Allow;
            bool expectedMcp = hasRead && !isPureOrchestrator;
            Assert.Equal(expectedMcp, tools.Contains("mcp__codegraph__*", StringComparer.Ordinal));
            Assert.Equal(expectedMcp, tools.Contains("mcp__kyber-weave__*", StringComparer.Ordinal));
            Assert.Equal(expectedMcp, tools.Contains("mcp__context7__*", StringComparer.Ordinal));

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
        AssertTools(result, "architect", ["TodoWrite", "Skill", "Read", "mcp__codegraph__*", "mcp__kyber-weave__*", "mcp__context7__*", "Grep", "Glob", "WebFetch", "WebSearch", "Agent(azure-reader, research-agent)"]);

        SquadCapabilityProfile docProfile = source.CapabilityProfiles.Profiles["documentation"];
        Assert.Equal(SquadPermissionDecision.Allow, docProfile.Permissions["filesystem.write"]);
        AssertTools(result, "docs-dev", ["TodoWrite", "Skill", "Read", "mcp__codegraph__*", "mcp__kyber-weave__*", "mcp__context7__*", "Grep", "Glob", "Edit", "Write", "NotebookEdit"]);

        SquadCapabilityProfile investigatorProfile = source.CapabilityProfiles.Profiles["investigator"];
        Assert.Equal(SquadPermissionDecision.Allow, investigatorProfile.Permissions["process.execute"]);
        AssertTools(result, "bug-crusher-investigator", ["TodoWrite", "Skill", "Read", "mcp__codegraph__*", "mcp__kyber-weave__*", "mcp__context7__*", "Grep", "Glob", "Bash", "PowerShell", "WebFetch", "WebSearch"]);

        // Conductor and conductor-v3 are native primary agents on Claude: present as
        // .claude/agents/<name>.md and never duplicated as skills.
        foreach (string conductor in SuppressedSkillNames)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".claude/agents/{conductor}.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".claude/skills/{conductor}/SKILL.md");
        }

        // Skills verification
        foreach (SquadSkill skill in source.Skills)
        {
            bool isConductor = SuppressedSkillNames.Contains(skill.Name);
            string path = $".claude/skills/{skill.Name}/SKILL.md";
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
            Assert.True(
                string.Equals(expectedDescription, RequireScalar(frontmatter, "description", skill.Name), StringComparison.Ordinal),
                $"Skill '{skill.Name}' description mismatch.");
            Assert.True(
                string.Equals("MIT", RequireScalar(frontmatter, "license", skill.Name), StringComparison.Ordinal),
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

        // Degradations: 'ask' capabilities produce safety-narrowed; non-empty DelegatesTo
        // produces permission-not-expressible. An agent may carry both records.
        string[] expectedSafetyNarrowed = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                return prof.Permissions.Values.Any(decision => decision == SquadPermissionDecision.Ask);
            })
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] expectedPermissionNotExpressible = source.Agents
            .Where(a => a.DelegatesTo.Count > 0)
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedSafetyNarrowed);
        Assert.NotEmpty(expectedPermissionNotExpressible);

        IReadOnlyList<SquadDegradationRecord> safetyNarrowed = result.Degradations
            .Where(d => string.Equals(d.Code, "safety-narrowed", StringComparison.Ordinal))
            .ToArray();
        IReadOnlyList<SquadDegradationRecord> permissionNotExpressible = result.Degradations
            .Where(d => string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            expectedSafetyNarrowed,
            safetyNarrowed.Select(d => d.CanonicalIdentity).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(
            expectedPermissionNotExpressible,
            permissionNotExpressible.Select(d => d.CanonicalIdentity).OrderBy(n => n, StringComparer.Ordinal));

        foreach (SquadDegradationRecord degradation in result.Degradations)
        {
            Assert.True(
                string.Equals("claude", degradation.Target, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong target.");
            Assert.True(
                degradation.Code is "safety-narrowed" or "permission-not-expressible",
                $"Degradation for '{degradation.CanonicalIdentity}' has an unexpected code '{degradation.Code}'.");
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
        SquadRendererRegistry registry = new([new ClaudeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude],
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

    private static bool IsDocumentedClaudeTool(string tool) =>
        DocumentedClaudeTools.Contains(tool, StringComparer.Ordinal) ||
        (tool.StartsWith("Agent(", StringComparison.Ordinal) && tool.EndsWith(')'));

    private static void AssertTools(SquadRenderResult result, string agentName, string[] expected)
    {
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == $".claude/agents/{agentName}.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(file.Content.Span),
            agentName);
        Assert.Equal(expected, RequireSequence(frontmatter, "tools", agentName));
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

    private static IReadOnlyList<string> RequireSequence(YamlMappingNode node, string key, string identity)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"'{identity}' frontmatter is missing required key '{key}'. Present keys: {presentKeys}.");
        }

        YamlSequenceNode sequence = Assert.IsType<YamlSequenceNode>(value);
        return sequence.Children
            .Select(child => Assert.IsType<YamlScalarNode>(child).Value
                ?? throw new InvalidOperationException($"'{identity}' key '{key}' has a null sequence entry."))
            .ToArray();
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
