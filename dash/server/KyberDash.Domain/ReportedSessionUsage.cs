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
public record ReportedSessionUsageTotals(long? FreshInput, long? CacheRead, long? CacheWrite, long? Output, long? Total)
{
    /// <summary>Gets the combined input (fresh input plus both cache classes), matching the per-turn figure.</summary>
    public long? CombinedInput => FreshInput.HasValue || CacheRead.HasValue || CacheWrite.HasValue
        ? (FreshInput ?? 0L) + (CacheRead ?? 0L) + (CacheWrite ?? 0L)
        : null;
}

/// <summary>One model's share of a session's reported usage.</summary>
/// <param name="Model">The model the harness reported the usage for.</param>
/// <param name="Totals">That model's summed token classes.</param>
public record ReportedModelUsageTotals(string Model, ReportedSessionUsageTotals Totals);

/// <summary>A session's published cost priced from its reported usage, with the reasons any part is not measured.</summary>
/// <param name="Cost">The published-basis cost block.</param>
/// <param name="Problems">One warning per model that could not be priced, naming why.</param>
public record ReportedUsageCost(CostBlock Cost, IReadOnlyList<Problem> Problems);

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
        IReadOnlyList<CollectedModelUsage>? effective = ResolveEffectiveRows(session, records);
        return effective is null ? null : Sum(effective);
    }

    /// <summary>
    /// Resolves the same reported usage as <see cref="Resolve"/>, kept per model so each model
    /// can be priced at its own rate, or <see langword="null"/> when <see cref="Resolve"/> is.
    /// </summary>
    /// <param name="session">The session whose reported usage is read.</param>
    /// <param name="records">The session's records.</param>
    public static IReadOnlyList<ReportedModelUsageTotals>? ResolveByModel(CollectedSession session, IReadOnlyList<CollectedRecord> records)
    {
        IReadOnlyList<CollectedModelUsage>? effective = ResolveEffectiveRows(session, records);
        if (effective is null || Sum(effective) is null)
        {
            return null;
        }

        List<ReportedModelUsageTotals> byModel = [];
        foreach (IGrouping<string, CollectedModelUsage> group in effective.GroupBy(static u => u.Model, StringComparer.Ordinal).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            if (Sum([.. group]) is { } totals)
            {
                byModel.Add(new ReportedModelUsageTotals(group.Key, totals));
            }
        }

        return byModel;
    }

    /// <summary>
    /// Prices the session's reported usage at each model's published rate when no record carries
    /// priced tokens, or returns <see langword="null"/> when <see cref="Resolve"/> does. A model
    /// with no published rate, a model whose rate depends on each request's prompt size (a session
    /// total cannot say which tier applied), and a total-only figure for a model whose rates differ
    /// by token class are not measured, each with its reason; no rate is ever derived.
    /// </summary>
    /// <param name="session">The session whose reported usage is priced.</param>
    /// <param name="records">The session's records.</param>
    /// <param name="pricing">Injected pricing, or <see langword="null"/> for the bundled tables.</param>
    public static ReportedUsageCost? Price(CollectedSession session, IReadOnlyList<CollectedRecord> records, IModelPricingLookup? pricing)
    {
        IReadOnlyList<ReportedModelUsageTotals>? byModel = ResolveByModel(session, records);
        if (byModel is null || byModel.Count == 0)
        {
            return null;
        }

        List<CostBlock> blocks = [];
        List<Problem> problems = [];
        foreach (ReportedModelUsageTotals entry in byModel)
        {
            blocks.Add(PriceModel(entry, pricing, problems));
        }

        CostTotalResult total = CanonPricing.SumCosts(blocks);
        return new ReportedUsageCost(total.Total ?? new CostBlock(CostBasis.Published, CostStatus.NoRate), problems);
    }

    private static IReadOnlyList<CollectedModelUsage>? ResolveEffectiveRows(CollectedSession session, IReadOnlyList<CollectedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(records);

        if (session.ReportedUsage is not { Count: > 0 } usage || records.Any(static r => r.Tokens.Count > 0))
        {
            return null;
        }

        return SelectEffectiveRows(session.HarnessId, usage);
    }

    private static CostBlock PriceModel(ReportedModelUsageTotals entry, IModelPricingLookup? pricing, List<Problem> problems)
    {
        string model = entry.Model;
        ReportedSessionUsageTotals totals = entry.Totals;
        if (string.IsNullOrWhiteSpace(model))
        {
            return NotPriced("The reported session usage names no model, so it has no published rate.");
        }

        if (CanonPricing.IsFlatRateModel(model))
        {
            return new CostBlock(CostBasis.Published, CostStatus.NotBilled);
        }

        ModelCosts? costs = CanonPricing.GetModelCosts(model, pricing);
        if (costs is null ||
            !(costs.InputCostPerToken > 0 || costs.OutputCostPerToken > 0 || costs.CacheReadCostPerToken > 0 || costs.CacheWriteCostPerToken > 0))
        {
            return NotPriced($"No published rate exists for model \"{model}\".");
        }

        if (CanonPricing.IsPromptTieredModel(model))
        {
            return NotPriced($"The rate of model \"{model}\" depends on each request's prompt size, which a session total does not carry.");
        }

        if (totals.Total is long totalOnly)
        {
            bool singleRate = costs.InputCostPerToken > 0 &&
                costs.InputCostPerToken == costs.OutputCostPerToken &&
                costs.InputCostPerToken == costs.CacheReadCostPerToken &&
                costs.InputCostPerToken == costs.CacheWriteCostPerToken;
            if (!singleRate)
            {
                return NotPriced($"Model \"{model}\" was reported as one total with no token-class split, and its published rates differ by class.");
            }

            double flat = totalOnly * costs.InputCostPerToken;
            return Priced(model, flat, CostStatus.Priced);
        }

        long fresh = totals.FreshInput ?? 0L;
        long cacheWrite = totals.CacheWrite ?? 0L;
        double billedInput = costs.CacheWriteCostIsExplicit ? fresh : fresh + cacheWrite;
        double billedCacheWrite = costs.CacheWriteCostIsExplicit ? cacheWrite : 0;
        double value = CanonPricing.CalculateCost(
            model,
            billedInput,
            totals.Output ?? 0L,
            billedCacheWrite,
            totals.CacheRead ?? 0L,
            webSearchRequests: 0,
            fast: false,
            oneHourCacheCreationTokens: 0,
            pricing: pricing);

        bool complete = totals.Output.HasValue && totals.CombinedInput.HasValue;
        return Priced(model, value, complete ? CostStatus.Priced : CostStatus.Partial);

        CostBlock NotPriced(string reason)
        {
            problems.Add(new Problem(ProblemSeverity.Warning, "reported-usage-not-priced", reason));
            return new CostBlock(CostBasis.Published, CostStatus.NoRate);
        }

        static CostBlock Priced(string model, double value, CostStatus status) =>
            new(CostBasis.Published, status, value, "USD", new Dictionary<string, double>(StringComparer.Ordinal) { [model] = value });
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
