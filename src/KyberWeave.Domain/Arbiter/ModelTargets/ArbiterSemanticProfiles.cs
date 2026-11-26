using KyberWeave.Core.Agents.Roster;

namespace KyberWeave.Core.Arbiter.ModelTargets;

/// <summary>
/// The semantic profile names the Arbiter accepts in its model-targeting keys. The set mirrors the
/// profile keys Kyber-Squad ships, and a parity test keeps the two in step.
/// </summary>
public static class ArbiterSemanticProfiles
{
    /// <summary>Every semantic profile name, in alphabetical order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "architect",
        "deep-planning",
        DeveloperTiers.Hard,
        "fast",
        "general",
        "reviewer",
    ];
}
