using System.Text;
using KyberWeave.Tests.Fakes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;
using static KyberWeave.Tests.Fakes.SquadRenderedOutput;

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
    /// <summary>The capabilities Cursor's <c>readonly</c> boolean enforces.</summary>
    private static readonly string[] ReadOnlyEnforcedCapabilities = ["filesystem.write", "process.execute"];

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
    ///     Cursor's own decisions on the real corpus: a subagent is an agent file whose model follows
    ///     the <c>cursor</c> column verbatim and which is <c>readonly</c> exactly when both write and
    ///     execute are withheld; a primary agent never gets an agent file — it lowers onto a skill
    ///     carrying its body, or reuses its canonical skill when the identity is shared; and every
    ///     agent with a non-deny governed capability records what Cursor cannot express. The
    ///     canonical skill documents are the shared pipeline's, checked in
    ///     <see cref="Squad.SquadRendererSharedContractTests"/>.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Cursor_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        HashSet<string> sharedIdentities = SharedIdentities(source);
        SquadRenderResult result = await new SquadRendererRegistry([new CursorRenderer()]).RenderAsync(
            new SquadRenderRequest(ProductRoot, [SquadTarget.Cursor], SquadDeploymentScope.Project));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        foreach (SquadAgent agent in source.Agents.Where(a => a.Invocation == SquadInvocation.Subagent))
        {
            SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == $".cursor/agents/{agent.Name}.md");
            (YamlMappingNode frontmatter, _) = SplitFrontmatter(Encoding.UTF8.GetString(file.Content.Span), agent.Name);

            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            string? expectedModel = modelProfile.HarnessModels.TryGetValue("cursor", out string? cursorModel)
                ? cursorModel
                : modelProfile.Default == "inherit" ? null : modelProfile.Default;
            Assert.Equal(
                expectedModel,
                frontmatter.Children.TryGetValue(new YamlScalarNode("model"), out YamlNode? model) ? ((YamlScalarNode)model).Value : null);

            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            bool expectedReadOnly = !ReadOnlyEnforcedCapabilities.Any(capability =>
                profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision == SquadPermissionDecision.Allow);
            Assert.Equal(expectedReadOnly, frontmatter.Children.ContainsKey(new YamlScalarNode("readonly")));
        }

        SquadAgent[] primaryAgents = [.. source.Agents.Where(a => a.Invocation == SquadInvocation.Primary)];
        Assert.NotEmpty(primaryAgents);
        foreach (SquadAgent primary in primaryAgents)
        {
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".cursor/agents/{primary.Name}.md");
            SquadDeploymentFile skill = Assert.Single(result.Files, f => f.RelativePath == $".cursor/skills/{primary.Name}/SKILL.md");
            (_, string body) = SplitFrontmatter(Encoding.UTF8.GetString(skill.Content.Span), primary.Name);
            string expectedBody = sharedIdentities.Contains(primary.Name)
                ? source.Skills.Single(canonical => canonical.Name == primary.Name).InstructionBody
                : primary.InstructionBody;
            string normalized = expectedBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(normalized.EndsWith('\n') ? normalized : normalized + "\n", body);
        }

        Assert.Equal(
            source.Agents
                .Where(a => GovernedCapabilities.Any(capability =>
                    source.CapabilityProfiles.Profiles[a.CapabilityProfile].Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                    decision != SquadPermissionDecision.Deny))
                .Select(a => a.Name)
                .Order(StringComparer.Ordinal),
            result.Degradations.Select(d => d.CanonicalIdentity).Order(StringComparer.Ordinal));
        Assert.All(result.Degradations, d => Assert.Equal("permission-not-expressible", d.Code));
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
        SquadSource source = TestSquadCorpus.CanonicalSource;
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

        // The corpus declares the conductor plus the two root wrappers; asserting non-empty
        // here keeps the loop below from silently vacuously passing if that ever changes to zero.
        Assert.NotEmpty(primaryAgents);

        // Primary shared identities reuse their canonical skill and carry no lowered skill of
        // their own; they are pinned by the corpus test. This test pins the lowering of the
        // remaining (non-shared) primaries.
        HashSet<string> sharedIdentities = SharedIdentities(source);
        foreach (SquadAgent agent in primaryAgents.Where(a => !sharedIdentities.Contains(a.Name)))
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
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent primaryAgent = Assert.Single(source.Agents, agent => agent.Name == "conductor");

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
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent primaryAgent = Assert.Single(source.Agents, agent => agent.Name == "conductor");

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
        SquadSource baselineSource = TestSquadCorpus.CanonicalSource;
        SquadAgent primaryAgent = Assert.Single(baselineSource.Agents, agent => agent.Name == "conductor");

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
}
