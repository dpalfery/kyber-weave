using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Squad.Model;

/// <summary>
///     Computes model token diffs across agents for target harnesses between two profile configurations.
/// </summary>
public static class SquadModelDiff
{
    /// <summary>
    ///     Computes the diff items between <paramref name="before" /> and <paramref name="after" /> model profiles
    ///     for all agents in <paramref name="source" /> targeting harness <paramref name="target" />.
    /// </summary>
    /// <param name="source">The canonical Squad source containing agents.</param>
    /// <param name="target">The harness target name.</param>
    /// <param name="before">The model profiles before the change.</param>
    /// <param name="after">The model profiles after the change.</param>
    /// <returns>A list of changed model resolutions, ordered ordinally by agent name.</returns>
    public static IReadOnlyList<SquadModelDiffItem> Compute(
        SquadSource source,
        string target,
        SquadModelProfiles before,
        SquadModelProfiles after)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        List<SquadModelDiffItem> diffs = new();

        foreach (SquadAgent agent in source.Agents)
        {
            // An agent with no model profile (the conductor) runs on its session's model, so a
            // preset change cannot move it and there is no token to diff.
            if (string.IsNullOrWhiteSpace(agent.ModelProfile))
            {
                continue;
            }

            string? oldModel = SquadModelResolution.ResolveToken(before, agent.ModelProfile, target);
            string? newModel = SquadModelResolution.ResolveToken(after, agent.ModelProfile, target);

            if (!string.Equals(oldModel, newModel, StringComparison.Ordinal))
                diffs.Add(new SquadModelDiffItem(agent.Name, oldModel, newModel));
        }

        diffs.Sort((a, b) => StringComparer.Ordinal.Compare(a.Agent, b.Agent));
        return diffs;
    }

    /// <inheritdoc cref="Compute(SquadSource, string, SquadModelProfiles, SquadModelProfiles)" path="/summary" />
    /// <param name="source">The canonical Squad source containing agents.</param>
    /// <param name="target">The harness target enum value.</param>
    /// <param name="before">The model profiles before the change.</param>
    /// <param name="after">The model profiles after the change.</param>
    /// <returns>A list of changed model resolutions, ordered ordinally by agent name.</returns>
    public static IReadOnlyList<SquadModelDiffItem> Compute(
        SquadSource source,
        SquadTarget target,
        SquadModelProfiles before,
        SquadModelProfiles after)
    {
        return Compute(source, SquadTargetCatalog.GetToken(target), before, after);
    }
}
