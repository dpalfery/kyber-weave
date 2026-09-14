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
/// <see cref="SquadRendererRegistry"/> with <see cref="PiRenderer"/> and validates the result
/// against the Pi rendering contract (plan <c>docs/plans/2026-09-14-pi-harness-target.md</c>,
/// section 6).
/// </summary>
/// <remarks>
/// <para>
/// Pi is a native target: the 20 subagent-invocation agents render as
/// <c>.pi/agents/&lt;name&gt;.md</c>, and the one <c>invocation: primary</c> agent
/// (<c>conductor</c>) lowers to <c>.pi/skills/&lt;name&gt;/SKILL.md</c> per its fallback
/// profile's <c>no-primary-agent</c> value, because Pi core has no primary-agent selection.
/// Canonical skills render at <c>.pi/skills/&lt;name&gt;/SKILL.md</c>.
/// </para>
/// <para>
/// The facts this suite pins were verified on 2026-09-14 against the installed
/// <c>@earendil-works/pi-coding-agent</c> <b>0.84.4</b> (package.json; the Homebrew keg label
/// 0.84.1 is stale) and the <c>@tintinweb/pi-subagents</c> extension <b>0.19.0</b>, which
/// defines the Claude Code-like custom-agent file format Pi has no native equivalent for.
/// Pi core itself ships no sub-agents and no permission prompts (pi-coding-agent README,
/// "No sub-agents" / "No permission popups"). The seven-tool vocabulary
/// (<c>read, grep, find, ls, edit, write, bash</c>), the always-emitted <c>tools</c> key
/// (omitting it grants every built-in), <c>extensions: false</c> (the owner's Q1-A decision,
/// so only built-in tools are reachable), and <c>allowed_subagents</c> runtime enforcement all
/// come from the pi-subagents README and its <c>custom-agents.ts</c> / <c>agent-types.ts</c>
/// sources — see plan section 5.1 (P1–P17) for the full evidence table.
/// </para>
/// </remarks>
public sealed class PiRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// The capability→tool lowering this suite pins, transcribed independently from plan
    /// section 6 rather than read from the renderer. <c>network.read</c> and
    /// <c>network.publish</c> are absent because Pi has no built-in tool for either
    /// (R8); <c>delegate</c> is handled separately because it produces
    /// <c>allowed_subagents</c>, not a tool.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityToolContract =
    [
        ("filesystem.read", ["read"]),
        ("filesystem.search", ["grep", "find", "ls"]),
        ("filesystem.write", ["edit", "write"]),
        ("process.execute", ["bash"]),
    ];

    /// <summary>Fixed emission order for the <c>tools</c> CSV, per plan section 6.</summary>
    private static readonly string[] ToolOrder = ["read", "grep", "find", "ls", "edit", "write", "bash"];

    /// <summary>
    /// Shared identities are read from the loaded fallback profiles so this suite follows
    /// the same source-derived contract as the renderer rather than a literal roster that
    /// could drift from <c>profiles/fallbacks.yml</c>.
    /// </summary>
    private static HashSet<string> SharedIdentities(SquadSource source) =>
        source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string> ComputeExpectedTools(SquadCapabilityProfile profile)
    {
        HashSet<string> granted = new(StringComparer.Ordinal);
        foreach ((string capability, string[] tools) in CapabilityToolContract)
        {
            if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision == SquadPermissionDecision.Allow)
            {
                foreach (string tool in tools)
                {
                    granted.Add(tool);
                }
            }
        }

        return ToolOrder.Where(tool => granted.Contains(tool)).ToArray();
    }

    /// <summary>
    /// Parses a Pi <c>tools</c>/<c>allowed_subagents</c> CSV scalar into an ordered token
    /// list. Splitting rather than pinning an exact separator string keeps this suite bound
    /// to the documented contract (membership and fixed order) instead of an unspecified
    /// whitespace choice the renderer is free to make.
    /// </summary>
    private static IReadOnlyList<string> ParseCsvList(string raw)
    {
        if (string.Equals(raw, "none", StringComparison.Ordinal))
        {
            return [];
        }

        return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

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

    private static async Task<SquadRenderResult> RenderPiAsync(string sourceDirectory)
    {
        SquadRendererRegistry registry = new([new PiRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: sourceDirectory,
            Targets: [SquadTarget.Pi],
            Scope: SquadDeploymentScope.Project);
        return await registry.RenderAsync(request);
    }

    public void Dispose()
    {
        // No disposable state: the suite only reads the checked-in corpus, plus per-test
        // fixture copies disposed locally via `using`. IDisposable is implemented per the
        // test-coding-standard so a future class-level fixture has a home.
    }

    [Fact]
    public void SupportedTargets_IsExactlyPi()
    {
        SquadRendererRegistry registry = new([new PiRenderer()]);

        Assert.Equal([SquadTarget.Pi], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new PiRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Pi, SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("cursor", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonPiTarget()
    {
        PiRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Pi_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = SharedIdentities(source);

        SquadRenderResult result = await RenderPiAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        // R17: Agents.Count + Σ agent resources + Skills.Count − shared-identity skills +
        // Σ non-suppressed skill resources. The conductor contributes one principal whether
        // it renders as an agent (elsewhere) or, here, a lowered skill.
        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));
        int expectedFileCount =
            source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + source.Skills.Count - suppressedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);

        Assert.Equal(expectedFileCount, result.Files.Count);
        Assert.All(result.Files, f => Assert.Equal("pi", f.Target));
        Assert.All(result.Files, f => Assert.True(
            f.RelativePath.StartsWith(".pi/agents/", StringComparison.Ordinal) ||
            f.RelativePath.StartsWith(".pi/skills/", StringComparison.Ordinal),
            $"File '{f.RelativePath}' is outside .pi/agents/ and .pi/skills/."));

        // Native target: role- prefixing is Antigravity's fallback-lowering mechanism only.
        Assert.DoesNotContain(
            result.Files,
            f => f.RelativePath.Contains("role-", StringComparison.OrdinalIgnoreCase));

        // R14: the renderer never touches pre-existing Pi runtime configuration files.
        Assert.DoesNotContain(result.Files, f => f.RelativePath == ".pi/subagents.json");
        Assert.DoesNotContain(result.Files, f => f.RelativePath == ".pi/settings.json");
    }

    [Fact]
    public async Task RenderAsync_Pi_EachSubagentAgentHasCanonicalFrontmatterAndBody()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderPiAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in source.Agents.Where(a => a.Invocation == SquadInvocation.Subagent))
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".pi/agents/{agent.Name}.md");

            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent.Name);

            string emittedName = RequireScalar(frontmatter, "name", agent.Name);
            Assert.Equal(agent.Name, emittedName);
            Assert.DoesNotContain(':', emittedName);

            string expectedDescription = CollapseToSingleLine(agent.Description);
            Assert.True(
                string.Equals(expectedDescription, RequireScalar(frontmatter, "description", agent.Name), StringComparison.Ordinal),
                $"Agent '{agent.Name}' description mismatch.");

            // T2 criterion 3: model value per profile. The renderer emits `model` only when
            // the resolved Pi model is not `inherit`. Expected values hardcoded per plan section 6b.
            // Derive agent membership from the loaded source, but assert against approved tokens.
            string expectedModel = agent.ModelProfile switch
            {
                "deep-planning" => "zai/glm-5.3",
                "fast" => "opencode/muse-spark-1.3-contributor-free",
                "general" => "opencode/muse-spark-1.3-contributor-free",
                "reviewer" => "opencode-go/kimi-k2.7-code",
                "orchestration" => "inherit",
                _ => throw new InvalidOperationException($"Unknown profile '{agent.ModelProfile}' for agent '{agent.Name}'.")
            };

            if (expectedModel == "inherit")
            {
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                    $"Agent '{agent.Name}' on profile '{agent.ModelProfile}' should omit 'model' (resolves to inherit).");
            }
            else
            {
                string emittedModel = RequireScalar(frontmatter, "model", agent.Name);
                Assert.True(
                    expectedModel == emittedModel,
                    $"Agent '{agent.Name}' model '{emittedModel}' does not match expected '{expectedModel}'.");
            }

            SquadCapabilityProfile capabilityProfile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            IReadOnlyList<string> expectedTools = ComputeExpectedTools(capabilityProfile);
            string rawTools = RequireScalar(frontmatter, "tools", agent.Name);
            IReadOnlyList<string> actualTools = ParseCsvList(rawTools);

            Assert.All(actualTools, tool => Assert.Contains(tool, ToolOrder));
            Assert.True(
                actualTools.Distinct(StringComparer.Ordinal).Count() == actualTools.Count,
                $"Agent '{agent.Name}' has duplicate tool entries: {string.Join(", ", actualTools)}.");
            Assert.Equal(expectedTools, actualTools);
            if (expectedTools.Count == 0)
            {
                Assert.Equal("none", rawTools);
            }

            Assert.Equal("false", RequireScalar(frontmatter, "extensions", agent.Name));

            bool delegateAllowed =
                capabilityProfile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                delegateDecision == SquadPermissionDecision.Allow;
            bool expectRoster = delegateAllowed && agent.DelegatesTo.Count > 0;
            bool hasAllowedSubagentsKey = frontmatter.Children.ContainsKey(new YamlScalarNode("allowed_subagents"));

            if (expectRoster)
            {
                Assert.True(hasAllowedSubagentsKey, $"Agent '{agent.Name}' should declare 'allowed_subagents'.");
                IReadOnlyList<string> roster = ParseCsvList(RequireScalar(frontmatter, "allowed_subagents", agent.Name));
                Assert.Equal(agent.DelegatesTo, roster);
            }
            else
            {
                Assert.False(hasAllowedSubagentsKey, $"Agent '{agent.Name}' should omit 'allowed_subagents'.");
            }

            // Key order is the frontmatter contract (plan section 6): name, description, [model],
            // tools, extensions, [allowed_subagents]. 'model' appears only when the resolved Pi
            // model is not 'inherit' (plan section 7, T16 criterion 3).
            List<string> expectedKeyOrder = ["name", "description"];
            if (expectedModel != "inherit")
            {
                expectedKeyOrder.Add("model");
            }
            expectedKeyOrder.Add("tools");
            expectedKeyOrder.Add("extensions");
            if (expectRoster)
            {
                expectedKeyOrder.Add("allowed_subagents");
            }

            string[] actualKeys = frontmatter.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(key => key.Value ?? string.Empty)
                .ToArray();
            Assert.Equal(expectedKeyOrder, actualKeys);

            string expectedBody = NormalizeBody(agent.InstructionBody);
            Assert.Contains(expectedBody, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RenderAsync_Pi_AgentsDirectoryHasOnlyAgentPrincipalsAtTopLevel()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderPiAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        const string agentsPrefix = ".pi/agents/";
        const string skillsPrefix = ".pi/skills/";

        string[] expectedPrincipals = source.Agents
            .Where(a => a.Invocation == SquadInvocation.Subagent)
            .Select(a => $"{agentsPrefix}{a.Name}.md")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        string[] directAgentChildren = result.Files
            .Select(f => f.RelativePath)
            .Where(path => path.StartsWith(agentsPrefix, StringComparison.Ordinal))
            .Where(path => !path[agentsPrefix.Length..].Contains('/', StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        // Every direct .pi/agents/ child is an agent principal; a resource for that agent
        // must live one level deeper (.pi/agents/<name>/<resource path>).
        Assert.Equal(expectedPrincipals, directAgentChildren);

        Assert.DoesNotContain(result.Files, f =>
            f.RelativePath.StartsWith(skillsPrefix, StringComparison.Ordinal) &&
            f.RelativePath.EndsWith(".md", StringComparison.Ordinal) &&
            !f.RelativePath[skillsPrefix.Length..].Contains('/', StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Pi_LowersThePrimaryAgentToASkill()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderPiAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadAgent[] primaryAgents = source.Agents.Where(a => a.Invocation == SquadInvocation.Primary).ToArray();

        // The current corpus declares exactly one primary agent (conductor); asserting
        // non-empty here keeps the loop below from silently vacuously passing if that ever
        // changes to zero.
        Assert.NotEmpty(primaryAgents);

        foreach (SquadAgent agent in primaryAgents)
        {
            SquadFallbackProfile fallbackProfile = source.FallbackProfiles.Profiles[agent.Fallback];

            if (string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(result.Files, f => f.RelativePath == $".pi/agents/{agent.Name}.md");

                SquadDeploymentFile skillFile = Assert.Single(
                    result.Files,
                    f => f.RelativePath == $".pi/skills/{agent.Name}/SKILL.md");

                (YamlMappingNode frontmatter, string body) = SplitFrontmatter(
                    Encoding.UTF8.GetString(skillFile.Content.Span),
                    agent.Name);

                Assert.Equal(agent.Name, RequireScalar(frontmatter, "name", agent.Name));
                Assert.True(
                    string.Equals(CollapseToSingleLine(agent.Description), RequireScalar(frontmatter, "description", agent.Name), StringComparison.Ordinal),
                    $"Lowered primary agent '{agent.Name}' description mismatch.");
                Assert.True(
                    string.Equals("MIT", RequireScalar(frontmatter, "license", agent.Name), StringComparison.Ordinal),
                    $"Lowered primary agent '{agent.Name}' license mismatch.");

                Assert.Contains(NormalizeBody(agent.InstructionBody), body, StringComparison.Ordinal);

                foreach (SquadResource resource in agent.Resources)
                {
                    Assert.Contains(
                        result.Files,
                        f => f.RelativePath == $".pi/skills/{agent.Name}/{resource.RelativePath}");
                }

                SquadDegradationRecord fallbackRecord = Assert.Single(
                    result.Degradations,
                    d => d.CanonicalIdentity == agent.Name && d.Code == "role-skill-fallback");
                Assert.Equal(agent.Name, fallbackRecord.OutputIdentity);
                Assert.Equal(agent.BodyDigest, fallbackRecord.InstructionDigest);

                SquadDegradationRecord permissionRecord = Assert.Single(
                    result.Degradations,
                    d => d.CanonicalIdentity == agent.Name && d.Code == "permission-not-expressible");
                Assert.Equal(agent.Name, permissionRecord.OutputIdentity);
            }
            else if (string.Equals(fallbackProfile.NoPrimaryAgent, "omit", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(result.Files, f => f.RelativePath == $".pi/agents/{agent.Name}.md");
                Assert.DoesNotContain(result.Files, f => f.RelativePath == $".pi/skills/{agent.Name}/SKILL.md");

                SquadDegradationRecord omitted = Assert.Single(
                    result.Degradations,
                    d => d.CanonicalIdentity == agent.Name && d.Code == "omitted");
                Assert.Equal(agent.BodyDigest, omitted.InstructionDigest);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Fallback profile '{agent.Fallback}' declares unsupported no-primary-agent " +
                    $"value '{fallbackProfile.NoPrimaryAgent}' for primary agent '{agent.Name}'.");
            }
        }
    }

    [Fact]
    public async Task RenderAsync_Pi_CanonicalSkillsHaveValidFrontmatter()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderPiAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadSkill skill in source.Skills)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".pi/skills/{skill.Name}/SKILL.md");

            (YamlMappingNode frontmatter, _) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                skill.Name);

            string name = RequireScalar(frontmatter, "name", skill.Name);
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", name);
            Assert.True(name.Length <= 64, $"Skill '{skill.Name}' rendered name exceeds 64 characters.");

            string description = RequireScalar(frontmatter, "description", skill.Name);
            Assert.DoesNotContain('\n', description);
            Assert.True(description.Length <= 1024, $"Skill '{skill.Name}' rendered description exceeds 1024 characters.");

            Assert.True(
                string.Equals("MIT", RequireScalar(frontmatter, "license", skill.Name), StringComparison.Ordinal),
                $"Skill '{skill.Name}' license mismatch.");
        }
    }

    [Fact]
    public async Task RenderAsync_Pi_RecordsDegradationsPerContract()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderPiAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);

        // Section 6: every record carries Target "pi", tracks the same canonical and output
        // identity on this native target (Pi fails closed rather than role-prefixing — see
        // RenderAsync_Pi_ThrowsWhenACanonicalSkillOccupiesTheLoweredPrimaryIdentity), matches
        // the owning agent's body digest, and never claims a widened grant.
        Assert.All(result.Degradations, d => Assert.Equal("pi", d.Target));
        Assert.All(result.Degradations, d => Assert.Equal(d.CanonicalIdentity, d.OutputIdentity));
        Assert.All(result.Degradations, d => Assert.Equal(agentsByName[d.CanonicalIdentity].BodyDigest, d.InstructionDigest));
        Assert.All(result.Degradations, d => Assert.DoesNotContain("widen", d.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        SquadAgent[] subagentAgents = source.Agents.Where(a => a.Invocation == SquadInvocation.Subagent).ToArray();

        string[] expectedSafetyNarrowed = subagentAgents
            .Where(a => source.CapabilityProfiles.Profiles[a.CapabilityProfile].Permissions.Values
                .Any(decision => decision == SquadPermissionDecision.Ask))
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] actualSafetyNarrowed = result.Degradations
            .Where(d => string.Equals(d.Code, "safety-narrowed", StringComparison.Ordinal))
            .Select(d => d.CanonicalIdentity)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedSafetyNarrowed);
        Assert.Equal(expectedSafetyNarrowed, actualSafetyNarrowed);

        foreach (string name in expectedSafetyNarrowed)
        {
            SquadDegradationRecord record = Assert.Single(
                result.Degradations,
                d => d.CanonicalIdentity == name && d.Code == "safety-narrowed");

            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agentsByName[name].CapabilityProfile];
            string[] askCapabilities = profile.Permissions
                .Where(pair => pair.Value == SquadPermissionDecision.Ask)
                .Select(pair => pair.Key)
                .OrderBy(capability => capability, StringComparer.Ordinal)
                .ToArray();

            foreach (string capability in askCapabilities)
            {
                Assert.Contains(capability, record.Details ?? string.Empty, StringComparison.Ordinal);
            }
        }

        // Non-primary permission-not-expressible: network.read/publish allowed, or an empty
        // delegate-allow roster (R9/R10). No corpus agent hits the empty-roster case today,
        // but the predicate is written generically rather than pinned to the current set.
        string[] expectedNonPrimaryPermissionNotExpressible = subagentAgents
            .Where(a =>
            {
                SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                bool networkReadAllowed =
                    profile.Permissions.TryGetValue("network.read", out SquadPermissionDecision readDecision) &&
                    readDecision == SquadPermissionDecision.Allow;
                bool networkPublishAllowed =
                    profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
                    publishDecision == SquadPermissionDecision.Allow;
                bool emptyRosterDelegateAllowed =
                    profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                    delegateDecision == SquadPermissionDecision.Allow &&
                    a.DelegatesTo.Count == 0;
                return networkReadAllowed || networkPublishAllowed || emptyRosterDelegateAllowed;
            })
            .Select(a => a.Name)
            .ToArray();

        // Lowered primary agents always carry permission-not-expressible (section 6).
        string[] expectedPrimaryPermissionNotExpressible = source.Agents
            .Where(a => a.Invocation == SquadInvocation.Primary &&
                        string.Equals(source.FallbackProfiles.Profiles[a.Fallback].NoPrimaryAgent, "skill", StringComparison.Ordinal))
            .Select(a => a.Name)
            .ToArray();

        string[] expectedPermissionNotExpressible = expectedNonPrimaryPermissionNotExpressible
            .Concat(expectedPrimaryPermissionNotExpressible)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] actualPermissionNotExpressible = result.Degradations
            .Where(d => string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal))
            .Select(d => d.CanonicalIdentity)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedPermissionNotExpressible);
        Assert.Equal(expectedPermissionNotExpressible, actualPermissionNotExpressible);

        foreach (string name in expectedPermissionNotExpressible)
        {
            Assert.Single(result.Degradations, d => d.CanonicalIdentity == name && d.Code == "permission-not-expressible");
        }
    }

    [Fact]
    public async Task RenderAsync_Pi_IsDeterministic()
    {
        SquadRenderResult first = await RenderPiAsync(ProductRoot);
        SquadRenderResult second = await RenderPiAsync(ProductRoot);

        Assert.True(first.Success, string.Join("; ", first.Errors));
        Assert.True(second.Success, string.Join("; ", second.Errors));
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
    public async Task RenderAsync_Pi_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new PiRenderer(),
            SquadTarget.Pi,
            ".pi/agents/bug-crusher-investigator.md",
            ".pi/agents",
            ".pi/skills");
    }

    /// <summary>
    /// Criterion 8: a corpus copy that adds a <c>pi:</c> harness override to one model
    /// profile must emit <c>model: &lt;value&gt;</c> for that profile's agents, and an
    /// explicit <c>pi: inherit</c> on another profile must still omit 'model' — proving the
    /// explicit-inherit branch is exercised, not just the omitted-key default.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Pi_ResolvesModelOverridesPerHarness()
    {
        using PiModelOverrideFixture fixture = PiModelOverrideFixture.Create();

        SquadRenderResult result = await RenderPiAsync(fixture.ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile overriddenAgentFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".pi/agents/{PiModelOverrideFixture.OverriddenAgentName}.md");
        (YamlMappingNode overriddenFrontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(overriddenAgentFile.Content.Span),
            PiModelOverrideFixture.OverriddenAgentName);
        Assert.Equal(
            PiModelOverrideFixture.OverrideModel,
            RequireScalar(overriddenFrontmatter, "model", PiModelOverrideFixture.OverriddenAgentName));

        SquadDeploymentFile inheritAgentFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".pi/agents/{PiModelOverrideFixture.InheritAgentName}.md");
        (YamlMappingNode inheritFrontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(inheritAgentFile.Content.Span),
            PiModelOverrideFixture.InheritAgentName);
        Assert.False(
            inheritFrontmatter.Children.ContainsKey(new YamlScalarNode("model")),
            "An explicit 'pi: inherit' harness override must still omit 'model'.");
    }

    /// <summary>
    /// Criterion 9 / R3's fail-closed rule: if a canonical skill already occupies the
    /// identity the primary agent would lower to, Pi cannot fall back to a
    /// <c>role-</c>-prefixed skill (that is Antigravity's fallback-only mechanism), so
    /// rendering must throw <see cref="SquadRenderValidationException"/> naming the identity.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Pi_ThrowsWhenACanonicalSkillOccupiesTheLoweredPrimaryIdentity()
    {
        SquadSource baselineSource = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(baselineSource.Agents, a => a.Invocation == SquadInvocation.Primary);

        using PiPrimaryIdentityCollisionFixture fixture = PiPrimaryIdentityCollisionFixture.Create(primaryAgent.Name);

        SquadRenderValidationException exception = await Assert.ThrowsAsync<SquadRenderValidationException>(
            () => RenderPiAsync(fixture.ProductRoot));

        Assert.Contains(primaryAgent.Name, exception.Message, StringComparison.Ordinal);
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

/// <summary>
/// Copies the real <c>products/kyber-squad</c> corpus and adds a <c>pi:</c> harness override
/// to one model profile (and an explicit <c>pi: inherit</c> to another), so
/// <see cref="PiRendererContractTests.RenderAsync_Pi_ResolvesModelOverridesPerHarness"/> can
/// observe both branches of model resolution without hand-authoring a minimal corpus that
/// could drift from the schema the real one is validated against.
/// </summary>
internal sealed class PiModelOverrideFixture : IDisposable
{
    internal const string OverriddenProfile = "general";
    internal const string OverriddenAgentName = "dal-dev";
    internal const string OverrideModel = "acme/pi-test-model";
    internal const string InheritProfile = "fast";
    internal const string InheritAgentName = "csharp-dev";

    private readonly TempDirectory _temp = new();

    private PiModelOverrideFixture()
    {
        ProductRoot = Path.Combine(_temp.Path, "kyber-squad");
    }

    internal string ProductRoot { get; }

    internal static PiModelOverrideFixture Create()
    {
        PiModelOverrideFixture fixture = new();
        string canonicalRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
        CopyDirectory(canonicalRoot, fixture.ProductRoot);

        string modelsPath = Path.Combine(fixture.ProductRoot, "profiles", "models.yml");
        string original = File.ReadAllText(modelsPath);

        // Set or replace pi: values in the target profiles, whether they exist or not.
        // Regex pattern matches a profile section and replaces or inserts its pi: value.
        string mutated = ReplaceOrInsertPiValue(original, OverriddenProfile, OverrideModel);
        mutated = ReplaceOrInsertPiValue(mutated, InheritProfile, "inherit");

        if (string.Equals(original, mutated, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected '{modelsPath}' to contain profiles '{OverriddenProfile}' and " +
                $"'{InheritProfile}' to mutate.");
        }

        File.WriteAllText(modelsPath, mutated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fixture;
    }

    /// <summary>
    /// Replaces or inserts a pi: value in a profile section. Handles both cases where the
    /// pi: line already exists and where it doesn't. Uses regex to robustly find and replace
    /// within the profile block.
    /// </summary>
    private static string ReplaceOrInsertPiValue(string content, string profileName, string piValue)
    {
        // Use regex to find the profile section and its pi: line (if it exists)
        Regex profileRegex = new(
            $@"^  {Regex.Escape(profileName)}:\n    default: inherit\n((?:    \w+:.*\n)*)",
            RegexOptions.Multiline);

        Match match = profileRegex.Match(content);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Profile '{profileName}' not found in models.yml.");
        }

        // Check if pi: already exists in the captured lines
        string existingLines = match.Groups[1].Value;
        Regex piLineRegex = new(@"    pi:.*\n");
        Match piMatch = piLineRegex.Match(existingLines);

        string newProfile;
        if (piMatch.Success)
        {
            // Replace existing pi: line
            newProfile = piLineRegex.Replace(existingLines, $"    pi: {piValue}\n", 1);
        }
        else
        {
            // Insert new pi: line after default: inherit
            newProfile = existingLines + $"    pi: {piValue}\n";
        }

        // Replace the entire matched section with the modified version
        string replacement = $"  {profileName}:\n    default: inherit\n" + newProfile;
        return content.Replace(match.Value, replacement, StringComparison.Ordinal);
    }

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
}

/// <summary>
/// Copies the real <c>products/kyber-squad</c> corpus and adds a canonical skill whose name
/// collides with the primary agent's identity, so
/// <see cref="PiRendererContractTests.RenderAsync_Pi_ThrowsWhenACanonicalSkillOccupiesTheLoweredPrimaryIdentity"/>
/// can observe the fail-closed behavior R3 requires on a native target.
/// </summary>
internal sealed class PiPrimaryIdentityCollisionFixture : IDisposable
{
    private readonly TempDirectory _temp = new();

    private PiPrimaryIdentityCollisionFixture()
    {
        ProductRoot = Path.Combine(_temp.Path, "kyber-squad");
    }

    internal string ProductRoot { get; }

    internal static PiPrimaryIdentityCollisionFixture Create(string primaryAgentName)
    {
        PiPrimaryIdentityCollisionFixture fixture = new();
        string canonicalRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
        CopyDirectory(canonicalRoot, fixture.ProductRoot);

        string skillDirectory = Path.Combine(fixture.ProductRoot, "skills", primaryAgentName);
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            $"""
             ---
             name: {primaryAgentName}
             description: Use when a canonical skill deliberately occupies a primary agent's lowered identity.
             license: MIT
             ---
             Fixture body for the '{primaryAgentName}' identity collision.
             """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return fixture;
    }

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
}
