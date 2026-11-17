namespace KyberWeave.Core.Configuration;

/// <summary>The two worker rosters a run may dispatch from. The values are the host configuration's and an agent's.</summary>
public static class WorkerRosters
{
    /// <summary>The per-technology worker agents: today's roster, and the default until removal.</summary>
    public const string Technology = "technology";

    /// <summary>The three developer agents by difficulty: <c>dev-easy</c>, <c>dev-medium</c>, <c>dev-hard</c>.</summary>
    public const string Tiered = "tiered";

    /// <summary>Both values, in the order a diagnostic lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Technology, Tiered];

    /// <summary>True when <paramref name="value"/> is one of the two rosters.</summary>
    public static bool IsKnown(string? value) =>
        string.Equals(value, Technology, StringComparison.Ordinal)
        || string.Equals(value, Tiered, StringComparison.Ordinal);
}
