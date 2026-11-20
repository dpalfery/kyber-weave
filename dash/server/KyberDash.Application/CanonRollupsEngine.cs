using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Projects per-harness efficiency rollups with honest unobservability (<c>not_measurable</c>
/// instead of zero) over the relational store contracts, and computes run scorecards and
/// comparisons.
/// </summary>
/// <remarks>
/// <para>
/// The primary constructor projects rollups typed: it reads sessions and read-time aggregates
/// through <see cref="IRelationalStoreReader"/> and writes <c>harness_rollup</c> rows with one
/// <c>harness_rollup_metric</c> row per measurable dimension through <see cref="IProjectionStore"/>.
/// Harnesses without sessions leave no rollup or metric rows behind (never zero-valued filler),
/// and the BL-3 pruned identities (<c>aider</c>, <c>windsurf</c>) are excluded even when observed.
/// </para>
/// <para>
/// A transitional constructor keeps the <see cref="ICanonStoreRepository"/> path for callers not
/// yet migrated; later tasks remove it once the last legacy caller moves over.
/// </para>
/// </remarks>
public class CanonRollupsEngine
{
    private const string NoRollupReason =
        "No live rollup: harness observed on runs but not yet aggregated";

    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Default inactivity window (30 minutes in milliseconds) for heuristic run grouping.</summary>
    public const long DefaultInactivityWindowMs = 30L * 60L * 1000L;

    /// <summary>Ordered coverage dimensions evaluated per harness.</summary>
    public static IReadOnlyList<string> CoverageDimensions { get; } =
    [
        "token_usage",
        "cache_hit_rate",
        "prefix_stability",
        "tool_yield",
        "delegation_overhead",
        "context_pressure",
    ];

    private static readonly string[] RunIdAttributeKeys =
    [
        "gen_ai.run.id",
        "run.id",
        "run_id",
        "runId",
        "gen_ai.workflow.id",
        "workflow.id",
        "workflow_id",
        "task.id",
        "task_id",
    ];

    private static readonly string[] WorkingDirectoryAttributeKeys =
    [
        "process.working_directory",
        "working_directory",
        "cwd",
        "workspace.root",
        "project.path",
        "vcs.repository.name",
        "repo",
    ];

    private static readonly string[] CanonicalRegisteredHarnesses =
    [
        "antigravity",
        "claude-code",
        "cline",
        "codex",
        "copilot",
        "cursor",
        "droid",
        "kilo-code",
        "opencode",
        "pi",
    ];

    /// <summary>Harness identities pruned from rollup projection (BL-3).</summary>
    private static readonly string[] PrunedHarnessIdentities =
    [
        "aider",
        "windsurf",
    ];

    private readonly IRelationalStoreReader? reader;
    private readonly IProjectionStore? projections;
    private readonly ICanonStoreRepository? legacyStore;
    private readonly IPricingRepository pricing;

    /// <summary>
    /// Initializes a relational <see cref="CanonRollupsEngine"/> projecting typed harness rollups
    /// from the normalized relational store.
    /// </summary>
    /// <param name="reader">Relational reader supplying sessions and read-time aggregates.</param>
    /// <param name="projections">Typed projection store receiving harness rollup rows.</param>
    /// <param name="pricing">Model rate-table repository.</param>
    public CanonRollupsEngine(
        IRelationalStoreReader reader,
        IProjectionStore projections,
        IPricingRepository pricing)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(pricing);

        this.reader = reader;
        this.projections = projections;
        this.pricing = pricing;
    }

    /// <summary>
    /// Initializes a transitional <see cref="CanonRollupsEngine"/> over
    /// <see cref="ICanonStoreRepository"/> for callers not yet migrated to the relational contracts.
    /// </summary>
    /// <param name="store">Canonical store repository.</param>
    /// <param name="pricing">Model rate-table repository.</param>
    public CanonRollupsEngine(ICanonStoreRepository store, IPricingRepository pricing)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pricing);

        legacyStore = store;
        this.pricing = pricing;
    }

    /// <summary>
    /// Derives run identity from a batch of canonical records: explicit
    /// <c>run.id</c> yields <c>explicit</c>; otherwise derives from working directory or session.
    /// </summary>
    /// <param name="records">Canonical records belonging to the run.</param>
    /// <param name="acceptDerivedGrouping">Whether working-directory grouping is permitted.</param>
    /// <param name="fallbackRunId">Optional explicit fallback run identifier.</param>
    /// <returns>Derived or explicit run identity.</returns>
    public static DerivedRunIdentity DeriveRunIdentity(
        IReadOnlyList<CanonicalRecord> records,
        bool acceptDerivedGrouping = true,
        string? fallbackRunId = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        OutcomeBlock outcome = CanonFindings.DeriveOutcome(records);

        if (records.Count == 0)
        {
            return new DerivedRunIdentity(
                RunId: fallbackRunId ?? "unknown",
                GroupingBasis: "derived",
                GroupingRule: "session_fallback",
                WorkingDirectory: null,
                Outcome: outcome);
        }

        CanonicalRecord first = records[0];
        string? cwd = FindFirstRawAttribute(records, WorkingDirectoryAttributeKeys);
        string? explicitId = FindFirstRawAttribute(records, RunIdAttributeKeys);
        if (!string.IsNullOrEmpty(explicitId))
        {
            return new DerivedRunIdentity(
                RunId: explicitId,
                GroupingBasis: "explicit",
                GroupingRule: "explicit_run_id",
                WorkingDirectory: cwd,
                Outcome: outcome);
        }

        if (acceptDerivedGrouping && !string.IsNullOrEmpty(cwd))
        {
            long minMs = long.MaxValue;
            foreach (CanonicalRecord record in records)
            {
                if (DateTimeOffset.TryParse(record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
                {
                    long ms = dto.ToUnixTimeMilliseconds();
                    if (ms < minMs)
                    {
                        minMs = ms;
                    }
                }
            }

            long startTime = minMs == long.MaxValue ? 0 : minMs;
            string cleanCwd = SanitizePathSegment(cwd);
            return new DerivedRunIdentity(
                RunId: string.Create(CultureInfo.InvariantCulture, $"derived:{first.Harness}:{cleanCwd}:{startTime}"),
                GroupingBasis: "derived",
                GroupingRule: "working_directory_and_inactivity_window",
                WorkingDirectory: cwd,
                Outcome: outcome);
        }

        string fallback = fallbackRunId ?? first.SessionId ?? first.TraceId ?? first.SpanId;
        string seed = StripLeadingHarnessPrefix(first.Harness, fallback);
        return new DerivedRunIdentity(
            RunId: $"derived:{first.Harness}:{seed}",
            GroupingBasis: "derived",
            GroupingRule: "session_fallback",
            WorkingDirectory: cwd,
            Outcome: outcome);
    }

    /// <summary>Computes the median of a sorted ascending numeric sequence (<c>0</c> if empty).</summary>
    /// <param name="sorted">Sorted ascending sequence.</param>
    /// <returns>Median value or <c>0</c> when empty.</returns>
    public static double ComputeMedian(IReadOnlyList<double> sorted)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        int count = sorted.Count;
        if (count == 0)
        {
            return 0;
        }

        int mid = count / 2;
        return count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Computes the nearest-rank 95th percentile of a sorted ascending sequence (<c>0</c> if empty).</summary>
    /// <param name="sorted">Sorted ascending sequence.</param>
    /// <returns>95th percentile value or <c>0</c> when empty.</returns>
    public static double ComputeP95(IReadOnlyList<double> sorted)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        int count = sorted.Count;
        if (count == 0)
        {
            return 0;
        }

        int index = Math.Clamp((int)Math.Ceiling(0.95 * count) - 1, 0, count - 1);
        return sorted[index];
    }

    /// <summary>
    /// Projects harness rollups from all sessions currently materialized in the backing store.
    /// </summary>
    /// <returns>Projected harness rollup rows ordered by harness identifier.</returns>
    public IReadOnlyList<HarnessRollupRow> ProjectRunsAndHarnesses() =>
        legacyStore is not null ? ProjectRunsAndHarnessesLegacy() : ProjectRunsAndHarnessesRelational();

    /// <summary>
    /// Projects typed <c>harness_rollup</c> rows with one <c>harness_rollup_metric</c> row per
    /// measurable dimension for every harness observed in the relational store. Harnesses without
    /// sessions leave no rows behind, and stale rows (including the BL-3 pruned identities) are
    /// deleted.
    /// </summary>
    /// <returns>Projected harness rollup rows ordered by harness identifier.</returns>
    private IReadOnlyList<HarnessRollupRow> ProjectRunsAndHarnessesRelational()
    {
        _ = pricing.LoadPricingTables();

        Dictionary<string, List<CollectedSession>> sessionsByHarness = new(StringComparer.Ordinal);
        foreach (CollectedSession session in reader!.Sessions())
        {
            string normalized = CanonPricing.NormalizeHarnessName(session.HarnessId);
            if (CanonPricing.IsExcludedHarnessIdentity(normalized) || IsPrunedHarnessIdentity(normalized))
            {
                continue;
            }

            if (!sessionsByHarness.TryGetValue(normalized, out List<CollectedSession>? harnessSessions))
            {
                harnessSessions = [];
                sessionsByHarness[normalized] = harnessSessions;
            }

            harnessSessions.Add(session);
        }

        Dictionary<string, List<StoredRun>> runsByHarness = new(StringComparer.Ordinal);
        foreach (StoredRun run in projections!.ListRuns())
        {
            string normalized = CanonPricing.NormalizeHarnessName(run.HarnessId);
            if (!runsByHarness.TryGetValue(normalized, out List<StoredRun>? harnessRuns))
            {
                harnessRuns = [];
                runsByHarness[normalized] = harnessRuns;
            }

            harnessRuns.Add(run);
        }

        List<StoredHarnessRollup> rollups = [];
        foreach (string harness in sessionsByHarness.Keys.Order(StringComparer.Ordinal))
        {
            runsByHarness.TryGetValue(harness, out List<StoredRun>? harnessRuns);
            harnessRuns ??= [];
            List<StoredExecution> harnessExecutions = [];
            foreach (StoredRun run in harnessRuns)
            {
                harnessExecutions.AddRange(projections.ListExecutionsForRun(run.RunId));
            }

            List<SessionDatum> data = new(sessionsByHarness[harness].Count);
            foreach (CollectedSession session in sessionsByHarness[harness])
            {
                data.Add(ReadSessionDatum(session.SessionKey));
            }

            rollups.Add(BuildStoredRollup(harness, data, harnessRuns.Count, harnessExecutions));
        }

        projections.ReplaceHarnessRollups(rollups);

        HashSet<string> live = new(sessionsByHarness.Keys, StringComparer.Ordinal);
        foreach (StoredHarnessRollup existing in projections.ListHarnessRollups())
        {
            if (!live.Contains(existing.HarnessId))
            {
                projections.DeleteHarnessRollup(existing.HarnessId);
            }
        }

        return [.. rollups.Select(ToHarnessRollupRow).OrderBy(static row => row.Harness, StringComparer.Ordinal)];
    }

    private static bool IsPrunedHarnessIdentity(string harness)
    {
        foreach (string pruned in PrunedHarnessIdentities)
        {
            if (string.Equals(pruned, harness, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Typed per-session inputs for harness metric aggregation.</summary>
    /// <param name="Projection">Projected session summary, when the session projector ran.</param>
    /// <param name="Aggregate">Read-time session aggregate, when the session has turns.</param>
    private sealed record SessionDatum(StoredSessionProjection? Projection, SessionAggregateView? Aggregate);

    private SessionDatum ReadSessionDatum(string sessionKey) =>
        new(projections!.GetSessionProjection(sessionKey), reader!.GetSessionAggregate(sessionKey));

    /// <summary>
    /// Aggregates measurable harness metrics from typed session projections and read-time
    /// aggregates. Unmeasurable dimensions are omitted from <c>Metrics</c> rather than zeroed.
    /// </summary>
    private static StoredHarnessRollup BuildStoredRollup(
        string harness,
        IReadOnlyList<SessionDatum> data,
        int runCount,
        IReadOnlyList<StoredExecution> executions)
    {
        List<double> peakPressures = [];
        bool anyDerivedPressure = false;
        int unknownWindowSessions = 0;
        long totalCacheRead = 0;
        long totalInput = 0;
        bool hasCacheRead = false;
        int totalDefinedTools = 0;
        int totalInvokedTools = 0;
        int subagentSessions = 0;

        foreach (SessionDatum datum in data)
        {
            if (datum.Projection is not null && datum.Projection.IsSubagent)
            {
                subagentSessions++;
            }

            if (datum.Projection?.Context is StoredSessionContext context
                && !IsUnnamedWindowSource(context.ContextLimitSource))
            {
                double peak = 0;
                bool found = false;
                foreach (StoredSessionContextTurn turn in context.Turns)
                {
                    if (turn.Pressure is double pressure && double.IsFinite(pressure)
                        && (!found || pressure > peak))
                    {
                        peak = pressure;
                        found = true;
                    }
                }

                if (found)
                {
                    peakPressures.Add(peak);
                    if (string.Equals(context.ContextLimitSource, "declared", StringComparison.Ordinal)
                        || context.DerivedCounts.Count > 0)
                    {
                        anyDerivedPressure = true;
                    }
                }
                else
                {
                    unknownWindowSessions++;
                }
            }
            else
            {
                unknownWindowSessions++;
            }

            if (datum.Aggregate is not SessionAggregateView aggregate)
            {
                continue;
            }

            long input = aggregate.TotalInput ?? 0;
            long cacheRead = aggregate.TotalCacheRead ?? 0;
            totalInput += input;
            totalCacheRead += cacheRead;

            if (cacheRead > 0)
            {
                hasCacheRead = true;
            }

            HashSet<string> offered = new(aggregate.ToolsOffered, StringComparer.Ordinal);
            totalDefinedTools += offered.Count;
            foreach (string tool in aggregate.ToolsInvoked)
            {
                if (offered.Contains(tool))
                {
                    totalInvokedTools++;
                }
            }
        }

        List<StoredHarnessRollupMetric> metrics = [];
        if (peakPressures.Count > 0)
        {
            peakPressures.Sort();
            double medianPressure = Math.Round(ComputeMedian(peakPressures), 4);
            double p95Pressure = Math.Round(ComputeP95(peakPressures), 4);
            string basis = anyDerivedPressure ? "derived" : "measured";
            metrics.Add(new StoredHarnessRollupMetric("context_pressure_median", medianPressure, basis));
            metrics.Add(new StoredHarnessRollupMetric("context_pressure_p95", p95Pressure, basis));
        }

        double? cacheHitRate = null;
        if (hasCacheRead && totalInput > 0)
        {
            cacheHitRate = Math.Round((double)totalCacheRead / totalInput, 4);
            metrics.Add(new StoredHarnessRollupMetric("cache_hit_rate", cacheHitRate.Value, "measured"));
        }

        double? toolYield = null;
        if (totalDefinedTools > 0)
        {
            toolYield = Math.Round((double)totalInvokedTools / totalDefinedTools, 4);
            metrics.Add(new StoredHarnessRollupMetric("tool_yield", toolYield.Value, "measured"));
        }

        double? delegationOverhead = null;
        if (data.Count > 0 && subagentSessions > 0)
        {
            delegationOverhead = Math.Round((double)subagentSessions / data.Count, 4);
            metrics.Add(new StoredHarnessRollupMetric("delegation_overhead", delegationOverhead.Value, "measured"));
        }
        else if (executions.Count > 1 && executions.Any(static execution => !execution.IsRoot))
        {
            int children = executions.Count(static execution => !execution.IsRoot);
            delegationOverhead = Math.Round((double)children / executions.Count, 4);
            metrics.Add(new StoredHarnessRollupMetric("delegation_overhead", delegationOverhead.Value, "measured"));
        }

        int sampleCount = Math.Max(data.Count, Math.Max(runCount, executions.Count));
        return new StoredHarnessRollup(
            harness,
            data.Count,
            runCount,
            executions.Count,
            unknownWindowSessions,
            data.Count,
            metrics);
    }

    /// <summary>Maps a typed harness rollup to the legacy row shape kept for unmigrated callers.</summary>
    private static HarnessRollupRow ToHarnessRollupRow(StoredHarnessRollup rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);

        string pressureReason = $"No measurable turns found in recorded sessions for harness \"{rollup.HarnessId}\".";
        Dictionary<string, JsonElement> measurability = new(StringComparer.Ordinal)
        {
            ["context_pressure"] = MetricTag(rollup, "context_pressure_median", pressureReason),
            ["context_pressure_median"] = MetricTag(rollup, "context_pressure_median", pressureReason),
            ["context_pressure_p95"] = MetricTag(rollup, "context_pressure_p95", pressureReason),
            ["cache_hit_rate"] = MetricTag(rollup, "cache_hit_rate", $"Cache-read telemetry not observed for harness \"{rollup.HarnessId}\"."),
            ["tool_yield"] = MetricTag(rollup, "tool_yield", $"No tool definitions recorded for harness \"{rollup.HarnessId}\"."),
            ["delegation_overhead"] = MetricTag(rollup, "delegation_overhead", $"No subagent executions observed for harness \"{rollup.HarnessId}\"."),
            ["field_coverage"] = JsonSerializer.SerializeToElement("measured"),
        };

        double fieldCoverage = ComputeFieldCoverage(rollup);

        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            sessionCount = rollup.SessionCount,
            runCount = rollup.RunCount,
            executionCount = rollup.ExecutionCount,
            unknownWindowSessions = rollup.UnknownWindowSessions,
            windowSessionsTotal = rollup.WindowSessionsTotal,
        });

        return new HarnessRollupRow(
            Harness: rollup.HarnessId,
            SampleCount: Math.Max(rollup.SessionCount, Math.Max(rollup.RunCount, rollup.ExecutionCount)),
            ContextPressureMedian: MetricValue(rollup, "context_pressure_median"),
            ContextPressureP95: MetricValue(rollup, "context_pressure_p95"),
            CacheHitRate: MetricValue(rollup, "cache_hit_rate"),
            ToolYield: MetricValue(rollup, "tool_yield"),
            DelegationOverhead: MetricValue(rollup, "delegation_overhead"),
            FieldCoverage: fieldCoverage,
            Measurability: measurability,
            Payload: payload);
    }

    /// <summary>
    /// Computes field coverage at read time from the metric dimensions evidenced on a stored
    /// rollup: context pressure (median or p95), cache hit rate, tool yield, and delegation
    /// overhead each contribute one covered dimension.
    /// </summary>
    private static double ComputeFieldCoverage(StoredHarnessRollup rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);

        int covered = 0;
        if (MetricValue(rollup, "context_pressure_median").HasValue
            || MetricValue(rollup, "context_pressure_p95").HasValue)
        {
            covered++;
        }

        if (MetricValue(rollup, "cache_hit_rate").HasValue)
        {
            covered++;
        }

        if (MetricValue(rollup, "tool_yield").HasValue)
        {
            covered++;
        }

        if (MetricValue(rollup, "delegation_overhead").HasValue)
        {
            covered++;
        }

        return Math.Round((double)covered / CoverageDimensions.Count, 4);
    }

    private static double? MetricValue(StoredHarnessRollup rollup, string metric)
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

    private static JsonElement MetricTag(StoredHarnessRollup rollup, string metric, string missingReason)
    {
        foreach (StoredHarnessRollupMetric entry in rollup.Metrics)
        {
            if (string.Equals(entry.Metric, metric, StringComparison.Ordinal))
            {
                return JsonSerializer.SerializeToElement(entry.Basis);
            }
        }

        return NotMeasurableJson(missingReason);
    }

    private IReadOnlyList<HarnessRollupRow> ProjectRunsAndHarnessesLegacy()
    {
        ICanonStoreRepository store = legacyStore!;
        _ = pricing.LoadPricingTables();
        IReadOnlyList<SessionSummaryRow> sessions = store.ListSessions();

        Dictionary<string, RunAccumulator> runsById = new(StringComparer.Ordinal);
        List<ExecutionRow> executions = [];
        Dictionary<string, List<SessionSummaryRow>> sessionsByHarness = new(StringComparer.Ordinal);

        foreach (SessionSummaryRow session in sessions)
        {
            string normalizedHarness = CanonPricing.NormalizeHarnessName(session.Harness);
            if (CanonPricing.IsExcludedHarnessIdentity(normalizedHarness))
            {
                continue;
            }

            if (!sessionsByHarness.TryGetValue(normalizedHarness, out List<SessionSummaryRow>? harnessList))
            {
                harnessList = [];
                sessionsByHarness[normalizedHarness] = harnessList;
            }

            harnessList.Add(session);

            IReadOnlyList<CanonicalRecord> sessionRecords = store.ListRecordsBySessionKey(session.SessionId);
            string? explicitRunId = FindFirstRawAttribute(sessionRecords, RunIdAttributeKeys);
            string? cwd = session.Repo ?? FindFirstRawAttribute(sessionRecords, WorkingDirectoryAttributeKeys);
            string runId = !string.IsNullOrEmpty(explicitRunId)
                ? explicitRunId
                : $"derived:{normalizedHarness}:{StripLeadingHarnessPrefix(normalizedHarness, session.SessionId)}";
            string groupingBasis = !string.IsNullOrEmpty(explicitRunId) ? "explicit" : "derived";
            string groupingRule = !string.IsNullOrEmpty(explicitRunId) ? "explicit_run_id" : "session_fallback";

            if (!runsById.TryGetValue(runId, out RunAccumulator? runAcc))
            {
                runAcc = new RunAccumulator(runId, normalizedHarness, session.Label ?? runId, groupingBasis, groupingRule, cwd);
                runsById[runId] = runAcc;
            }

            runAcc.AddSession(session);

            string? parentExecId = !string.IsNullOrEmpty(session.ParentSession)
                ? $"exec:{session.ParentSession}"
                : null;
            bool isRoot = !session.IsSubagent && parentExecId is null;
            JsonElement linkage = JsonSerializer.SerializeToElement<object>(
                parentExecId is not null
                    ? new { status = "measured", parentSession = session.ParentSession }
                    : new { status = isRoot ? "measured" : "not_measurable", reason = isRoot ? "root_execution" : "no_parent_trace_or_session" });

            executions.Add(new ExecutionRow(
                ExecutionId: $"exec:{session.SessionId}",
                RunId: runId,
                SessionId: session.SessionId,
                ParentExecutionId: parentExecId,
                Harness: normalizedHarness,
                AgentName: session.AgentName,
                IsRoot: isRoot,
                Started: session.Started,
                Ended: session.Ended,
                ParentLinkage: linkage,
                Payload: null));
        }

        List<RunRow> runRows = [];
        foreach (RunAccumulator acc in runsById.Values)
        {
            runRows.Add(acc.ToRunRow());
        }

        store.UpsertRuns(runRows);
        store.UpsertExecutions(executions);

        HashSet<string> allHarnesses = new(CanonicalRegisteredHarnesses, StringComparer.Ordinal);
        foreach (string observed in store.ListObservedHarnesses())
        {
            string norm = CanonPricing.NormalizeHarnessName(observed);
            if (!CanonPricing.IsExcludedHarnessIdentity(norm))
            {
                allHarnesses.Add(norm);
            }
        }

        List<HarnessRollupRow> rollups = [];
        foreach (string harness in allHarnesses.Order(StringComparer.Ordinal))
        {
            sessionsByHarness.TryGetValue(harness, out List<SessionSummaryRow>? harnessSessions);
            int runCount = runRows.Count(r => string.Equals(r.Harness, harness, StringComparison.Ordinal));
            List<ExecutionRow> harnessExecs = [.. executions.Where(e => string.Equals(e.Harness, harness, StringComparison.Ordinal))];

            HarnessRollupRow rollup = BuildRollupRow(
                harness,
                harnessSessions ?? [],
                runCount,
                harnessExecs);
            store.UpsertHarnessRollups([rollup]);
            rollups.Add(rollup);
        }

        foreach (HarnessRollupRow existing in store.ListHarnessRollups())
        {
            if (!allHarnesses.Contains(existing.Harness))
            {
                store.DeleteHarnessRollup(existing.Harness);
            }
        }

        return rollups;
    }

    /// <summary>Derives the six scorecard dimensions for one <see cref="HarnessRollupRow"/>.</summary>
    /// <param name="row">Harness rollup row.</param>
    /// <returns>Serialized scorecard JSON element.</returns>
    public static JsonElement BuildHarnessScorecard(HarnessRollupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        (
            object contextHygiene,
            object cacheEfficiency,
            object toolYield,
            object skillUtilisation,
            object delegationOverhead,
            object continuity
        ) = BuildScorecardDimensions(row);

        return JsonSerializer.SerializeToElement(new
        {
            contextHygiene,
            cacheEfficiency,
            toolYield,
            skillUtilisation,
            delegationOverhead,
            continuity,
        }, CamelCaseJsonOptions);
    }

    /// <summary>Builds a run scorecard over sessions linked to <paramref name="runId"/>.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <returns>Serialized run scorecard, or JSON <c>null</c> when missing.</returns>
    public JsonElement BuildScorecard(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        if (legacyStore is not null)
        {
            return BuildScorecardLegacy(runId);
        }

        StoredRun? storedRun = projections!.GetRun(runId);
        if (storedRun is null)
        {
            return JsonSerializer.SerializeToElement<object?>(null);
        }

        IReadOnlyList<StoredExecution> executions = projections.ListExecutionsForRun(runId);
        List<SessionDatum> data = [];
        foreach (StoredExecution execution in executions)
        {
            if (!string.IsNullOrEmpty(execution.SessionId))
            {
                data.Add(ReadSessionDatum(execution.SessionId));
            }
        }

        HarnessRollupRow metrics = ToHarnessRollupRow(BuildStoredRollup(storedRun.HarnessId, data, 1, executions));
        return BuildScorecardPayload(
            storedRun.RunId,
            storedRun.HarnessId,
            storedRun.Label,
            storedRun.GroupingBasis,
            storedRun.GroupingRule,
            data.Count,
            executions.Count,
            metrics);
    }

    private JsonElement BuildScorecardLegacy(string runId)
    {
        ICanonStoreRepository store = legacyStore!;
        RunRow? run = store.GetRun(runId);
        if (run is null)
        {
            return JsonSerializer.SerializeToElement<object?>(null);
        }

        IReadOnlyList<ExecutionRow> execs = store.ListExecutionsForRun(runId);
        List<SessionSummaryRow> runSessions = [];
        foreach (ExecutionRow exec in execs)
        {
            if (string.IsNullOrEmpty(exec.SessionId))
            {
                continue;
            }

            SessionSummaryRow? session = store.GetSession(exec.SessionId);
            if (session is not null)
            {
                runSessions.Add(session);
            }
        }

        HarnessRollupRow metrics = BuildRollupRow(run.Harness, runSessions, 1, execs);
        return BuildScorecardPayload(
            run.RunId,
            run.Harness,
            run.Label,
            run.GroupingBasis,
            run.GroupingRule,
            runSessions.Count,
            execs.Count,
            metrics);
    }

    private static JsonElement BuildScorecardPayload(
        string runId,
        string harness,
        string? label,
        string? groupingBasis,
        string? groupingRule,
        int sessionCount,
        int executionCount,
        HarnessRollupRow metrics)
    {
        (
            object contextHygiene,
            object cacheEfficiency,
            object toolYield,
            object skillUtilisation,
            object delegationOverhead,
            object continuity
        ) = BuildScorecardDimensions(metrics);

        return JsonSerializer.SerializeToElement(new
        {
            runId,
            harness,
            label,
            groupingBasis,
            groupingRule,
            sessionCount,
            executionCount,
            contextPressureMedian = metrics.ContextPressureMedian,
            contextPressureP95 = metrics.ContextPressureP95,
            cacheHitRate = metrics.CacheHitRate,
            rawToolYield = metrics.ToolYield,
            rawDelegationOverhead = metrics.DelegationOverhead,
            fieldCoverage = metrics.FieldCoverage,
            measurability = metrics.Measurability,
            contextHygiene,
            cacheEfficiency,
            toolYield,
            skillUtilisation,
            delegationOverhead,
            continuity,
        }, CamelCaseJsonOptions);
    }

    /// <summary>Computes structured comparison across all persisted harness rollups.</summary>
    /// <returns>Serialized harness comparison payload.</returns>
    public JsonElement CompareHarnesses()
    {
        if (legacyStore is not null)
        {
            ICanonStoreRepository store = legacyStore;
            IReadOnlyList<HarnessRollupRow> legacyRollups = store.ListHarnessRollups();
            if (legacyRollups.Count == 0)
            {
                legacyRollups = ProjectRunsAndHarnessesLegacy();
            }

            return SerializeComparison(legacyRollups);
        }

        IReadOnlyList<StoredHarnessRollup> stored = projections!.ListHarnessRollups();
        IReadOnlyList<HarnessRollupRow> rollups = stored.Count == 0
            ? ProjectRunsAndHarnessesRelational()
            : [.. stored.Select(ToHarnessRollupRow)];
        return SerializeComparison(rollups);
    }

    private static JsonElement SerializeComparison(IReadOnlyList<HarnessRollupRow> rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        return JsonSerializer.SerializeToElement(new
        {
            harnesses = rollups.Select(r => new
            {
                harness = r.Harness,
                sampleCount = r.SampleCount,
                contextPressureMedian = r.ContextPressureMedian,
                contextPressureP95 = r.ContextPressureP95,
                cacheHitRate = r.CacheHitRate,
                toolYield = r.ToolYield,
                delegationOverhead = r.DelegationOverhead,
                fieldCoverage = r.FieldCoverage,
                measurability = r.Measurability,
            }),
        }, CamelCaseJsonOptions);
    }

    /// <summary>Compares two persisted runs (<paramref name="runAId"/> vs <paramref name="runBId"/>).</summary>
    /// <param name="runAId">Baseline run identifier.</param>
    /// <param name="runBId">Comparison run identifier.</param>
    /// <returns>Comparison JSON element, or <see langword="null"/> when either run is absent.</returns>
    public JsonElement? CompareRuns(string runAId, string runBId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runAId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runBId);

        if (legacyStore is not null)
        {
            return CompareRunsLegacy(runAId, runBId);
        }

        StoredRun? runA = projections!.GetRun(runAId);
        StoredRun? runB = projections.GetRun(runBId);
        if (runA is null || runB is null)
        {
            return null;
        }

        RunMetrics a = SummarizeRunMetrics(runA);
        RunMetrics b = SummarizeRunMetrics(runB);

        return CompareRunMetrics(a, b);
    }

    private static (
        object ContextHygiene,
        object CacheEfficiency,
        object ToolYield,
        object SkillUtilisation,
        object DelegationOverhead,
        object Continuity) BuildScorecardDimensions(HarnessRollupRow row)
    {
        if (row.SampleCount <= 0)
        {
            object unrolled = UnmeasurableDimension(NoRollupReason);
            return (unrolled, unrolled, unrolled, unrolled, unrolled, unrolled);
        }

        return (
            ScorecardDimension(
                row.ContextPressureMedian,
                AsPercent,
                RollupReason(row, "context_pressure_median", "Telemetry missing: context composition not recorded")),
            ScorecardDimension(
                row.CacheHitRate,
                AsPercent,
                RollupReason(row, "cache_hit_rate", "Telemetry missing: harness does not export cache read/creation counters")),
            ScorecardDimension(
                row.ToolYield,
                AsMultiple,
                RollupReason(row, "tool_yield", "Telemetry missing: tool call residency and schema tokens unobserved")),
            UnmeasurableDimension("Telemetry missing: skill activation unobserved on this harness"),
            ScorecardDimension(
                row.DelegationOverhead,
                AsPercent,
                RollupReason(row, "delegation_overhead", "Telemetry missing: no delegation events recorded or single-agent run")),
            UnmeasurableDimension("Telemetry missing: turn outcome or correction telemetry unobserved"));
    }

    private static object ScorecardDimension(double? value, Func<double, string> format, string fallbackReason)
    {
        if (value is double numeric && double.IsFinite(numeric))
        {
            return new { value = numeric, display = format(numeric) };
        }

        return UnmeasurableDimension(fallbackReason);
    }

    private static object UnmeasurableDimension(string reason) => new { value = (double?)null, reason };

    private static string AsPercent(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value * 100.0, MidpointRounding.AwayFromZero):0}%");

    private static string AsMultiple(double value) =>
        value.ToString("F1", CultureInfo.InvariantCulture) + "x";

    private static string RollupReason(HarnessRollupRow row, string metricKey, string fallbackReason)
    {
        if (row.Measurability is not null && row.Measurability.TryGetValue(metricKey, out JsonElement entry)
            && entry.ValueKind == JsonValueKind.Object
            && ((entry.TryGetProperty("availability", out JsonElement availEl)
                    && availEl.ValueKind == JsonValueKind.String
                    && string.Equals(availEl.GetString(), "not_measurable", StringComparison.Ordinal))
                || (entry.TryGetProperty("status", out JsonElement statusEl)
                    && statusEl.ValueKind == JsonValueKind.String
                    && string.Equals(statusEl.GetString(), "not_measurable", StringComparison.Ordinal)))
            && entry.TryGetProperty("reason", out JsonElement reasonEl)
            && reasonEl.ValueKind == JsonValueKind.String)
        {
            string? reason = reasonEl.GetString();
            if (!string.IsNullOrEmpty(reason))
            {
                return reason;
            }
        }

        return fallbackReason;
    }

    private JsonElement? CompareRunsLegacy(string runAId, string runBId)
    {
        ICanonStoreRepository store = legacyStore!;
        RunRow? runA = store.GetRun(runAId);
        RunRow? runB = store.GetRun(runBId);
        if (runA is null || runB is null)
        {
            return null;
        }

        RunMetrics a = SummarizeRunMetrics(runA);
        RunMetrics b = SummarizeRunMetrics(runB);
        return CompareRunMetrics(a, b);
    }

    private static JsonElement CompareRunMetrics(RunMetrics a, RunMetrics b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        long? deltaInput = a.TotalInput.HasValue && b.TotalInput.HasValue ? b.TotalInput.Value - a.TotalInput.Value : null;
        long? deltaOutput = a.TotalOutput.HasValue && b.TotalOutput.HasValue ? b.TotalOutput.Value - a.TotalOutput.Value : null;
        double? deltaPressure = a.PeakPressure.HasValue && b.PeakPressure.HasValue
            ? Math.Round(b.PeakPressure.Value - a.PeakPressure.Value, 4)
            : null;

        return JsonSerializer.SerializeToElement(new
        {
            runA = a,
            runB = b,
            delta = new
            {
                inputTokens = deltaInput,
                outputTokens = deltaOutput,
                peakPressure = deltaPressure,
                turns = b.TurnCount - a.TurnCount,
            },
        }, CamelCaseJsonOptions);
    }

    private record RunMetrics(
        string RunId,
        string Harness,
        string? Started,
        string? Ended,
        int SessionCount,
        int TurnCount,
        long? TotalInput,
        long? TotalOutput,
        double? PeakPressure);

    private RunMetrics SummarizeRunMetrics(RunRow run)
    {
        ICanonStoreRepository store = legacyStore!;
        IReadOnlyList<ExecutionRow> execs = store.ListExecutionsForRun(run.RunId);
        long totalInput = 0;
        long totalOutput = 0;
        bool hasInput = false;
        bool hasOutput = false;
        int turns = 0;
        double maxPressure = 0;
        bool hasPressure = false;

        foreach (ExecutionRow exec in execs)
        {
            if (string.IsNullOrEmpty(exec.SessionId))
            {
                continue;
            }

            SessionSummaryRow? session = store.GetSession(exec.SessionId);
            if (session is null)
            {
                continue;
            }

            if (TryGetSummaryElement(session, out JsonElement summary))
            {
                if (TryGetLong(summary, "total_input", out long inp))
                {
                    totalInput += inp;
                    hasInput = true;
                }

                if (TryGetLong(summary, "total_output", out long outp))
                {
                    totalOutput += outp;
                    hasOutput = true;
                }

                if (TryGetInt(summary, "turn_count", out int tc))
                {
                    turns += tc;
                }
            }

            if (TryExtractPeakPressure(session, out double peak, out _))
            {
                if (!hasPressure || peak > maxPressure)
                {
                    maxPressure = peak;
                }

                hasPressure = true;
            }
        }

        return new RunMetrics(
            run.RunId,
            run.Harness,
            run.Started,
            run.Ended,
            execs.Count,
            turns,
            hasInput ? totalInput : null,
            hasOutput ? totalOutput : null,
            hasPressure ? Math.Round(maxPressure, 4) : null);
    }

    private RunMetrics SummarizeRunMetrics(StoredRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        IReadOnlyList<StoredExecution> execs = projections!.ListExecutionsForRun(run.RunId);
        long totalInput = 0;
        long totalOutput = 0;
        bool hasInput = false;
        bool hasOutput = false;
        int turns = 0;
        double maxPressure = 0;
        bool hasPressure = false;

        foreach (StoredExecution execution in execs)
        {
            if (string.IsNullOrEmpty(execution.SessionId))
            {
                continue;
            }

            SessionDatum datum = ReadSessionDatum(execution.SessionId);
            if (datum.Aggregate is SessionAggregateView aggregate)
            {
                if (aggregate.TotalInput is long input)
                {
                    totalInput += input;
                    hasInput = true;
                }

                if (aggregate.TotalOutput is long output)
                {
                    totalOutput += output;
                    hasOutput = true;
                }

                turns += aggregate.TurnCount;
            }

            if (datum.Projection?.Context is StoredSessionContext context)
            {
                foreach (StoredSessionContextTurn turn in context.Turns)
                {
                    if (turn.Pressure is double pressure && double.IsFinite(pressure)
                        && (!hasPressure || pressure > maxPressure))
                    {
                        maxPressure = pressure;
                        hasPressure = true;
                    }
                }
            }
        }

        return new RunMetrics(
            run.RunId,
            run.HarnessId,
            run.StartedAtUtc,
            run.EndedAtUtc,
            execs.Count,
            turns,
            hasInput ? totalInput : null,
            hasOutput ? totalOutput : null,
            hasPressure ? Math.Round(maxPressure, 4) : null);
    }

    private static HarnessRollupRow BuildRollupRow(
        string harness,
        IReadOnlyList<SessionSummaryRow> sessions,
        int runCount,
        IReadOnlyList<ExecutionRow> executions)
    {
        Dictionary<string, JsonElement> measurability = new(StringComparer.Ordinal);
        if (sessions.Count == 0 && executions.Count == 0 && runCount == 0)
        {
            string reason = $"No collectable runs or sessions recorded for harness \"{harness}\".";
            foreach (string dim in new[] { "context_pressure", "context_pressure_median", "context_pressure_p95", "cache_hit_rate", "tool_yield", "delegation_overhead" })
            {
                measurability[dim] = NotMeasurableJson(reason);
            }

            measurability["field_coverage"] = JsonSerializer.SerializeToElement("measured");

            JsonElement emptyPayload = JsonSerializer.SerializeToElement(new
            {
                sessionCount = 0,
                runCount = 0,
                executionCount = 0,
                reason,
            });

            return new HarnessRollupRow(
                Harness: harness,
                SampleCount: 0,
                ContextPressureMedian: null,
                ContextPressureP95: null,
                CacheHitRate: null,
                ToolYield: null,
                DelegationOverhead: null,
                FieldCoverage: 0.0,
                Measurability: measurability,
                Payload: emptyPayload);
        }

        List<double> peakPressures = [];
        bool anyDerivedPressure = false;
        int unknownWindowSessions = 0;
        long totalCacheRead = 0;
        long totalInput = 0;
        bool hasCacheRead = false;
        bool hasInput = false;
        int totalDefinedTools = 0;
        int totalInvokedTools = 0;
        int subagentSessions = 0;

        foreach (SessionSummaryRow session in sessions)
        {
            if (session.IsSubagent)
            {
                subagentSessions++;
            }

            if (IsUnnamedWindowSource(session.ContextLimitSource))
            {
                unknownWindowSessions++;
            }

            if (TryExtractPeakPressure(session, out double peak, out bool derived))
            {
                peakPressures.Add(peak);
                if (derived)
                {
                    anyDerivedPressure = true;
                }
            }

            if (TryGetSummaryElement(session, out JsonElement summary))
            {
                if (TryGetLong(summary, "total_input", out long inp))
                {
                    totalInput += inp;
                    if (inp > 0)
                    {
                        hasInput = true;
                    }
                }

                if (TryGetLong(summary, "total_cache_read", out long cr))
                {
                    totalCacheRead += cr;
                    if (cr > 0)
                    {
                        hasCacheRead = true;
                    }
                }

                if (summary.TryGetProperty("tools_offered", out JsonElement offered) && offered.ValueKind == JsonValueKind.Array
                    && summary.TryGetProperty("tools_invoked", out JsonElement invoked) && invoked.ValueKind == JsonValueKind.Array)
                {
                    HashSet<string> offeredSet = new(StringComparer.Ordinal);
                    foreach (JsonElement item in offered.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is string name)
                        {
                            offeredSet.Add(name);
                        }
                    }

                    totalDefinedTools += offeredSet.Count;
                    foreach (JsonElement item in invoked.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is string name && offeredSet.Contains(name))
                        {
                            totalInvokedTools++;
                        }
                    }
                }
            }
        }

        double? medianPressure = null;
        double? p95Pressure = null;
        if (peakPressures.Count > 0)
        {
            peakPressures.Sort();
            medianPressure = Math.Round(ComputeMedian(peakPressures), 4);
            p95Pressure = Math.Round(ComputeP95(peakPressures), 4);
            JsonElement tag = JsonSerializer.SerializeToElement(anyDerivedPressure ? "derived" : "measured");
            measurability["context_pressure"] = tag;
            measurability["context_pressure_median"] = tag;
            measurability["context_pressure_p95"] = tag;
        }
        else
        {
            string reason = unknownWindowSessions > 0
                ? string.Create(CultureInfo.InvariantCulture, $"No source reported a context window for {unknownWindowSessions} recorded session(s) for harness \"{harness}\"; pressure is unmeasurable, not zero.")
                : $"No measurable turns found in recorded sessions for harness \"{harness}\".";
            JsonElement notMeas = NotMeasurableJson(reason);
            measurability["context_pressure"] = notMeas;
            measurability["context_pressure_median"] = notMeas;
            measurability["context_pressure_p95"] = notMeas;
        }

        double? cacheHitRate = null;
        if (hasCacheRead && totalInput > 0)
        {
            cacheHitRate = Math.Round((double)totalCacheRead / totalInput, 4);
            measurability["cache_hit_rate"] = JsonSerializer.SerializeToElement("measured");
        }
        else
        {
            measurability["cache_hit_rate"] = NotMeasurableJson(
                $"Cache-read telemetry not observed for harness \"{harness}\".");
        }

        double? toolYield = null;
        if (totalDefinedTools > 0)
        {
            toolYield = Math.Round((double)totalInvokedTools / totalDefinedTools, 4);
            measurability["tool_yield"] = JsonSerializer.SerializeToElement("measured");
        }
        else
        {
            measurability["tool_yield"] = NotMeasurableJson(
                $"No tool definitions recorded for harness \"{harness}\".");
        }

        double? delegationOverhead = null;
        if (sessions.Count > 0 && subagentSessions > 0)
        {
            delegationOverhead = Math.Round((double)subagentSessions / sessions.Count, 4);
            measurability["delegation_overhead"] = JsonSerializer.SerializeToElement("measured");
        }
        else if (executions.Count > 1 && executions.Any(e => !e.IsRoot))
        {
            int children = executions.Count(e => !e.IsRoot);
            delegationOverhead = Math.Round((double)children / executions.Count, 4);
            measurability["delegation_overhead"] = JsonSerializer.SerializeToElement("measured");
        }
        else
        {
            measurability["delegation_overhead"] = NotMeasurableJson(
                $"No subagent executions observed for harness \"{harness}\".");
        }

        int coveredDimensions = 0;
        if (hasInput)
        {
            coveredDimensions++;
        }

        if (cacheHitRate.HasValue)
        {
            coveredDimensions++;
        }

        if (toolYield.HasValue)
        {
            coveredDimensions++;
        }

        if (delegationOverhead.HasValue)
        {
            coveredDimensions++;
        }

        if (medianPressure.HasValue)
        {
            coveredDimensions++;
        }

        double fieldCoverage = Math.Round((double)coveredDimensions / CoverageDimensions.Count, 4);
        measurability["field_coverage"] = JsonSerializer.SerializeToElement("measured");

        int sampleCount = Math.Max(sessions.Count, Math.Max(runCount, executions.Count));
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            sessionCount = sessions.Count,
            runCount,
            executionCount = executions.Count,
            unknownWindowSessions,
            windowSessionsTotal = sessions.Count,
        });

        return new HarnessRollupRow(
            Harness: harness,
            SampleCount: sampleCount,
            ContextPressureMedian: medianPressure,
            ContextPressureP95: p95Pressure,
            CacheHitRate: cacheHitRate,
            ToolYield: toolYield,
            DelegationOverhead: delegationOverhead,
            FieldCoverage: fieldCoverage,
            Measurability: measurability,
            Payload: payload);
    }

    private static JsonElement NotMeasurableJson(string reason) =>
        JsonSerializer.SerializeToElement(new { status = "not_measurable", reason });

    private static bool IsUnnamedWindowSource(string? source) =>
        string.IsNullOrEmpty(source) || string.Equals(source, "default", StringComparison.Ordinal);

    private static bool TryExtractPeakPressure(SessionSummaryRow session, out double peak, out bool derived)
    {
        peak = 0;
        derived = false;
        if (IsUnnamedWindowSource(session.ContextLimitSource))
        {
            return false;
        }

        if (session.Payload.ValueKind != JsonValueKind.Object
            || !session.Payload.TryGetProperty("context", out JsonElement context)
            || context.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (string.Equals(session.ContextLimitSource, "declared", StringComparison.Ordinal))
        {
            derived = true;
        }

        if (context.TryGetProperty("derivedCounts", out JsonElement dc) && dc.ValueKind == JsonValueKind.True)
        {
            derived = true;
        }

        if (!context.TryGetProperty("turns", out JsonElement turns) || turns.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        bool found = false;
        foreach (JsonElement turn in turns.EnumerateArray())
        {
            if (turn.ValueKind == JsonValueKind.Object
                && turn.TryGetProperty("pressure", out JsonElement pEl)
                && pEl.ValueKind == JsonValueKind.Number
                && pEl.TryGetDouble(out double val)
                && double.IsFinite(val))
            {
                if (!found || val > peak)
                {
                    peak = val;
                }

                found = true;
            }
        }

        return found;
    }

    private static bool TryGetSummaryElement(SessionSummaryRow session, out JsonElement summary)
    {
        summary = default;
        if (!string.IsNullOrEmpty(session.SummaryJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(session.SummaryJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    summary = doc.RootElement.Clone();
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        if (session.Payload.ValueKind == JsonValueKind.Object
            && session.Payload.TryGetProperty("summary", out JsonElement embedded)
            && embedded.ValueKind == JsonValueKind.Object)
        {
            summary = embedded;
            return true;
        }

        return false;
    }

    private static bool TryGetLong(JsonElement obj, string propName, out long value)
    {
        value = 0;
        return obj.TryGetProperty(propName, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt64(out value);
    }

    private static bool TryGetInt(JsonElement obj, string propName, out int value)
    {
        value = 0;
        return obj.TryGetProperty(propName, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }

    private static string? FindFirstRawAttribute(IReadOnlyList<CanonicalRecord> records, string[] keys)
    {
        foreach (CanonicalRecord record in records)
        {
            if (record.Raw is null)
            {
                continue;
            }

            foreach (string key in keys)
            {
                if (!record.Raw.TryGetValue(key, out JsonElement el))
                {
                    continue;
                }

                if (el.ValueKind == JsonValueKind.String)
                {
                    string? text = el.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
                else if (el.ValueKind == JsonValueKind.Number)
                {
                    return el.GetRawText();
                }
            }
        }

        return null;
    }

    private static string StripLeadingHarnessPrefix(string harness, string seed)
    {
        string prefix = $"{harness}:";
        return seed.StartsWith(prefix, StringComparison.Ordinal)
            ? seed[prefix.Length..]
            : seed;
    }

    private static string SanitizePathSegment(string value)
    {
        StringBuilder sb = new(value.Length);
        foreach (char ch in value)
        {
            bool safe = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
            sb.Append(safe ? ch : '_');
        }

        return sb.ToString();
    }

    private class RunAccumulator(
        string runId,
        string harness,
        string label,
        string groupingBasis,
        string groupingRule,
        string? workingDirectory)
    {
        private string? started;
        private string? ended;
        private int count;

        public void AddSession(SessionSummaryRow session)
        {
            count++;
            if (!string.IsNullOrEmpty(session.Started)
                && (started is null || string.Compare(session.Started, started, StringComparison.Ordinal) < 0))
            {
                started = session.Started;
            }

            if (!string.IsNullOrEmpty(session.Ended)
                && (ended is null || string.Compare(session.Ended, ended, StringComparison.Ordinal) > 0))
            {
                ended = session.Ended;
            }
        }

        public RunRow ToRunRow() => new(
            RunId: runId,
            Harness: harness,
            Label: label,
            GroupingBasis: groupingBasis,
            GroupingRule: groupingRule,
            WorkingDirectory: workingDirectory,
            Started: started,
            Ended: ended,
            ExecutionCount: count,
            Payload: null);
    }
}
