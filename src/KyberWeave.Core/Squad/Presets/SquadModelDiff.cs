using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Squad.Model;

/// <summary>
///     A model diff item representing a changed model resolution for an agent.
/// </summary>
/// <param name="Agent">The agent canonical name.</param>
/// <param name="OldModel">The previously resolved model token, or <c>null</c> if inherited or unset.</param>
/// <param name="NewModel">The newly resolved model token, or <c>null</c> if inherited or unset.</param>
public sealed record SquadModelDiffItem(string Agent, string? OldModel, string? NewModel);

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
            string? oldModel = SquadModelResolution.ResolveToken(before, agent.ModelProfile, target);
            string? newModel = SquadModelResolution.ResolveToken(after, agent.ModelProfile, target);

            if (!string.Equals(oldModel, newModel, StringComparison.Ordinal))
                diffs.Add(new SquadModelDiffItem(agent.Name, oldModel, newModel));
        }

        diffs.Sort((a, b) => StringComparer.Ordinal.Compare(a.Agent, b.Agent));
        return diffs;
    }

    /// <summary>
    ///     Computes the diff items between <paramref name="before" /> and <paramref name="after" /> model profiles
    ///     for all agents in <paramref name="source" /> targeting harness <paramref name="target" />.
    /// </summary>
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
