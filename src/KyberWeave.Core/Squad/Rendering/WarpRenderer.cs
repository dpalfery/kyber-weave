using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Warp workspace skills via role-skill
/// lowering (Warp has no native agent primitive).
/// </summary>
/// <remarks>
/// <para>
/// Warp is a fallback target in <see cref="SquadRendererRegistry"/>: every agent
/// lowers to a skill in <c>.warp/skills/</c>, profile-declared shared identities
/// reuse their canonical skill, and distinct-body collisions emit both the canonical
/// skill and a <c>role-</c>-prefixed skill. Unoccupied identities, including
/// conductors when no same-name canonical skill exists, lower directly to their agent
/// name. Deployed <c>SKILL.md</c> files carry no capability enforcement, so non-deny
/// profile decisions are recorded as <c>permission-not-expressible</c> rather than
/// invented into frontmatter.
/// </para>
/// </remarks>
public sealed class WarpRenderer : SquadHarnessRenderer
{
    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.Warp,
        "Warp",
        ".warp",
        AgentsSubdirectory: null,
        SquadModelPolicy.NotRendered,
        SquadLineBreaks.LineFeedOnly);

    public WarpRenderer()
        : base(HarnessSpec)
    {
    }

    /// <summary>
    /// Skills first, then each agent lowered onto a skill, then resources. Canonical skills are
    /// emitted before lowered agents to preserve the established principal ordering; resource
    /// projection follows canonical source ownership, agents first, with each closure already sorted
    /// by the loader.
    /// </summary>
    private protected override SquadRenderResult Render(SquadHarnessRenderContext context)
    {
        foreach (SquadSkill skill in context.Source.Skills)
        {
            context.Files.Add(RenderSkill(context, skill.Name, skill.Description, skill.InstructionBody));
        }

        foreach (SquadAgent agent in context.Source.Agents)
        {
            RenderAgent(context, agent);
        }

        // The extra agent-relative segment keeps authored links valid when an agent is lowered
        // beneath a role-skill directory.
        foreach (SquadAgent agent in context.Source.Agents)
        {
            SquadResourceProjection.Append(
                context.Files,
                $"{context.SkillsDirectory}/{Lower(context, agent).OutputIdentity}/SKILL.md",
                agent.Resources,
                context.Token);
        }

        foreach (SquadSkill skill in context.Source.Skills)
        {
            SquadResourceProjection.Append(
                context.Files,
                $"{context.SkillsDirectory}/{skill.Name}/SKILL.md",
                skill.Resources,
                context.Token);
        }

        return context.ToResult();
    }

    /// <summary>Lowers one agent onto a skill, unless it reuses its canonical skill.</summary>
    private protected override void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent)
    {
        (WarpLowering lowering, string outputIdentity) = Lower(context, agent);
        switch (lowering)
        {
            case WarpLowering.ReuseCanonicalSkill:
                // Shared identity: the canonical skill was already emitted, so emitting a
                // role-prefixed duplicate would violate the single-projection rule the registry
                // enforces. This covers both shapes: a subagent shared identity whose body is
                // identical to the skill's, and a primary shared identity whose body is the thin
                // wrapper pointing at it.
                context.Degradations.Add(context.Degradation(
                    agent,
                    "role-skill-fallback",
                    "Reused the same-name canonical skill as the entry point; no separate " +
                    "agent projection is emitted on a target with no agent primitive."));
                break;
            case WarpLowering.RolePrefixedSkill:
                context.Files.Add(RenderSkill(context, outputIdentity, agent.Description, agent.InstructionBody));
                context.Degradations.Add(new SquadDegradationRecord(
                    context.Token,
                    agent.Name,
                    outputIdentity,
                    "role-skill-fallback",
                    agent.BodyDigest,
                    "Agent lowered to role-prefixed skill to preserve distinct canonical skill."));
                break;
            default:
                context.Files.Add(RenderSkill(context, outputIdentity, agent.Description, agent.InstructionBody));
                context.Degradations.Add(context.Degradation(agent, "role-skill-fallback", "Agent lowered to role skill."));
                break;
        }

        context.AddDegradation(BuildPermissionDegradation(context, agent));
    }

    /// <summary>
    /// Decides how an agent lowers. An identity a canonical skill also holds either reuses that
    /// skill (a profile-declared shared identity) or lowers beside it under a <c>role-</c> prefix
    /// (a distinct-body collision); an unoccupied identity, including a conductor with no same-name
    /// skill, lowers under its own name. Occupancy is read from the loaded source, so a corpus change
    /// that creates or removes a collision updates the lowering without a renderer change.
    /// </summary>
    private static (WarpLowering Lowering, string OutputIdentity) Lower(SquadHarnessRenderContext context, SquadAgent agent)
    {
        if (!context.SkillNames.Contains(agent.Name))
        {
            return (WarpLowering.RoleSkill, agent.Name);
        }

        return ResolveSharedIdentities(context.Source).Contains(agent.Name)
            ? (WarpLowering.ReuseCanonicalSkill, agent.Name)
            : (WarpLowering.RolePrefixedSkill, $"role-{agent.Name}");
    }

    /// <summary>
    /// Shared identities (the fallback profile's <c>shared-identities</c> list) whose
    /// canonical skill is reused instead of emitting a role-prefixed duplicate. Read from
    /// the loaded fallback profile so a change to profiles/fallbacks.yml is honored
    /// without a renderer change.
    /// </summary>
    private static HashSet<string> ResolveSharedIdentities(SquadSource source)
    {
        if (!source.FallbackProfiles.Profiles.TryGetValue("role-skill", out SquadFallbackProfile? profile))
        {
            return [];
        }

        return profile.SharedIdentities.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Canonical names become directory names in the rendered tree, so a name carrying path syntax
    /// is rejected here, before any file is built, rather than at deployment time.
    /// </summary>
    private static SquadDeploymentFile RenderSkill(
        SquadHarnessRenderContext context,
        string name,
        string description,
        string instructionBody)
    {
        SquadHarnessRenderContext.ValidateCanonicalName(name);
        return context.RenderSkillDocument(name, description, instructionBody);
    }

    private static SquadDegradationRecord? BuildPermissionDegradation(
        SquadHarnessRenderContext context,
        SquadAgent agent)
    {
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles = context.CapabilityProfiles;
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            throw new SquadRenderValidationException(
                $"Agent '{agent.Name}' references capability profile '{agent.CapabilityProfile}', " +
                "which profiles/capabilities.yml does not declare. " +
                $"Declared profiles: {string.Join(", ", capabilityProfiles.Keys.Order(StringComparer.Ordinal))}. " +
                "Correct the agent's capability-profile value before rendering.");
        }

        // Warp skills are instruction-only: any non-deny decision is unenforceable
        // at the harness boundary, so it is named rather than invented into frontmatter.
        List<string> constrained = context.CapabilityVocabulary
            .Where(capability =>
                profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision != SquadPermissionDecision.Deny)
            .ToList();

        if (constrained.Count == 0)
        {
            return null;
        }

        return context.Degradation(
            agent,
            "permission-not-expressible",
            $"Capability profile '{agent.CapabilityProfile}' constrains " +
            $"{string.Join(", ", constrained)} but Warp skills cannot express " +
            "capability permissions; the deployed skill's behaviour is governed by the " +
            "harness default, not the canonical profile.");
    }

    private enum WarpLowering
    {
        RoleSkill,
        RolePrefixedSkill,
        ReuseCanonicalSkill
    }
}
