using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Validation;
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
/// and skills lower to <c>.claude/skills/&lt;name&gt;/SKILL.md</c>. Verifies profile-declared
/// shared-identity suppression, explicit tool allow-listing, model resolution from <c>models.yml</c>,
/// and structured degradation accounting for <c>safety-narrowed</c> and
/// <c>permission-not-expressible</c> codes.
///
/// <para>
/// <strong>Primary-agent entry point:</strong> A primary agent with <c>fallback: role-skill</c>
/// and profile <c>no-primary-agent: skill</c> renders as both a subagent file at
/// <c>.claude/agents/&lt;name&gt;.md</c> and an entry-point skill at
/// <c>.claude/skills/&lt;name&gt;/SKILL.md</c>, invoked as <c>/&lt;name&gt;</c> in the main conversation.
/// The subagent file is kept for enforced invocation (<c>claude --agent &lt;name&gt;</c>).
/// The skill frontmatter contains exactly <c>name</c>, <c>description</c>, and <c>license</c>;
/// its body matches the canonical agent body verbatim. The entry-point skill and its resources
/// share degradation records with the agent. Under profile <c>no-primary-agent: omit</c>,
/// no entry-point skill is emitted, and the agent's existing records remain unchanged.
/// </para>
/// </remarks>
public sealed class ClaudeRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly string[] SkillFrontmatterKeys = ["name", "description", "license"];
    private static readonly string[] PrimaryAgentDegradationCodes = ["permission-not-expressible", "role-skill-fallback"];

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
    /// Shared identities are read from the loaded fallback profile so the test follows the
    /// same generic single-projection contract as the renderer.
    /// </summary>
    private static HashSet<string> SharedIdentities(SquadSource source) =>
        source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

    public void Dispose()
    {
        // No disposable state: the suite only reads the checked-in corpus. IDisposable is
        // implemented per the test-coding-standard so adding fixtures later has a home.
    }

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
        HashSet<string> sharedIdentities = SharedIdentities(source);
        SquadRendererRegistry registry = new([new ClaudeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));

        // C3: every rendered owner also projects its validated resource closure beside its
        // principal output, so the corpus count is principals plus emitted closures.
        // A primary agent with no-primary-agent: skill contributes an entry-point skill
        // (1 + its resource closure) in addition to the agent file.
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Invocation == SquadInvocation.Primary);
        int primaryAgentSkillContribution = 1 + primaryAgent.Resources.Count;

        int expectedFileCount =
            source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + source.Skills.Count - suppressedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count)
            + primaryAgentSkillContribution;
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
            // the agent so a failure identifies the offender out of the 21-agent corpus.
            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            if (modelProfile.HarnessModels.TryGetValue("claude", out string? claudeHarnessModel))
            {
                if (string.Equals(claudeHarnessModel, "inherit", StringComparison.Ordinal))
                {
                    Assert.False(
                        frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                        $"Agent '{agent.Name}' should omit 'model' (claude harness override is inherit).");
                }
                else
                {
                    Assert.True(
                        string.Equals(claudeHarnessModel, RequireScalar(frontmatter, "model", agent.Name), StringComparison.Ordinal),
                        $"Agent '{agent.Name}' model mismatch: expected '{claudeHarnessModel}'.");
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

            // 'tools' is where the capability lattice lands on Claude. Omitting the key
            // inherits every subagent tool, so assert presence and both directions of the mapping.
            SquadCapabilityProfile capProfile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            IReadOnlyList<string> tools = RequireSequence(frontmatter, "tools", agent.Name);
            Assert.All(tools, tool => Assert.True(IsDocumentedClaudeTool(tool), $"Agent '{agent.Name}' tool '{tool}' is not in the documented vocabulary."));
            Assert.True(
                tools.Distinct(StringComparer.Ordinal).Count() == tools.Count,
                $"Agent '{agent.Name}' has duplicate tool entries: {string.Join(", ", tools)}.");

            foreach ((string capability, string[] mapped) in CapabilityToolContract)
            {
                bool allowed = capProfile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                               decision == SquadPermissionDecision.Allow;
                foreach (string tool in mapped)
                {
                    Assert.True(
                        allowed == tools.Contains(tool, StringComparer.Ordinal),
                        $"Agent '{agent.Name}' capability '{capability}' allowed={allowed} tool '{tool}'.");
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
            bool isPureOrchestrator = string.Equals(
                agent.CapabilityProfile,
                "orchestrator",
                StringComparison.Ordinal);
            bool hasRead = capProfile.Permissions.TryGetValue("filesystem.read", out SquadPermissionDecision readDecision) &&
                           readDecision == SquadPermissionDecision.Allow;
            bool expectedMcp = hasRead && !isPureOrchestrator;
            foreach (string mcpTool in new[] { "mcp__codegraph__*", "mcp__kyber-weave__*", "mcp__context7__*" })
            {
                Assert.True(
                    expectedMcp == tools.Contains(mcpTool, StringComparer.Ordinal),
                    $"Agent '{agent.Name}' mcpTool '{mcpTool}'.");
            }

            // Exact comparison: the renderer appends the normalized body verbatim, so a
            // duplicated or padded body must fail, and the message names the offender.
            string expectedAgentBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
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
        Assert.Equal(SquadPermissionDecision.Allow, architectProfile.Permissions["filesystem.write"]);
        Assert.Equal(SquadPermissionDecision.Ask, architectProfile.Permissions["process.execute"]);
        AssertTools(result, "architect", ["TodoWrite", "Skill", "Read", "mcp__codegraph__*", "mcp__kyber-weave__*", "mcp__context7__*", "Grep", "Glob", "Edit", "Write", "NotebookEdit", "WebFetch", "WebSearch", "Agent(azure-reader, research-agent)"]);

        SquadCapabilityProfile docProfile = source.CapabilityProfiles.Profiles["documentation"];
        Assert.Equal(SquadPermissionDecision.Allow, docProfile.Permissions["filesystem.write"]);
        AssertTools(result, "docs-dev", ["TodoWrite", "Skill", "Read", "mcp__codegraph__*", "mcp__kyber-weave__*", "mcp__context7__*", "Grep", "Glob", "Edit", "Write", "NotebookEdit"]);

        SquadCapabilityProfile investigatorProfile = source.CapabilityProfiles.Profiles["investigator"];
        Assert.Equal(SquadPermissionDecision.Allow, investigatorProfile.Permissions["process.execute"]);
        AssertTools(result, "bug-crusher-investigator", ["TodoWrite", "Skill", "Read", "mcp__codegraph__*", "mcp__kyber-weave__*", "mcp__context7__*", "Grep", "Glob", "Bash", "PowerShell", "WebFetch", "WebSearch"]);

        foreach (string sharedIdentity in sharedIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".claude/agents/{sharedIdentity}.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".claude/skills/{sharedIdentity}/SKILL.md");
        }

        // Skills verification
        foreach (SquadSkill skill in source.Skills)
        {
            bool isSharedIdentity = sharedIdentities.Contains(skill.Name);
            string path = $".claude/skills/{skill.Name}/SKILL.md";
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

            // Exact comparison with the normalized canonical body: the renderer appends it
            // verbatim, so duplication or padding must fail, naming the offender.
            string expectedSkillBody = skill.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedSkillBody.EndsWith('\n'))
            {
                expectedSkillBody += "\n";
            }

            Assert.True(
                string.Equals(expectedSkillBody, skillBody, StringComparison.Ordinal),
                $"Skill '{skill.Name}' body mismatch.");
        }

        // Degradations: 'ask' capabilities produce safety-narrowed; allowed network.publish
        // and delegate-allow rosters produce permission-not-expressible. An agent may carry
        // both records, and one permission-not-expressible record may combine both causes.
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
            .Where(a =>
            {
                SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                bool publishAllowed = prof.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
                                      publishDecision == SquadPermissionDecision.Allow;
                bool rosterLimited = prof.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                                     delegateDecision == SquadPermissionDecision.Allow &&
                                     a.DelegatesTo.Count > 0;
                return publishAllowed || rosterLimited;
            })
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

        foreach (SquadDegradationRecord degradation in permissionNotExpressible)
        {
            SquadAgent agent = agentsByName[degradation.CanonicalIdentity];
            SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            if (prof.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
                publishDecision == SquadPermissionDecision.Allow)
            {
                Assert.Contains(
                    "network.publish",
                    degradation.Details,
                    StringComparison.Ordinal);
            }

            if (prof.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                delegateDecision == SquadPermissionDecision.Allow &&
                agent.DelegatesTo.Count > 0)
            {
                Assert.Contains(
                    "Roster:",
                    degradation.Details,
                    StringComparison.Ordinal);
            }
        }

        foreach (SquadDegradationRecord degradation in result.Degradations)
        {
            Assert.True(
                string.Equals("claude", degradation.Target, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong target.");
            Assert.True(
                degradation.Code is "safety-narrowed" or "permission-not-expressible" or "capability-not-isolable" or "role-skill-fallback",
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

    /// <summary>
    /// Agents with process.execute: allow and filesystem.write: ask or deny (e.g. investigator and
    /// reviewer profiles) receive a capability-not-isolable degradation record naming the granted
    /// shell tools (Bash, PowerShell) and withheld write tools (Edit, NotebookEdit, Write).
    /// Agents with filesystem.write: allow or process.execute: deny do not receive this degradation.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Claude_RecordsCapabilityNotIsolableForShellImpliesWrite()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new ClaudeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        string[] grantedShellTools = ["Bash", "PowerShell"];
        string[] withheldWriteTools = ["Edit", "NotebookEdit", "Write"];

        List<string> expectedAgents = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile p = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                bool exec = p.Permissions.TryGetValue("process.execute", out SquadPermissionDecision e) && e == SquadPermissionDecision.Allow;
                bool write = p.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision w) && w == SquadPermissionDecision.Allow;
                return exec && !write;
            })
            .Select(a => a.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(expectedAgents);

        foreach (SquadAgent agent in source.Agents)
        {
            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            bool executeAllowed = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec) &&
                exec == SquadPermissionDecision.Allow;
            bool writeAllowed = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write) &&
                write == SquadPermissionDecision.Allow;

            SquadDegradationRecord? record = result.Degradations.FirstOrDefault(
                d => d.CanonicalIdentity == agent.Name && d.Code == "capability-not-isolable");

            if (executeAllowed && !writeAllowed)
            {
                Assert.NotNull(record);
                Assert.Equal("claude", record.Target);
                Assert.Equal(agent.Name, record.CanonicalIdentity);
                Assert.Equal(agent.Name, record.OutputIdentity);
                Assert.Equal(agent.BodyDigest, record.InstructionDigest);
                Assert.NotNull(record.Details);
                foreach (string shellTool in grantedShellTools)
                {
                    Assert.Contains(shellTool, record.Details, StringComparison.Ordinal);
                }
                foreach (string writeTool in withheldWriteTools)
                {
                    Assert.Contains(writeTool, record.Details, StringComparison.Ordinal);
                }
            }
            else
            {
                Assert.Null(record);
            }
        }
    }

    /// <summary>
    /// Pins the Claude model of the primary agent by value. The corpus test only checks that
    /// the renderer echoes <c>models.yml</c>, so a wrong tier there still passes it. On
    /// <c>haiku</c> the conductor answered from its own reads instead of delegating to
    /// <c>architect</c>, then reached for the Skill tool where the Agent tool was needed.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Claude_ConductorRunsOnSonnet()
    {
        SquadRendererRegistry registry = new([new ClaudeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == ".claude/agents/conductor.md");
        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(file.Content.Span),
            "conductor");
        Assert.Equal("sonnet", RequireScalar(frontmatter, "model", "conductor"));
    }

    /// <summary>
    /// Row (b): Project scope renders the primary agent as both a subagent and an entry-point
    /// skill, each with its resource closure, under the same degradation records.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Claude_ExposesThePrimaryAgentAsAnEntryPointSkillBesideItsSubagent()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Invocation == SquadInvocation.Primary);

        SquadRendererRegistry registry = new([new ClaudeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Subagent file is present (Q1).
        SquadDeploymentFile agentFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".claude/agents/{primaryAgent.Name}.md");

        // Entry-point skill is present with exact frontmatter key set: {name, description, license}.
        SquadDeploymentFile skillFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".claude/skills/{primaryAgent.Name}/SKILL.md");
        (YamlMappingNode skillFrontmatter, string skillBody) = SplitFrontmatter(
            Encoding.UTF8.GetString(skillFile.Content.Span),
            primaryAgent.Name);

        Assert.Equal(
            SkillFrontmatterKeys.OrderBy(k => k, StringComparer.Ordinal),
            skillFrontmatter.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(n => n.Value)
                .OrderBy(v => v, StringComparer.Ordinal));

        Assert.Equal(primaryAgent.Name, RequireScalar(skillFrontmatter, "name", primaryAgent.Name));
        string expectedDescription = string.Join(" ", primaryAgent.Description.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Equal(expectedDescription, RequireScalar(skillFrontmatter, "description", primaryAgent.Name));
        Assert.Equal("MIT", RequireScalar(skillFrontmatter, "license", primaryAgent.Name));

        // Body matches normalized canonical agent body.
        string expectedAgentBody = primaryAgent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!expectedAgentBody.EndsWith('\n'))
        {
            expectedAgentBody += "\n";
        }
        Assert.Equal(expectedAgentBody, skillBody);

        // Resources are projected beside the skill.
        foreach (SquadResource resource in primaryAgent.Resources)
        {
            SquadDeploymentFile resourceFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".claude/skills/{primaryAgent.Name}/{resource.RelativePath}");
            ReadOnlySpan<byte> expectedBytes = Encoding.UTF8.GetBytes(resource.Content);
            Assert.True(expectedBytes.SequenceEqual(resourceFile.Content.Span));
        }

        // Exactly one role-skill-fallback record.
        SquadDegradationRecord roleSkillFallback = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == primaryAgent.Name && d.Code == "role-skill-fallback");
        Assert.Equal(primaryAgent.Name, roleSkillFallback.OutputIdentity);
        Assert.Equal(primaryAgent.BodyDigest, roleSkillFallback.InstructionDigest);
        Assert.Contains($"/{primaryAgent.Name}", roleSkillFallback.Details, StringComparison.Ordinal);
        Assert.Contains(primaryAgent.Fallback, roleSkillFallback.Details, StringComparison.Ordinal);
        Assert.Contains("no-primary-agent: skill", roleSkillFallback.Details, StringComparison.Ordinal);

        // Exactly one permission-not-expressible record.
        SquadDegradationRecord permissionNotExpressible = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == primaryAgent.Name && d.Code == "permission-not-expressible");
        Assert.Equal(primaryAgent.Name, permissionNotExpressible.OutputIdentity);
        Assert.Equal(primaryAgent.BodyDigest, permissionNotExpressible.InstructionDigest);

        // Check capability-decision pairs in details.
        SquadCapabilityProfile agentCapProfile = source.CapabilityProfiles.Profiles[primaryAgent.CapabilityProfile];
        foreach (string capability in source.CapabilityProfiles.Capabilities)
        {
            string decision = agentCapProfile.Permissions.TryGetValue(capability, out SquadPermissionDecision perm)
                ? perm switch
                {
                    SquadPermissionDecision.Allow => "allow",
                    SquadPermissionDecision.Ask => "ask",
                    SquadPermissionDecision.Deny => "deny",
                    _ => throw new InvalidOperationException($"Unexpected permission decision '{perm}'.")
                }
                : "deny";
            Assert.Contains($"{capability}: {decision}", permissionNotExpressible.Details, StringComparison.Ordinal);
        }

        Assert.Contains("Roster:", permissionNotExpressible.Details, StringComparison.Ordinal);
        Assert.Contains($"/{primaryAgent.Name}", permissionNotExpressible.Details, StringComparison.Ordinal);
        Assert.Contains("MCP", permissionNotExpressible.Details, StringComparison.Ordinal);
        Assert.Contains($"claude --agent {primaryAgent.Name}", permissionNotExpressible.Details, StringComparison.Ordinal);

        // No other codes for this agent.
        Assert.Equal(
            PrimaryAgentDegradationCodes,
            result.Degradations
                .Where(d => d.CanonicalIdentity == primaryAgent.Name)
                .Select(d => d.Code)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal));

        // Check all relative Markdown links in skill body resolve to emitted files.
        const string linkPattern = @"\[([^\]]+)\]\(([^)]+)\)";
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(skillBody, linkPattern))
        {
            string link = match.Groups[2].Value;
            if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !link.StartsWith('#'))
            {
                string expectedPath = Path.Combine($".claude/skills/{primaryAgent.Name}", link).Replace("\\", "/", StringComparison.Ordinal);
                Assert.Contains(
                    result.Files,
                    f => f.RelativePath == expectedPath);
            }
        }

        // At most one record per (Target, CanonicalIdentity, Code).
        var recordKeys = result.Degradations
            .Select(d => (d.Target, d.CanonicalIdentity, d.Code))
            .ToList();
        Assert.Equal(recordKeys.Count, recordKeys.Distinct().Count());

        // No Details contains "widening".
        Assert.DoesNotContain(
            result.Degradations,
            d => d.Details != null && d.Details.Contains("widening", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Row (c): Global scope renders the primary agent entry-point skill under <c>skills/</c>
    /// instead of <c>.claude/skills/</c>, and the subagent under <c>agents/</c>.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Claude_GlobalScopePlacesTheEntryPointSkillUnderSkills()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Invocation == SquadInvocation.Primary);

        SquadRendererRegistry registry = new([new ClaudeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Global);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Skill file is under skills/ without .claude prefix.
        SquadDeploymentFile skillFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $"skills/{primaryAgent.Name}/SKILL.md");

        // Resources are also under skills/ without .claude prefix.
        foreach (SquadResource resource in primaryAgent.Resources)
        {
            SquadDeploymentFile resourceFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $"skills/{primaryAgent.Name}/{resource.RelativePath}");
        }

        // Subagent file is under agents/ without .claude prefix.
        SquadDeploymentFile agentFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $"agents/{primaryAgent.Name}.md");

        // No .claude/ paths.
        Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith(".claude/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Row (d): The no-primary-agent setting switches only the entry-point skill. Both skill and
    /// omit modes keep the agent file identical, and differ only in whether the skill and its
    /// resources are emitted.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Claude_NoPrimaryAgentValueSwitchesOnlyTheEntryPointSkill()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Invocation == SquadInvocation.Primary);

        SquadRendererRegistry registry = new([new ClaudeRenderer()]);

        // Render with skill mode (default).
        SquadRenderRequest skillRequest = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project);
        SquadRenderResult skillResult = await registry.RenderAsync(skillRequest);
        Assert.True(skillResult.Success);

        // Render with omit mode.
        using (var omitFixture = ClaudeNoPrimaryAgentFixture.Create("omit"))
        {
            SquadRenderRequest omitRequest = new(
                SourceDirectory: omitFixture.ProductRoot,
                Targets: [SquadTarget.Claude],
                Scope: SquadDeploymentScope.Project);
            SquadRenderResult omitResult = await registry.RenderAsync(omitRequest);
            Assert.True(omitResult.Success);

            // Omit mode has no .claude/skills/<name>/ paths.
            Assert.DoesNotContain(
                omitResult.Files,
                f => f.RelativePath.StartsWith($".claude/skills/{primaryAgent.Name}/", StringComparison.Ordinal));

            // All omit-mode paths appear in skill mode.
            foreach (SquadDeploymentFile omitFile in omitResult.Files)
            {
                Assert.Contains(skillResult.Files, sf => sf.RelativePath == omitFile.RelativePath);
            }

            // Skill-mode paths minus omit-mode paths equal exactly the skill and its resources.
            HashSet<string> skillPaths = new(skillResult.Files.Select(f => f.RelativePath));
            HashSet<string> omitPaths = new(omitResult.Files.Select(f => f.RelativePath));
            skillPaths.ExceptWith(omitPaths);

            string[] expectedSkillPaths = new string[1 + primaryAgent.Resources.Count];
            expectedSkillPaths[0] = $".claude/skills/{primaryAgent.Name}/SKILL.md";
            for (int i = 0; i < primaryAgent.Resources.Count; i++)
            {
                expectedSkillPaths[i + 1] = $".claude/skills/{primaryAgent.Name}/{primaryAgent.Resources[i].RelativePath}";
            }

            Assert.Equal(
                expectedSkillPaths.OrderBy(p => p, StringComparer.Ordinal),
                skillPaths.OrderBy(p => p, StringComparer.Ordinal));

            // Agent file bytes are identical.
            SquadDeploymentFile skillAgentFile = Assert.Single(
                skillResult.Files,
                f => f.RelativePath == $".claude/agents/{primaryAgent.Name}.md");
            SquadDeploymentFile omitAgentFile = Assert.Single(
                omitResult.Files,
                f => f.RelativePath == $".claude/agents/{primaryAgent.Name}.md");
            Assert.True(skillAgentFile.Content.Span.SequenceEqual(omitAgentFile.Content.Span));

            // Omit mode: one permission-not-expressible (no role-skill-fallback, no omitted).
            IReadOnlyList<SquadDegradationRecord> omitPermissionNotExpressible = omitResult.Degradations
                .Where(d => d.CanonicalIdentity == primaryAgent.Name && d.Code == "permission-not-expressible")
                .ToArray();
            Assert.Single(omitPermissionNotExpressible);
            Assert.Contains(
                "Roster:",
                omitPermissionNotExpressible[0].Details,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"/{primaryAgent.Name}",
                omitPermissionNotExpressible[0].Details,
                StringComparison.Ordinal);

            // Omit mode has no role-skill-fallback.
            Assert.DoesNotContain(
                omitResult.Degradations,
                d => d.CanonicalIdentity == primaryAgent.Name && d.Code == "role-skill-fallback");
        }
    }

    /// <summary>
    /// Row (e): The renderer throws when a canonical skill has the same name as the primary agent,
    /// before any file is produced.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Claude_ThrowsWhenACanonicalSkillOccupiesTheEntryPointIdentity()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Invocation == SquadInvocation.Primary);

        using (var collisionFixture = PiPrimaryIdentityCollisionFixture.Create(primaryAgent.Name))
        {
            ClaudeRenderer renderer = new();
            SquadRenderRequest request = new(
                SourceDirectory: collisionFixture.ProductRoot,
                Targets: [SquadTarget.Claude],
                Scope: SquadDeploymentScope.Project);

            SquadRenderValidationException ex = await Assert.ThrowsAsync<SquadRenderValidationException>(
                () => renderer.RenderAsync(request));
            Assert.Contains(primaryAgent.Name, ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Row (f): Regression guard. The loader rejects unsupported no-primary-agent values,
    /// so this test passes before implementation and documents that the renderer also fails closed.
    /// RED waived.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Claude_FailsClosedOnAnUnsupportedNoPrimaryAgentValue()
    {
        using (var renameFixture = ClaudeNoPrimaryAgentFixture.Create("rename"))
        {
            ClaudeRenderer renderer = new();
            SquadRenderRequest request = new(
                SourceDirectory: renameFixture.ProductRoot,
                Targets: [SquadTarget.Claude],
                Scope: SquadDeploymentScope.Project);

            SquadSourceValidationException ex = await Assert.ThrowsAsync<SquadSourceValidationException>(() => renderer.RenderAsync(request));
            Assert.Contains("no-primary-agent", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task RenderAsync_Claude_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new ClaudeRenderer(),
            SquadTarget.Claude,
            ".claude/agents/bug-crusher-investigator.md",
            ".claude/agents",
            ".claude/skills");
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

/// <summary>
/// Copies the real <c>products/kyber-squad</c> corpus and modifies the <c>no-primary-agent</c>
/// value in the <c>role-skill</c> fallback profile to a different value (e.g., "omit" or an
/// unsupported value like "rename"), so contract tests can validate rendering behavior under
/// different profile configurations without editing the canonical corpus.
/// </summary>
internal sealed class ClaudeNoPrimaryAgentFixture : IDisposable
{
    private readonly TempDirectory _temp = new();

    private ClaudeNoPrimaryAgentFixture()
    {
        ProductRoot = Path.Combine(_temp.Path, "kyber-squad");
    }

    internal string ProductRoot { get; }

    internal static ClaudeNoPrimaryAgentFixture Create(string noPrimaryAgentValue)
    {
        ClaudeNoPrimaryAgentFixture fixture = new();
        string canonicalRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
        PiCorpusFixtureHelpers.CopyDirectory(canonicalRoot, fixture.ProductRoot);

        string fallbacksPath = Path.Combine(fixture.ProductRoot, "profiles", "fallbacks.yml");
        string original = File.ReadAllText(fallbacksPath);

        // Replace "no-primary-agent: skill" with "no-primary-agent: <value>"
        string mutated = original.Replace(
            "no-primary-agent: skill",
            $"no-primary-agent: {noPrimaryAgentValue}",
            StringComparison.Ordinal);

        if (string.Equals(original, mutated, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected '{fallbacksPath}' to contain 'no-primary-agent: skill' to replace.");
        }

        File.WriteAllText(fallbacksPath, mutated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fixture;
    }

    public void Dispose() => _temp.Dispose();
}
