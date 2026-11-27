using KyberWeave.Core.Agents.Roster;

namespace KyberWeave.Core.Arbiter.ModelTargets;

/// <summary>
/// The shipped developer tiers' model profiles: the profile each difficulty targets, and the ladder a
/// retarget climbs. The values follow the three developer tiers in
/// <c>products/kyber-squad/profiles/models.yml</c> (<c>dev-easy</c>, <c>dev-medium</c> and <c>dev-hard</c>).
/// </summary>
/// <param name="DifficultyProfiles">The profile each difficulty (<c>easy</c>, <c>standard</c>, <c>hard</c>) targets.</param>
/// <param name="Ladder">The ordered profiles a retarget may climb, cheapest first.</param>
public sealed record TierProfiles(
    IReadOnlyDictionary<string, string> DifficultyProfiles,
    IReadOnlyList<string> Ladder)
{
    /// <summary>The shipped table and ladder: easy to fast, standard to general, hard to dev-hard.</summary>
    public static TierProfiles Default { get; } = new(DeveloperTiers.Profiles, DeveloperTiers.Ladder);
}
