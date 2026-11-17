using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The caller's <c>delegates-to</c> for the run's worker roster, which is the roster rule
/// (<c>KW-ARB-ROSTER-001</c>) and the run-start rule (<c>KW-ARB-RUN-001</c>) both read.
/// </summary>
/// <remarks>
/// The Squad catalog the Arbiter reads is embedded in the running binary, so its snapshot can be older than the
/// Squad the host deployed: a second end-to-end tiered run was judged by a <c>kyber-weave-arbiter</c> built before
/// the developer tiers existed, and it refused <c>dev-medium</c> although the deployed conductor listed it. The
/// roster, unlike the snapshot, is always current: it is the host's <c>worker-roster</c> setting. So under
/// <c>tiered</c> this projection applies the one roster rule the renderer applies to the deployed files — a
/// technology agent is replaced by the agent the removal map names (<c>csharp-dev</c> by a developer tier,
/// <c>azure-reader</c> by <c>platform-researcher</c>, <c>github-devops</c> by <c>devops</c>) — and it completes the
/// developer ladder, so a task's difficulty and failed attempts can choose any of the three tiers.
/// Under <c>technology</c> the list is returned untouched.
/// </remarks>
public static class RosterDelegatesTo
{
    /// <summary>The shipped rule that asks whether the target is in the caller's <c>delegates-to</c>.</summary>
    public const string RuleId = "KW-ARB-ROSTER-001";

    /// <summary>Returns <paramref name="caller"/>'s roster of agents it may dispatch under <paramref name="roster"/>.</summary>
    /// <param name="caller">The calling agent, as the catalog describes it.</param>
    /// <param name="roster">The run's roster: <c>technology</c>, <c>tiered</c>, or null for the default.</param>
    /// <param name="catalog">The catalog the caller was read from.</param>
    public static IReadOnlyList<string> For(
        ArbiterSquadAgent caller,
        string? roster,
        ArbiterSquadCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!string.Equals(roster, WorkerRosters.Tiered, StringComparison.Ordinal))
        {
            return caller.DelegatesTo;
        }

        List<string> translated = [];
        foreach (string name in caller.DelegatesTo)
        {
            string replacement = PlanTaskAgentPick.RemovedAgents.TryGetValue(name, out string? known) ? known : name;
            if (!translated.Contains(replacement, StringComparer.Ordinal))
            {
                translated.Add(replacement);
            }
        }

        // A caller that dispatches no developer never gains one: the roster replaces a developer roster, not a
        // persona. A caller that does gets the whole ladder, because the task's rating and failed attempts pick the
        // rung and only one tier in the snapshot would leave the step-up with nowhere to go.
        IReadOnlyList<string> tiers = PlanTaskAgentPick.DeveloperTiers;
        if (!translated.Any(tiers.Contains))
        {
            return translated;
        }

        List<string> projected = [.. translated.Where(name => !tiers.Contains(name))];
        projected.AddRange(tiers);
        return projected;
    }

    /// <summary>
    /// The sentence a tiered run's refusal gives for a dispatch to <paramref name="target"/> when that agent is a
    /// <c>worker-roster: technology</c> developer, or null when the dispatch is not one. It names the tier the task's
    /// rating gives, so the conductor can dispatch the right agent from the refusal alone.
    /// </summary>
    /// <param name="target">The dispatched agent.</param>
    /// <param name="roster">The run's roster.</param>
    /// <param name="difficulty">The task's recorded rating, or null when none is recorded.</param>
    /// <param name="catalog">The catalog the target was read from.</param>
    public static string? TechnologyDeveloperRefusal(
        string? target,
        string? roster,
        string? difficulty,
        ArbiterSquadCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrWhiteSpace(target)
            || !string.Equals(roster, WorkerRosters.Tiered, StringComparison.Ordinal)
            || !IsTechnologyDeveloper(target.Trim(), catalog))
        {
            return null;
        }

        return PlanTaskAgentPick.TieredDeveloperRefusal(target.Trim(), difficulty);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a technology developer: an implementation agent the catalog declares on the
    /// technology roster, or a name the removal map replaces with a developer tier (which is how an older catalog's
    /// snapshot, written before agents declared a roster, is read).
    /// </summary>
    private static bool IsTechnologyDeveloper(string name, ArbiterSquadCatalog catalog)
    {
        if (PlanTaskAgentPick.RemovedAgents.TryGetValue(name, out string? replacement)
            && PlanTaskAgentPick.DeveloperTiers.Contains(replacement, StringComparer.Ordinal))
        {
            return true;
        }

        return catalog.Agents.TryGetValue(name, out ArbiterSquadAgent? agent)
            && string.Equals(agent.WorkerRoster, WorkerRosters.Technology, StringComparison.Ordinal)
            && string.Equals(agent.TargetClass, "implementation", StringComparison.Ordinal);
    }
}
