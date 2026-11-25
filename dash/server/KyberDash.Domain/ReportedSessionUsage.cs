using KyberDash.Data.Collector;

namespace KyberDash.Domain;

/// <summary>
/// Session token totals taken from the harness's reported session usage
/// (<c>session_model_usage</c>), used only when no record in the session carries its own token
/// counters.
/// </summary>
/// <param name="FreshInput">Summed <c>input</c> class, or <see langword="null"/> when not reported.</param>
/// <param name="CacheRead">Summed <c>cache_read</c> class, or <see langword="null"/> when not reported.</param>
/// <param name="CacheWrite">Summed <c>cache_write</c> class, or <see langword="null"/> when not reported.</param>
/// <param name="Output">Summed <c>output</c> class, or <see langword="null"/> when not reported.</param>
/// <param name="Total">Summed <c>total</c> class: a figure reported with no input/output split, or <see langword="null"/> when the harness reported a split or nothing.</param>
public sealed record ReportedSessionUsageTotals(long? FreshInput, long? CacheRead, long? CacheWrite, long? Output, long? Total)
{
    /// <summary>Gets the combined input (fresh input plus both cache classes), matching the per-turn figure.</summary>
    public long? CombinedInput => FreshInput.HasValue || CacheRead.HasValue || CacheWrite.HasValue
        ? (FreshInput ?? 0L) + (CacheRead ?? 0L) + (CacheWrite ?? 0L)
        : null;
}

/// <summary>
/// Resolves the session totals a harness reported for the whole session when its records carry
/// no token counters of their own. Shared by every store so the sessions list, the summary, and
/// the in-memory store answer one rule.
/// </summary>
/// <remarks>
/// <para>
/// What a snapshot means is the harness's, not the store's, so it is read per harness from what
/// each mapper writes:
/// </para>
/// <list type="bullet">
/// <item>A session with one snapshot position: the figures are summed across models. Droid
/// (<c>DroidSessionMapper.BuildReportedUsage</c>) and Warp (<c>WarpMapper.ExtractConversationUsage</c>)
/// only ever write position 0.</item>
/// <item>Copilot CLI writes one snapshot per <c>session.shutdown</c> leg, and each leg restates the
/// CLI's cumulative per-model counters, so the latest value per model and token class is the
/// total. A counter that goes down between legs is the CLI resetting (in-session compaction), and
/// the usage before the reset is not in any snapshot, so the totals are not measured.</item>
/// <item>Several snapshot positions from any other harness: nothing evidences whether they restate
/// or add up, so the totals are not measured, never guessed.</item>
/// </list>
/// <para>
/// Reported figures are summed, never split or estimated (decision D15). A reported total with no
/// split stays a total; mixed with split classes it is ambiguous and not measured.
/// </para>
/// </remarks>
public static class ReportedSessionUsage
{
    /// <summary>Harness identifier whose snapshots are cumulative per model (the Copilot CLI mapper's identifier).</summary>
    public const string CopilotCliHarnessId = "copilot-cli";

    /// <summary>
    /// Resolves the reported totals for <paramref name="session"/>, or <see langword="null"/> when
    /// record sums stay the only source: the records carry counters, nothing was reported, or the
    /// snapshots cannot be read to a total.
    /// </summary>
    /// <param name="session">The session whose reported usage is read.</param>
    /// <param name="records">The session's records.</param>
    public static ReportedSessionUsageTotals? Resolve(CollectedSession session, IReadOnlyList<CollectedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(records);

        if (session.ReportedUsage is not { Count: > 0 } usage || records.Any(static r => r.Tokens.Count > 0))
        {
            return null;
        }

        IReadOnlyList<CollectedModelUsage>? effective = SelectEffectiveRows(session.HarnessId, usage);
        return effective is null ? null : Sum(effective);
    }

    private static IReadOnlyList<CollectedModelUsage>? SelectEffectiveRows(string harnessId, IReadOnlyList<CollectedModelUsage> usage)
    {
        if (usage.Select(static u => u.SnapshotPosition).Distinct().Count() == 1)
        {
            return usage;
        }

        return string.Equals(harnessId, CopilotCliHarnessId, StringComparison.Ordinal)
            ? LatestCumulativeRows(usage)
            : null;
    }

    private static List<CollectedModelUsage>? LatestCumulativeRows(IReadOnlyList<CollectedModelUsage> usage)
    {
        List<CollectedModelUsage> latest = [];
        foreach (IGrouping<(string Model, TokenClass TokenClass), CollectedModelUsage> series in usage
            .GroupBy(static u => (u.Model, u.TokenClass))
            .OrderBy(static g => g.Key.Model, StringComparer.Ordinal)
            .ThenBy(static g => g.Key.TokenClass))
        {
            CollectedModelUsage? previous = null;
            foreach (CollectedModelUsage row in series.OrderBy(static u => u.SnapshotPosition))
            {
                if (previous is not null && row.Count < previous.Count)
                {
                    return null;
                }

                previous = row;
            }

            latest.Add(previous!);
        }

        return latest;
    }

    private static ReportedSessionUsageTotals? Sum(IReadOnlyList<CollectedModelUsage> rows)
    {
        long? freshInput = null;
        long? cacheRead = null;
        long? cacheWrite = null;
        long? output = null;
        long? total = null;
        foreach (CollectedModelUsage row in rows)
        {
            switch (row.TokenClass)
            {
                case TokenClass.Input:
                    freshInput = (freshInput ?? 0L) + row.Count;
                    break;
                case TokenClass.CacheRead:
                    cacheRead = (cacheRead ?? 0L) + row.Count;
                    break;
                case TokenClass.CacheWrite:
                    cacheWrite = (cacheWrite ?? 0L) + row.Count;
                    break;
                case TokenClass.Output:
                    output = (output ?? 0L) + row.Count;
                    break;
                case TokenClass.Total:
                    total = (total ?? 0L) + row.Count;
                    break;
                default:
                    break;
            }
        }

        bool hasSplit = freshInput.HasValue || cacheRead.HasValue || cacheWrite.HasValue || output.HasValue;
        return total.HasValue && hasSplit
            ? null
            : new ReportedSessionUsageTotals(freshInput, cacheRead, cacheWrite, output, total);
    }
}
