using KyberDash.Contracts.Repositories;

namespace KyberDash.Application;

/// <summary>
/// The small pipeline helpers shared by the canonical engines: harness-prefix stripping for
/// derived run identity and metric reads off persisted harness rollups.
/// </summary>
internal static class CanonPipelineShared
{
    /// <summary>Strips a leading <c>{harness}:</c> prefix from a session seed, when present.</summary>
    /// <param name="harness">Canonical harness identifier.</param>
    /// <param name="seed">Session identifier seed.</param>
    /// <returns>Seed without the harness prefix.</returns>
    internal static string StripHarnessPrefix(string harness, string seed)
    {
        string prefix = $"{harness}:";
        return seed.StartsWith(prefix, StringComparison.Ordinal)
            ? seed[prefix.Length..]
            : seed;
    }

    /// <summary>Reads one metric off a persisted harness rollup, or <see langword="null"/> when absent.</summary>
    /// <param name="rollup">Persisted harness rollup.</param>
    /// <param name="metric">Metric identifier, for example <c>context_pressure_median</c>.</param>
    /// <returns>The metric value, or <see langword="null"/> when the rollup did not record it.</returns>
    internal static double? RollupMetricValue(StoredHarnessRollup rollup, string metric)
    {
        foreach (StoredHarnessRollupMetric entry in rollup.Metrics)
        {
            if (string.Equals(entry.Metric, metric, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }
}
