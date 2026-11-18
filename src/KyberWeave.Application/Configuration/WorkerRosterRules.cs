namespace KyberWeave.Core.Configuration;

/// <summary>Checks a worker roster name against the rosters a host may select.</summary>
public static class WorkerRosterRules
{
    /// <summary>True when <paramref name="value"/> is the roster a host may select.</summary>
    public static bool IsKnown(string? value) =>
        string.Equals(value?.Trim(), WorkerRosters.Tiered, StringComparison.Ordinal);
}
