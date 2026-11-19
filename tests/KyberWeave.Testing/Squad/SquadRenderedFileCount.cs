using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// Computes how many files an R17 render writes for one target, from the loaded Squad source rather than a
/// hardcoded literal, so a receipt assertion cannot drift from the source it describes.
/// </summary>
public static class SquadRenderedFileCount
{
    /// <summary>
    ///     R17:
    ///     <c>
    ///         Agents.Count + Σ agent resources + Skills.Count − shared-identity skills +
    ///         Σ non-suppressed skill resources
    ///     </c>
    ///     , plus the entry-point skill a target that keeps the native principal adds for a non-shared
    ///     primary, and minus the agent principal a lowering target replaces with that entry point.
    /// </summary>
    /// <param name="source">The loaded Squad source the render starts from.</param>
    /// <param name="target">The harness target the render writes for.</param>
    /// <returns>The number of files the receipt for that target must hold.</returns>
    public static int For(SquadSource source, SquadTarget target)
    {
        ArgumentNullException.ThrowIfNull(source);

        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

        // A subagent shared identity suppresses its canonical skill; a primary shared identity
        // reuses it as the entry point and emits it, so nothing is suppressed here.
        HashSet<string> suppressedSkillIdentities =
            SquadSharedIdentityProjection.SuppressedSkillIdentities(source, sharedIdentities);
        int suppressedSkillCount = source.Skills.Count(skill => suppressedSkillIdentities.Contains(skill.Name));

        int baseCount = source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
                                            + source.Skills.Count - suppressedSkillCount
                        + source.Skills.Where(skill => !suppressedSkillIdentities.Contains(skill.Name))
                            .Sum(skill => skill.Resources.Count);

        // A primary shared identity emits no agent principal of its own: its canonical skill is the
        // entry point and is already counted in baseCount. That subtracts one principal per shared
        // identity on a target that lowers primaries, and adds nothing on a target that keeps the
        // agent file.
        int sharedPrimaryCount = source.Agents.Count(agent =>
            agent.Invocation == SquadInvocation.Primary && sharedIdentities.Contains(agent.Name));

        // Codex, Claude, and Factory keep the native principal and add an entry-point skill beside
        // it, but only for a non-shared primary: a primary shared identity already has its
        // canonical skill as the entry point. The lowering targets (Cursor, Pi, ZCode, Devin, Warp)
        // replace a non-shared primary's agent principal with an entry point, and emit nothing at all
        // for a shared one.
        if (target is SquadTarget.Codex or SquadTarget.Claude or SquadTarget.Factory)
        {
            int entryPointAddition = 0;
            foreach (SquadAgent agent in source.Agents.Where(agent =>
                         agent.Invocation == SquadInvocation.Primary && !sharedIdentities.Contains(agent.Name)))
            {
                string fallbackProfileId = agent.Fallback;
                if (!source.FallbackProfiles.Profiles.TryGetValue(fallbackProfileId, out SquadFallbackProfile? profile))
                {
                    throw new InvalidOperationException(
                        $"Agent '{agent.Name}' names fallback profile '{fallbackProfileId}', " +
                        "which the loaded Squad source does not hold.");
                }

                if (profile.NoPrimaryAgent == "skill")
                    // Add 1 for the SKILL.md and agent.Resources.Count for the resources
                    entryPointAddition += 1 + agent.Resources.Count;
            }

            return baseCount + entryPointAddition;
        }

        if (target is SquadTarget.Cursor or SquadTarget.Pi or SquadTarget.ZCode
            or SquadTarget.Devin or SquadTarget.Warp)
            return baseCount - sharedPrimaryCount;

        return baseCount;
    }
}
