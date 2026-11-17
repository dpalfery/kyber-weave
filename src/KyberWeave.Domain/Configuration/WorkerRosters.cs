namespace KyberWeave.Core.Configuration;

/// <summary>
/// The worker roster a run dispatches from. After the removal there is one: the three developer agents by
/// difficulty. <see cref="Technology"/> names the roster the removal deleted; it is read only to recognise a
/// value written before the removal, and never selects a dispatch.
/// </summary>
public static class WorkerRosters
{
    /// <summary>
    /// The per-technology worker agents the removal deleted. No file may select it any more: a host
    /// configuration that still carries it is an error, and a task file that records it is read as
    /// <see cref="Tiered"/> with a warning.
    /// </summary>
    public const string Technology = "technology";

    /// <summary>The three developer agents by difficulty: <c>dev-easy</c>, <c>dev-medium</c>, <c>dev-hard</c>.</summary>
    public const string Tiered = "tiered";

    /// <summary>The rosters a host may select, in the order a diagnostic lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Tiered];

    /// <summary>True when <paramref name="value"/> is the roster a host may select.</summary>
    public static bool IsKnown(string? value) => string.Equals(value?.Trim(), Tiered, StringComparison.Ordinal);
}
