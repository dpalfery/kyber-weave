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
/// <see cref="SquadRendererRegistry"/> with <see cref="DevinRenderer"/> and validates the
/// result against the Devin Desktop rendering contract.
/// </summary>
/// <remarks>
/// <para>
/// Devin Desktop (Cognition's desktop app, formerly Windsurf) runs the Devin Local agent,
/// which uses the Devin CLI's discovery and file formats. Subagent-invocation agents render as
/// <c>.devin/agents/&lt;name&gt;/AGENT.md</c> — the directory layout, so an agent's resource
/// closure sits inside its own directory and authored links resolve verbatim. The one
/// <c>invocation: primary</c> agent (<c>conductor</c>) lowers to
/// <c>.devin/skills/&lt;name&gt;/SKILL.md</c>, because Devin has no primary-agent primitive
/// (Devin Local is the only top-level agent). Canonical skills render at
/// <c>.devin/skills/&lt;name&gt;/SKILL.md</c>, never under <c>.windsurf/skills/</c>, which is an
/// import from another tool rather than Devin's own root.
/// </para>
/// <para>
/// The capability→tool contract below is transcribed independently from Devin's core tool
/// names (the <c>lifecycle-hooks</c> reference) rather than read from the renderer, so a
/// renderer edit that silently widens or narrows a grant fails here. So is the model table:
/// the pinned <c>devin:</c> values are an owner decision, and a change to them should be a
/// deliberate edit in two places.
/// </para>
/// </remarks>
public sealed class DevinRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly (string Capability, string[] Tools)[] CapabilityToolContract =
    [
        ("filesystem.read", ["read", "notebook_read"]),
        ("filesystem.search", ["grep", "glob"]),
        ("filesystem.write", ["edit", "write", "apply_patch", "notebook_edit"]),
        ("process.execute", ["exec", "get_output", "write_to_process", "kill_shell"]),
        ("network.read", ["webfetch", "web_search"]),
    ];

    /// <summary>
    /// Granted on every subagent: <c>todo_write</c> touches no file, and without <c>skill</c> the
    /// deployed skill tree is unreachable.
    /// </summary>
    private static readonly string[] UngovernedTools = ["todo_write", "skill"];

    private static readonly string[] ToolOrder =
    [
        "todo_write", "skill", "read", "notebook_read", "grep", "glob", "edit", "write",
        "apply_patch", "notebook_edit", "exec", "get_output", "write_to_process", "kill_shell",
        "webfetch", "web_search"
    ];

    private static readonly string[] ApprovedAgentKeys = ["name", "description", "model", "allowed-tools"];

    /// <summary>
    /// A Devin skill's <c>allowed-tools</c> and <c>permissions.allow</c> auto-approve rather than
    /// restrict, and <c>permissions.deny</c> on the lowered conductor might reach the subagents
    /// it dispatches (ADR 0025), so no rendered skill carries a tool or permission key.
    /// </summary>
    private static readonly string[] ApprovedSkillKeys = ["name", "description"];

    /// <summary>
    /// The lowered primary agent adds <c>triggers: [user]</c>: Devin Cloud discovers the same
    /// skill tree but loads no custom subagents, so the conductor must never start on a
    /// description match.
    /// </summary>
    private static readonly string[] ApprovedLoweredSkillKeys = ["name", "description", "triggers"];

    private static readonly string[] UserOnlyTriggers = ["user"];

    /// <summary>
    /// The owner-approved Devin model per canonical model profile (ADR 0025). Exact model ids,
    /// effort included, rather than family aliases that float to a newer model and price.
    /// </summary>
    private static readonly Dictionary<string, string> ExpectedDevinModels = new(StringComparer.Ordinal)
    {
        ["architect"] = "claude-opus-5-5-high",
        ["deep-planning"] = "claude-opus-5-5-high",
        ["fast"] = "deepseek-v4-1-flash-high",
        ["general"] = "swe-2-high",
        ["reviewer"] = "grok-4-7-high"
    };

    public void Dispose()
    {
        // No disposable state: the suite reads the checked-in corpus, plus per-test fixture
        // copies disposed locally via `using`.
    }

    [Fact]
    public void SupportedTargets_IsExactlyDevin()
    {
        SquadRendererRegistry registry = new([new DevinRenderer()]);

        Assert.Equal([SquadTarget.Devin], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonDevinTarget()
    {
        DevinRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.ZCode],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Devin_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = SharedIdentities(source);

        SquadRenderResult result = await RenderDevinAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));
        int expectedFileCount =
            source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + source.Skills.Count - suppressedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);

        Assert.Equal(expectedFileCount, result.Files.Count);
        Assert.All(result.Files, f => Assert.Equal("devin", f.Target));
        Assert.All(result.Files, f => Assert.True(
            f.RelativePath.StartsWith(".devin/agents/", StringComparison.Ordinal) ||
            f.RelativePath.StartsWith(".devin/skills/", StringComparison.Ordinal),
            $"File '{f.RelativePath}' is outside .devin/agents/ and .devin/skills/."));

        Assert.DoesNotContain(
            result.Files,
            f => f.RelativePath.Contains("role-", StringComparison.OrdinalIgnoreCase));

        // The renderer owns agents and skills only: the runtime's own configuration, hooks, MCP
        // servers, and rules are the operator's, and the legacy Windsurf root is never written.
        string[] operatorOwned =
        [
            ".devin/config.json",
            ".devin/config.local.json",
            ".devin/mcp_config.json",
            ".devin/hooks.v1.json",
            ".devin/global_rules.md"
        ];
        Assert.DoesNotContain(result.Files, f => operatorOwned.Contains(f.RelativePath, StringComparer.Ordinal));
        Assert.DoesNotContain(
            result.Files,
            f => f.RelativePath.StartsWith(".devin/rules/", StringComparison.Ordinal) ||
                 f.RelativePath.StartsWith(".windsurf/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Devin_EachSubagentHasCanonicalFrontmatterAndBody()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderDevinAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        IReadOnlyList<string> mcpTools = QualifiedMcpTools(source);
        Assert.NotEmpty(mcpTools);

        foreach (SquadAgent agent in source.Agents.Where(a => a.Invocation == SquadInvocation.Subagent))
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".devin/agents/{agent.Name}/AGENT.md");

            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent.Name);

            string[] keys = FrontmatterKeys(frontmatter);
            Assert.All(keys, key => Assert.Contains(key, ApprovedAgentKeys));

            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name", agent.Name));
            Assert.Equal(
                CollapseToSingleLine(agent.Description),
                RequireScalar(frontmatter, "description", agent.Name));

            // Every subagent's profile pins a Devin model: an unpinned custom subagent runs on
            // the router-chosen default subagent model, not the parent's.
            Assert.Equal(
                ExpectedDevinModels[agent.ModelProfile],
                RequireScalar(frontmatter, "model", agent.Name));

            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.DevinCapabilityProfile ?? agent.CapabilityProfile];
            IReadOnlyList<string> expectedTools =
            [
                .. ComputeExpectedBuiltInTools(profile),
                .. ExpectsMcp(agent, profile) ? mcpTools : []
            ];
            Assert.Equal(expectedTools, RequireSequence(frontmatter, "allowed-tools", agent.Name));

            Assert.Equal(NormalizeBody(agent.InstructionBody), body);
        }
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("product-owner")]
    public async Task RenderAsync_Devin_AuthoringRolesReceiveExecutionTools(string agentName)
    {
        SquadRenderResult result = await RenderDevinAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == $".devin/agents/{agentName}/AGENT.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(file.Content.Span),
            agentName);

        string[] allowedTools = RequireSequence(frontmatter, "allowed-tools", agentName);
        Assert.Contains("exec", allowedTools);
        Assert.Contains("get_output", allowedTools);
        Assert.Contains("write_to_process", allowedTools);
        Assert.Contains("kill_shell", allowedTools);

        Assert.DoesNotContain(
            result.Degradations,
            d => d.CanonicalIdentity == agentName && d.Code == "safety-narrowed");
    }

    /// <summary>
    /// Devin reads an agent from either <c>agents/&lt;name&gt;.md</c> or
    /// <c>agents/&lt;name&gt;/AGENT.md</c>. Mixing the two for one name is ambiguous, so the
    /// renderer uses the directory layout only, and anything else inside an agent's directory
    /// sits one level deeper, beneath <c>&lt;name&gt;/</c>, where it cannot be mistaken for a
    /// definition file.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Devin_AgentsDirectoryHoldsOnlyAgentDirectories()
    {
        SquadRenderResult result = await RenderDevinAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadDeploymentFile file in result.Files.Where(
            f => f.RelativePath.StartsWith(".devin/agents/", StringComparison.Ordinal)))
        {
            string[] segments = file.RelativePath[".devin/agents/".Length..].Split('/');
            Assert.True(
                segments.Length >= 2,
                $"'{file.RelativePath}' is a flat agent file; Devin agents render as <name>/AGENT.md.");

            if (segments.Length == 2)
            {
                Assert.Equal("AGENT.md", segments[1]);
            }
            else
            {
                Assert.Equal(segments[0], segments[1]);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_Devin_LowersThePrimaryAgentToASkill()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderDevinAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadAgent conductor = Assert.Single(source.Agents, a => a.Invocation == SquadInvocation.Primary);

        Assert.DoesNotContain(
            result.Files,
            f => f.RelativePath.StartsWith($".devin/agents/{conductor.Name}/", StringComparison.Ordinal));

        SquadDeploymentFile skillFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".devin/skills/{conductor.Name}/SKILL.md");
        (YamlMappingNode frontmatter, string body) = SplitFrontmatter(
            Encoding.UTF8.GetString(skillFile.Content.Span),
            conductor.Name);

        Assert.Equal(ApprovedLoweredSkillKeys, FrontmatterKeys(frontmatter));
        Assert.Equal(conductor.Name, RequireScalar(frontmatter, "name", conductor.Name));
        Assert.Equal(UserOnlyTriggers, RequireSequence(frontmatter, "triggers", conductor.Name));
        Assert.Equal(NormalizeBody(conductor.InstructionBody), body);

        foreach (SquadResource resource in conductor.Resources)
        {
            Assert.Single(
                result.Files,
                f => f.RelativePath == $".devin/skills/{conductor.Name}/{resource.RelativePath}");
        }
    }

    [Fact]
    public async Task RenderAsync_Devin_SkillsNeverCarryToolOrPermissionKeys()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> loweredIdentities = source.Agents
            .Where(a => a.Invocation == SquadInvocation.Primary)
            .Select(a => a.Name)
            .ToHashSet(StringComparer.Ordinal);

        SquadRenderResult result = await RenderDevinAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        IReadOnlyList<SquadDeploymentFile> skills = result.Files
            .Where(f => Regex.IsMatch(f.RelativePath, @"^\.devin/skills/[^/]+/SKILL\.md$"))
            .ToArray();
        Assert.NotEmpty(skills);

        foreach (SquadDeploymentFile skill in skills)
        {
            (YamlMappingNode frontmatter, _) = SplitFrontmatter(
                Encoding.UTF8.GetString(skill.Content.Span),
                skill.RelativePath);
            Assert.Equal(
                loweredIdentities.Contains(skill.RelativePath.Split('/')[2]) ? ApprovedLoweredSkillKeys : ApprovedSkillKeys,
                FrontmatterKeys(frontmatter));
        }
    }

    [Fact]
    public async Task RenderAsync_Devin_RecordsDegradationsPerContract()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderDevinAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        Assert.All(result.Degradations, d => Assert.Equal("devin", d.Target));

        foreach (SquadAgent agent in source.Agents)
        {
            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.DevinCapabilityProfile ?? agent.CapabilityProfile];
            string[] actualCodes = result.Degradations
                .Where(d => d.CanonicalIdentity == agent.Name && d.Code != "capability-not-isolable")
                .Select(d => d.Code)
                .Order(StringComparer.Ordinal)
                .ToArray();

            List<string> expectedCodes = [];
            if (agent.Invocation == SquadInvocation.Primary)
            {
                expectedCodes.Add("permission-not-expressible");
                expectedCodes.Add("role-skill-fallback");
            }
            else
            {
                if (profile.Permissions.Values.Any(d => d == SquadPermissionDecision.Ask))
                {
                    expectedCodes.Add("safety-narrowed");
                }

                if (IsAllowed(profile, "network.publish") ||
                    IsAllowed(profile, "delegate") ||
                    !ExpectsMcp(agent, profile))
                {
                    expectedCodes.Add("permission-not-expressible");
                }
            }

            Assert.True(
                expectedCodes.Order(StringComparer.Ordinal).SequenceEqual(actualCodes),
                $"Agent '{agent.Name}' degradations [{string.Join(", ", actualCodes)}] do not match " +
                $"the contract [{string.Join(", ", expectedCodes.Order(StringComparer.Ordinal))}].");

            if (agent.Invocation == SquadInvocation.Subagent && IsAllowed(profile, "delegate"))
            {
                SquadDegradationRecord record = Assert.Single(
                    result.Degradations,
                    d => d.CanonicalIdentity == agent.Name && d.Code == "permission-not-expressible");
                foreach (string delegateName in agent.DelegatesTo)
                {
                    Assert.Contains(delegateName, record.Details, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public async Task RenderAsync_Devin_RecordsCapabilityNotIsolableForShellImpliesWrite()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderDevinAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadAgent[] shellWithoutWrite = source.Agents
            .Where(a => a.Invocation == SquadInvocation.Subagent)
            .Where(a =>
            {
                SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[a.DevinCapabilityProfile ?? a.CapabilityProfile];
                return IsAllowed(profile, "process.execute") && !IsAllowed(profile, "filesystem.write");
            })
            .ToArray();
        Assert.NotEmpty(shellWithoutWrite);

        foreach (SquadAgent agent in shellWithoutWrite)
        {
            SquadDegradationRecord record = Assert.Single(
                result.Degradations,
                d => d.CanonicalIdentity == agent.Name && d.Code == "capability-not-isolable");
            Assert.Contains("exec", record.Details, StringComparison.Ordinal);
            Assert.Contains("edit", record.Details, StringComparison.Ordinal);
            Assert.Contains("write", record.Details, StringComparison.Ordinal);
            Assert.Contains("apply_patch", record.Details, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RenderAsync_Devin_IsDeterministic()
    {
        SquadRenderResult first = await RenderDevinAsync(ProductRoot);
        SquadRenderResult second = await RenderDevinAsync(ProductRoot);

        Assert.True(first.Success, string.Join("; ", first.Errors));
        Assert.Equal(
            first.Files.Select(f => (f.RelativePath, Convert.ToHexString(f.Content.Span))),
            second.Files.Select(f => (f.RelativePath, Convert.ToHexString(f.Content.Span))));
        Assert.Equal(first.Degradations, second.Degradations);
    }

    [Fact]
    public async Task RenderAsync_Devin_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new DevinRenderer(),
            SquadTarget.Devin,
            $".devin/agents/{ResourceBearingSquadFixture.AgentName}/AGENT.md",
            $".devin/agents/{ResourceBearingSquadFixture.AgentName}",
            ".devin/skills");
    }

    [Fact]
    public async Task RenderAsync_Devin_GlobalScopeDropsTheDevinPrefix()
    {
        SquadRendererRegistry registry = new([new DevinRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Devin],
            Scope: SquadDeploymentScope.Global));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.All(result.Files, f => Assert.True(
            f.RelativePath.StartsWith("agents/", StringComparison.Ordinal) ||
            f.RelativePath.StartsWith("skills/", StringComparison.Ordinal),
            $"Global-scope path '{f.RelativePath}' is outside agents/ and skills/."));
    }

    /// <summary>
    /// A <c>devin:</c> harness override on one model profile is emitted as <c>model</c>, and
    /// an explicit <c>devin: inherit</c> on another still omits it.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Devin_ResolvesModelOverridesPerHarness()
    {
        using DevinModelOverrideFixture fixture = DevinModelOverrideFixture.Create();

        SquadRenderResult result = await RenderDevinAsync(fixture.ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile overridden = Assert.Single(
            result.Files,
            f => f.RelativePath == $".devin/agents/{DevinModelOverrideFixture.OverriddenAgentName}/AGENT.md");
        (YamlMappingNode overriddenFrontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(overridden.Content.Span),
            DevinModelOverrideFixture.OverriddenAgentName);
        Assert.Equal(
            DevinModelOverrideFixture.OverrideModel,
            RequireScalar(overriddenFrontmatter, "model", DevinModelOverrideFixture.OverriddenAgentName));
        Assert.Equal(
            ["name", "description", "model", "allowed-tools"],
            FrontmatterKeys(overriddenFrontmatter));

        SquadDeploymentFile inherited = Assert.Single(
            result.Files,
            f => f.RelativePath == $".devin/agents/{DevinModelOverrideFixture.InheritAgentName}/AGENT.md");
        (YamlMappingNode inheritedFrontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(inherited.Content.Span),
            DevinModelOverrideFixture.InheritAgentName);
        Assert.DoesNotContain("model", FrontmatterKeys(inheritedFrontmatter));
    }

    /// <summary>
    /// Every agent on a canonical model profile renders that profile's pinned Devin model, and
    /// the orchestration profile — held only by the conductor, which lowers to a skill that runs
    /// on the session's model — renders none.
    /// </summary>
    [Theory]
    [InlineData("architect", "claude-opus-5-5-high")]
    [InlineData("deep-planning", "claude-opus-5-5-high")]
    [InlineData("fast", "deepseek-v4-1-flash-high")]
    [InlineData("general", "swe-2-high")]
    [InlineData("reviewer", "grok-4-7-high")]
    public async Task RenderAsync_Devin_CanonicalProfilesEmitTheirPinnedModel(
        string profileName,
        string expectedModel)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent[] agents = source.Agents
            .Where(a => a.Invocation == SquadInvocation.Subagent &&
                string.Equals(a.ModelProfile, profileName, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(agents);

        SquadRenderResult result = await RenderDevinAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".devin/agents/{agent.Name}/AGENT.md");
            (YamlMappingNode frontmatter, _) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent.Name);
            Assert.Equal(expectedModel, RequireScalar(frontmatter, "model", agent.Name));
        }
    }

    [Fact]
    public async Task RenderAsync_Devin_OrchestrationProfileEmitsNoModel()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent conductor = Assert.Single(source.Agents, a => a.Invocation == SquadInvocation.Primary);
        Assert.Equal("orchestration", conductor.ModelProfile);

        SquadRenderResult result = await RenderDevinAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile skillFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".devin/skills/{conductor.Name}/SKILL.md");
        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(skillFile.Content.Span),
            conductor.Name);
        Assert.DoesNotContain("model", FrontmatterKeys(frontmatter));
    }

    [Fact]
    public async Task RenderAsync_Devin_ThrowsWhenACanonicalSkillOccupiesTheLoweredPrimaryIdentity()
    {
        SquadSource baseline = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primary = Assert.Single(baseline.Agents, a => a.Invocation == SquadInvocation.Primary);

        using PiPrimaryIdentityCollisionFixture fixture = PiPrimaryIdentityCollisionFixture.Create(primary.Name);

        SquadRenderValidationException exception = await Assert.ThrowsAsync<SquadRenderValidationException>(
            () => new DevinRenderer().RenderAsync(new SquadRenderRequest(
                SourceDirectory: fixture.ProductRoot,
                Targets: [SquadTarget.Devin],
                Scope: SquadDeploymentScope.Project)));
        Assert.Contains(primary.Name, exception.Message, StringComparison.Ordinal);
    }

    private static async Task<SquadRenderResult> RenderDevinAsync(string sourceDirectory)
    {
        SquadRendererRegistry registry = new([new DevinRenderer()]);
        return await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: sourceDirectory,
            Targets: [SquadTarget.Devin],
            Scope: SquadDeploymentScope.Project));
    }

    private static HashSet<string> SharedIdentities(SquadSource source) =>
        source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string> ComputeExpectedBuiltInTools(SquadCapabilityProfile profile)
    {
        HashSet<string> granted = new(UngovernedTools, StringComparer.Ordinal);
        foreach ((string capability, string[] tools) in CapabilityToolContract)
        {
            if (IsAllowed(profile, capability))
            {
                granted.UnionWith(tools);
            }
        }

        return ToolOrder.Where(granted.Contains).ToArray();
    }

    /// <summary>
    /// Mirrors the rule every MCP-granting renderer shares: any role allowed to read the
    /// filesystem receives the declared servers' tools, except the pure orchestrator.
    /// </summary>
    private static bool ExpectsMcp(SquadAgent agent, SquadCapabilityProfile profile) =>
        !string.Equals(agent.DevinCapabilityProfile ?? agent.CapabilityProfile, "orchestrator", StringComparison.Ordinal) &&
        IsAllowed(profile, "filesystem.read");

    private static IReadOnlyList<string> QualifiedMcpTools(SquadSource source) =>
    [
        .. source.Toolchain.RequiredMcpTools
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .SelectMany(entry => entry.Value
                .OrderBy(tool => tool, StringComparer.Ordinal)
                .Select(tool => $"mcp__{entry.Key}__{tool}"))
    ];

    private static bool IsAllowed(SquadCapabilityProfile profile, string capability) =>
        profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
        decision == SquadPermissionDecision.Allow;

    private static string CollapseToSingleLine(string value) =>
        string.Join(
            ' ',
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string NormalizeBody(string body)
    {
        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalized.EndsWith('\n') ? normalized : normalized + "\n";
    }

    private static (YamlMappingNode Frontmatter, string Body) SplitFrontmatter(string text, string identity)
    {
        const string delimiter = "---\n";
        Assert.True(
            text.StartsWith(delimiter, StringComparison.Ordinal),
            $"'{identity}' is missing the opening frontmatter delimiter.");
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, $"'{identity}' is missing a closing '---' frontmatter delimiter.");

        YamlStream stream = new();
        stream.Load(new StringReader(text[delimiter.Length..(end + 1)]));
        return (Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode), text[(end + 5)..]);
    }

    private static string[] FrontmatterKeys(YamlMappingNode frontmatter) =>
        frontmatter.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(key => key.Value ?? string.Empty)
            .ToArray();

    private static string RequireScalar(YamlMappingNode node, string key, string identity)
    {
        Assert.True(
            node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value),
            $"'{identity}' frontmatter is missing '{key}'.");
        return Assert.IsType<YamlScalarNode>(value).Value
            ?? throw new InvalidOperationException($"'{identity}' key '{key}' has a null scalar value.");
    }

    private static string[] RequireSequence(YamlMappingNode node, string key, string identity)
    {
        Assert.True(
            node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value),
            $"'{identity}' frontmatter is missing '{key}'.");
        return Assert.IsType<YamlSequenceNode>(value).Children
            .Select(item => Assert.IsType<YamlScalarNode>(item).Value ?? string.Empty)
            .ToArray();
    }
}

/// <summary>
/// Copies the real <c>products/kyber-squad</c> corpus and sets one model profile's
/// <c>devin:</c> value to a different model and another's to an explicit <c>inherit</c>, so
/// both branches of model resolution are observable without hand-authoring a minimal corpus.
/// </summary>
internal sealed class DevinModelOverrideFixture : IDisposable
{
    internal const string OverriddenProfile = "general";
    internal const string OverriddenAgentName = "dal-dev";
    internal const string OverrideModel = "swe-1-6";
    internal const string InheritProfile = "fast";
    internal const string InheritAgentName = "csharp-dev";

    private readonly TempDirectory _temp = new();

    private DevinModelOverrideFixture()
    {
        ProductRoot = Path.Combine(_temp.Path, "kyber-squad");
    }

    internal string ProductRoot { get; }

    internal static DevinModelOverrideFixture Create()
    {
        DevinModelOverrideFixture fixture = new();
        string canonicalRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
        PiCorpusFixtureHelpers.CopyDirectory(canonicalRoot, fixture.ProductRoot);

        string modelsPath = Path.Combine(fixture.ProductRoot, "profiles", "models.yml");
        string original = File.ReadAllText(modelsPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        string mutated = SetHarnessValue(original, OverriddenProfile, OverrideModel);
        mutated = SetHarnessValue(mutated, InheritProfile, "inherit");

        File.WriteAllText(modelsPath, mutated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fixture;
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Replaces the profile's <c>devin:</c> line, or adds one after <c>default</c> when the
    /// profile has none, so the fixture keeps working whichever way the corpus pins Devin.
    /// </summary>
    private static string SetHarnessValue(string content, string profileName, string value)
    {
        string header = $"  {profileName}:\n    default: inherit\n";
        int start = content.IndexOf(header, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Profile '{profileName}' not found in models.yml.");
        }

        Match next = Regex.Match(content[(start + header.Length)..], @"^  \S", RegexOptions.Multiline);
        int end = next.Success ? start + header.Length + next.Index : content.Length;
        string block = content[start..end];
        string replaced = Regex.IsMatch(block, @"^    devin: ", RegexOptions.Multiline)
            ? Regex.Replace(block, @"^    devin: .*$", $"    devin: {value}", RegexOptions.Multiline)
            : block.Insert(header.Length, $"    devin: {value}\n");

        return string.Concat(content.AsSpan(0, start), replaced, content.AsSpan(end));
    }
}
