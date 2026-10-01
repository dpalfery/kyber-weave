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
/// and skills lower to <c>.cursor/skills/&lt;name&gt;/SKILL.md</c>. Verifies profile-declared
/// shared-identity suppression, permission lowering to Cursor's <c>readonly</c> boolean flag, model resolution
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
    /// The exact frontmatter key set a lowered primary-agent skill carries: <c>name</c>,
    /// single-line <c>description</c>, <c>license</c> — and no
    /// <c>disable-model-invocation</c> key, so auto-load stays allowed. Kept as a field
    /// per CA1861 because the skill-only assertion runs per primary agent.
    /// </summary>
    private static readonly string[] LoweredSkillFrontmatterKeys = ["name", "description", "license"];

    /// <summary>
    /// Shared identities are read from the loaded fallback profile so the test follows the
    /// same generic single-projection contract as the renderer.
    /// </summary>
    private static HashSet<string> SharedIdentities(SquadSource source) =>
        source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

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
        HashSet<string> sharedIdentities = SharedIdentities(source);
        SquadRendererRegistry registry = new([new CursorRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));

        // SKILL-ONLY replacement (Pi-renderer precedent): a canonical agent with
        // invocation: primary whose fallback profile declares no-primary-agent: skill
        // contributes its principal as a lowered skill (.cursor/skills/<name>/SKILL.md)
        // plus its resource closure beside it — never an agent file. An omit-mode
        // primary contributes nothing. Every non-omitted agent still contributes exactly
        // one principal; only the projection path changes.
        SquadAgent[] primaryAgents = source.Agents
            .Where(agent => agent.Invocation == SquadInvocation.Primary)
            .ToArray();

        // The current corpus declares exactly one primary agent (conductor); asserting
        // non-empty here keeps the replacement math below from silently vacuously passing
        // if that ever changes to zero.
        Assert.NotEmpty(primaryAgents);
        int omittedPrimaryCount = primaryAgents.Count(agent =>
            string.Equals(
                source.FallbackProfiles.Profiles[agent.Fallback].NoPrimaryAgent,
                "omit",
                StringComparison.Ordinal));
        int emittedAgentResourceCount = source.Agents.Sum(agent => agent.Resources.Count)
            - primaryAgents
                .Where(agent =>
                    string.Equals(
                        source.FallbackProfiles.Profiles[agent.Fallback].NoPrimaryAgent,
                        "omit",
                        StringComparison.Ordinal))
                .Sum(agent => agent.Resources.Count);

        // C3: every rendered owner also projects its validated resource closure beside its
        // principal output, so the corpus count is principals plus emitted closures.
        int expectedFileCount =
            source.Agents.Count - omittedPrimaryCount + emittedAgentResourceCount
            + source.Skills.Count - suppressedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);
        Assert.Equal(expectedFileCount, result.Files.Count);
        Assert.All(result.Files, f => Assert.Equal("cursor", f.Target));

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);
        foreach (SquadAgent agent in source.Agents.Where(a => a.Invocation == SquadInvocation.Subagent))
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".cursor/agents/{agent.Name}.md");

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
            if (modelProfile.HarnessModels.TryGetValue("cursor", out string? expectedModel))
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
                    string.Equals("true", RequireScalar(frontmatter, "readonly", agent.Name), StringComparison.Ordinal),
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
            string expectedAgentBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedAgentBody.EndsWith('\n'))
            {
                expectedAgentBody += "\n";
            }

            Assert.True(
                string.Equals(expectedAgentBody, body, StringComparison.Ordinal),
                $"Agent '{agent.Name}' body mismatch.");
        }

        // SKILL-ONLY replacement: every skill-mode primary agent renders only as a
        // lowered skill; no .cursor/agents/<name>.md is emitted for it. An omit-mode
        // primary renders neither an agent file nor a lowered skill.
        foreach (SquadAgent primaryAgent in primaryAgents)
        {
            SquadFallbackProfile fallbackProfile = source.FallbackProfiles.Profiles[primaryAgent.Fallback];
            if (string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(
                    result.Files,
                    f => f.RelativePath == $".cursor/agents/{primaryAgent.Name}.md");

                SquadDeploymentFile loweredSkillFile = Assert.Single(
                    result.Files,
                    f => f.RelativePath == $".cursor/skills/{primaryAgent.Name}/SKILL.md");
                (YamlMappingNode loweredFrontmatter, string loweredBody) = SplitFrontmatter(
                    Encoding.UTF8.GetString(loweredSkillFile.Content.Span),
                    primaryAgent.Name);
                Assert.Equal(primaryAgent.Name, RequireScalar(loweredFrontmatter, "name", primaryAgent.Name));
                string expectedLoweredDescription = string.Join(" ", primaryAgent.Description.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                Assert.True(
                    string.Equals(expectedLoweredDescription, RequireScalar(loweredFrontmatter, "description", primaryAgent.Name), StringComparison.Ordinal),
                    $"Lowered primary agent '{primaryAgent.Name}' description mismatch.");
                Assert.True(
                    string.Equals("MIT", RequireScalar(loweredFrontmatter, "license", primaryAgent.Name), StringComparison.Ordinal),
                    $"Lowered primary agent '{primaryAgent.Name}' license mismatch.");

                string expectedLoweredBody = primaryAgent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
                if (!expectedLoweredBody.EndsWith('\n'))
                {
                    expectedLoweredBody += "\n";
                }

                Assert.True(
                    string.Equals(expectedLoweredBody, loweredBody, StringComparison.Ordinal),
                    $"Lowered primary agent '{primaryAgent.Name}' body mismatch.");

                foreach (SquadResource primaryResource in primaryAgent.Resources)
                {
                    Assert.Contains(
                        result.Files,
                        f => f.RelativePath == $".cursor/skills/{primaryAgent.Name}/{primaryResource.RelativePath}");
                }
            }
            else
            {
                Assert.DoesNotContain(
                    result.Files,
                    f => f.RelativePath == $".cursor/agents/{primaryAgent.Name}.md");
                Assert.DoesNotContain(
                    result.Files,
                    f => f.RelativePath.StartsWith($".cursor/skills/{primaryAgent.Name}/", StringComparison.Ordinal));
            }
        }

        // Concrete lowerings verification against loaded profiles
        SquadCapabilityProfile architectProfile = source.CapabilityProfiles.Profiles["architect"];
        Assert.Equal(SquadPermissionDecision.Allow, architectProfile.Permissions["filesystem.write"]);
        Assert.Equal(SquadPermissionDecision.Ask, architectProfile.Permissions["process.execute"]);
        AssertReadOnly(result, "architect", expectedReadOnly: false);

        SquadCapabilityProfile docProfile = source.CapabilityProfiles.Profiles["documentation"];
        Assert.Equal(SquadPermissionDecision.Allow, docProfile.Permissions["filesystem.write"]);
        AssertReadOnly(result, "docs-dev", expectedReadOnly: false);

        SquadCapabilityProfile investigatorProfile = source.CapabilityProfiles.Profiles["investigator"];
        Assert.Equal(SquadPermissionDecision.Allow, investigatorProfile.Permissions["process.execute"]);
        AssertReadOnly(result, "bug-crusher-investigator", expectedReadOnly: false);

        foreach (string sharedIdentity in sharedIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".cursor/agents/{sharedIdentity}.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".cursor/skills/{sharedIdentity}/SKILL.md");
        }

        // Skills verification
        foreach (SquadSkill skill in source.Skills)
        {
            bool isSharedIdentity = sharedIdentities.Contains(skill.Name);
            string path = $".cursor/skills/{skill.Name}/SKILL.md";
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

    /// <summary>
    /// SKILL-ONLY replacement (Q1, Pi-renderer precedent): the canonical
    /// <c>invocation: primary</c> agent (conductor) renders only as
    /// <c>.cursor/skills/&lt;name&gt;/SKILL.md</c> — never as
    /// <c>.cursor/agents/&lt;name&gt;.md</c> — with frontmatter exactly <c>name</c>,
    /// single-line <c>description</c>, <c>license: MIT</c> and no
    /// <c>disable-model-invocation</c> key so auto-load stays allowed (Q2), plus its
    /// resource closure beside it.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Cursor_LowersThePrimaryAgentToASkillOnly()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new CursorRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadAgent[] primaryAgents = source.Agents
            .Where(agent => agent.Invocation == SquadInvocation.Primary)
            .ToArray();

        // The current corpus declares exactly one primary agent (conductor); asserting
        // non-empty here keeps the loop below from silently vacuously passing if that ever
        // changes to zero.
        Assert.NotEmpty(primaryAgents);

        foreach (SquadAgent agent in primaryAgents)
        {
            SquadFallbackProfile fallbackProfile = source.FallbackProfiles.Profiles[agent.Fallback];
            Assert.True(
                string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal),
                $"Primary agent '{agent.Name}' fallback profile '{agent.Fallback}' must declare no-primary-agent: skill.");

            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".cursor/agents/{agent.Name}.md");

            SquadDeploymentFile skillFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".cursor/skills/{agent.Name}/SKILL.md");

            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(
                Encoding.UTF8.GetString(skillFile.Content.Span),
                agent.Name);

            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name", agent.Name));
            string expectedDescription = string.Join(" ", agent.Description.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.True(
                string.Equals(expectedDescription, RequireScalar(frontmatter, "description", agent.Name), StringComparison.Ordinal),
                $"Lowered primary agent '{agent.Name}' description mismatch.");
            Assert.DoesNotContain('\n', RequireScalar(frontmatter, "description", agent.Name));
            Assert.True(
                string.Equals("MIT", RequireScalar(frontmatter, "license", agent.Name), StringComparison.Ordinal),
                $"Lowered primary agent '{agent.Name}' license mismatch.");

            // Auto-load stays allowed (Q2): no disable-model-invocation key, and no other
            // keys beyond the approved triple.
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("disable-model-invocation")),
                $"Lowered primary agent '{agent.Name}' must not carry 'disable-model-invocation' (auto-load stays allowed).");
            string[] actualKeys = frontmatter.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(key => key.Value ?? string.Empty)
                .ToArray();
            Assert.Equal(LoweredSkillFrontmatterKeys, actualKeys);

            string expectedBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedBody.EndsWith('\n'))
            {
                expectedBody += "\n";
            }

            Assert.True(
                string.Equals(expectedBody, body, StringComparison.Ordinal),
                $"Lowered primary agent '{agent.Name}' body mismatch.");

            foreach (SquadResource resource in agent.Resources)
            {
                Assert.Contains(
                    result.Files,
                    f => f.RelativePath == $".cursor/skills/{agent.Name}/{resource.RelativePath}");
            }
        }
    }

    /// <summary>
    /// The lowered primary-agent skill follows Cursor's global-scope roots: without the
    /// <c>.cursor/</c> prefix, at <c>skills/&lt;name&gt;/SKILL.md</c> with its resource
    /// closure beside it, and no agent file is emitted in either scope root.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Cursor_LoweredPrimarySkillUsesGlobalScopePaths()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(source.Agents, agent => agent.Invocation == SquadInvocation.Primary);

        SquadRendererRegistry registry = new([new CursorRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Global);

        SquadRenderResult result = await registry.RenderAsync(request);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        Assert.DoesNotContain(result.Files, f => f.RelativePath == $"agents/{primaryAgent.Name}.md");
        Assert.DoesNotContain(result.Files, f => f.RelativePath == $".cursor/agents/{primaryAgent.Name}.md");
        Assert.DoesNotContain(result.Files, f => f.RelativePath == $".cursor/skills/{primaryAgent.Name}/SKILL.md");

        SquadDeploymentFile skillFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $"skills/{primaryAgent.Name}/SKILL.md");
        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(skillFile.Content.Span),
            primaryAgent.Name);
        Assert.Equal(primaryAgent.Name, RequireScalar(frontmatter, "name", primaryAgent.Name));

        foreach (SquadResource resource in primaryAgent.Resources)
        {
            Assert.Single(
                result.Files,
                f => f.RelativePath == $"skills/{primaryAgent.Name}/{resource.RelativePath}");
        }
    }

    /// <summary>
    /// The no-primary-agent setting switches only the lowered skill. Skill and omit modes
    /// never emit an agent file for the primary; they differ only in whether the lowered
    /// skill and its resource closure are emitted.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Cursor_NoPrimaryAgentValueSwitchesOnlyTheLoweredSkill()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(source.Agents, agent => agent.Invocation == SquadInvocation.Primary);

        SquadRendererRegistry registry = new([new CursorRenderer()]);
        SquadRenderRequest skillRequest = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Project);
        SquadRenderResult skillResult = await registry.RenderAsync(skillRequest);
        Assert.True(skillResult.Success, string.Join("; ", skillResult.Errors));

        using ClaudeNoPrimaryAgentFixture omitFixture = ClaudeNoPrimaryAgentFixture.Create("omit");
        SquadRenderRequest omitRequest = new(
            SourceDirectory: omitFixture.ProductRoot,
            Targets: [SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Project);
        SquadRenderResult omitResult = await registry.RenderAsync(omitRequest);
        Assert.True(omitResult.Success, string.Join("; ", omitResult.Errors));

        Assert.DoesNotContain(
            skillResult.Files,
            f => f.RelativePath == $".cursor/agents/{primaryAgent.Name}.md");
        Assert.DoesNotContain(
            omitResult.Files,
            f => f.RelativePath == $".cursor/agents/{primaryAgent.Name}.md");
        Assert.DoesNotContain(
            omitResult.Files,
            f => f.RelativePath.StartsWith($".cursor/skills/{primaryAgent.Name}/", StringComparison.Ordinal));

        foreach (SquadDeploymentFile omitFile in omitResult.Files)
        {
            Assert.Contains(skillResult.Files, skillFile => skillFile.RelativePath == omitFile.RelativePath);
        }

        HashSet<string> skillPaths = new(skillResult.Files.Select(f => f.RelativePath));
        HashSet<string> omitPaths = new(omitResult.Files.Select(f => f.RelativePath));
        skillPaths.ExceptWith(omitPaths);

        string[] expectedSkillPaths = new string[1 + primaryAgent.Resources.Count];
        expectedSkillPaths[0] = $".cursor/skills/{primaryAgent.Name}/SKILL.md";
        for (int i = 0; i < primaryAgent.Resources.Count; i++)
        {
            expectedSkillPaths[i + 1] = $".cursor/skills/{primaryAgent.Name}/{primaryAgent.Resources[i].RelativePath}";
        }

        Assert.Equal(
            expectedSkillPaths.OrderBy(p => p, StringComparer.Ordinal),
            skillPaths.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// Fail-closed on identity collision (Pi-renderer precedent): if a canonical skill
    /// already occupies the identity the primary agent would lower to, Cursor has no
    /// role-prefixed fallback for it, so rendering throws
    /// <see cref="SquadRenderValidationException"/> naming the identity.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Cursor_ThrowsWhenACanonicalSkillOccupiesTheLoweredPrimaryIdentity()
    {
        SquadSource baselineSource = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(baselineSource.Agents, agent => agent.Invocation == SquadInvocation.Primary);

        using PiPrimaryIdentityCollisionFixture fixture = PiPrimaryIdentityCollisionFixture.Create(primaryAgent.Name);

        CursorRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: fixture.ProductRoot,
            Targets: [SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Project);

        SquadRenderValidationException exception = await Assert.ThrowsAsync<SquadRenderValidationException>(
            () => renderer.RenderAsync(request));

        Assert.Contains(primaryAgent.Name, exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task RenderAsync_Cursor_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new CursorRenderer(),
            SquadTarget.Cursor,
            ".cursor/agents/bug-crusher-investigator.md",
            ".cursor/agents",
            ".cursor/skills");
    }

    private static void AssertReadOnly(SquadRenderResult result, string agentName, bool expectedReadOnly)
    {
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == $".cursor/agents/{agentName}.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(file.Content.Span),
            agentName);
        if (expectedReadOnly)
        {
            Assert.True(
                string.Equals("true", RequireScalar(frontmatter, "readonly", agentName), StringComparison.Ordinal),
                $"Agent '{agentName}' should carry readonly: true.");
        }
        else
        {
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("readonly")),
                $"Agent '{agentName}' should omit readonly.");
        }
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
