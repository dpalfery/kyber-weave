using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KyberWeave.Core.Dash;

/// <summary>Derived or explicit run grouping result produced under Decision D13.</summary>
public sealed record DerivedRunIdentity(
    string RunId,
    string GroupingBasis,
    string GroupingRule,
    string? WorkingDirectory,
    OutcomeBlock Outcome);

/// <summary>
/// Derives run and execution boundaries, projects per-harness efficiency rollups with honest
/// unobservability (<c>not_measurable</c> instead of zero), and computes run scorecards and
/// comparisons (<c>dash/src/canon/runs.ts</c>, <c>dash/src/canon/harnesses.ts</c>).
/// </summary>
public static class CanonRollups
{
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
        "aider",
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
        "windsurf",
    ];

    /// <summary>
    /// Derives run identity from a batch of canonical records following Decision D13: explicit
    /// <c>run.id</c> yields <c>explicit</c>; otherwise derives from working directory or session.
    /// </summary>
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
                        minMs = ms;
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
    public static double ComputeMedian(IReadOnlyList<double> sorted)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        int count = sorted.Count;
        if (count == 0)
            return 0;

        int mid = count / 2;
        return count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Computes the nearest-rank 95th percentile of a sorted ascending sequence (<c>0</c> if empty).</summary>
    public static double ComputeP95(IReadOnlyList<double> sorted)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        int count = sorted.Count;
        if (count == 0)
            return 0;

        int index = Math.Clamp((int)Math.Ceiling(0.95 * count) - 1, 0, count - 1);
        return sorted[index];
    }

    /// <summary>
    /// Rebuilds <c>run</c>, <c>execution</c>, and <c>harness_rollup</c> rows from all sessions
    /// currently materialized in <paramref name="store"/>.
    /// </summary>
    public static IReadOnlyList<HarnessRollupRow> ProjectRunsAndHarnesses(CanonStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<SessionSummaryRow> sessions = store.ListSessions();

        Dictionary<string, RunAccumulator> runsById = new(StringComparer.Ordinal);
        List<ExecutionRow> executions = [];
        Dictionary<string, List<SessionSummaryRow>> sessionsByHarness = new(StringComparer.Ordinal);

        foreach (SessionSummaryRow session in sessions)
        {
            string normalizedHarness = CanonPricing.NormalizeHarnessName(session.Harness);
            if (CanonPricing.IsExcludedHarnessIdentity(normalizedHarness))
                continue;

            if (!sessionsByHarness.TryGetValue(normalizedHarness, out List<SessionSummaryRow>? harnessList))
            {
                harnessList = [];
                sessionsByHarness[normalizedHarness] = harnessList;
            }

            harnessList.Add(session);

            IReadOnlyList<CanonicalRecord> sessionRecords = store.GetRecordsForSessionKey(session.SessionId);
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
            runRows.Add(acc.ToRunRow());

        store.UpsertRuns(runRows);
        store.UpsertExecutions(executions);

        HashSet<string> allHarnesses = new(CanonicalRegisteredHarnesses, StringComparer.Ordinal);
        foreach (string observed in store.ListObservedHarnesses())
        {
            string norm = CanonPricing.NormalizeHarnessName(observed);
            if (!CanonPricing.IsExcludedHarnessIdentity(norm))
                allHarnesses.Add(norm);
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
            store.UpsertHarnessRollup(rollup);
            rollups.Add(rollup);
        }

        foreach (HarnessRollupRow existing in store.ListHarnessRollups())
        {
            if (!allHarnesses.Contains(existing.Harness))
                store.DeleteHarnessRollup(existing.Harness);
        }

        return rollups;
    }

    /// <summary>Builds a run scorecard over sessions linked to <paramref name="runId"/>.</summary>
    public static JsonElement BuildScorecard(CanonStore store, string runId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        RunRow? run = store.GetRun(runId);
        if (run is null)
            return JsonSerializer.SerializeToElement<object?>(null);

        IReadOnlyList<ExecutionRow> execs = store.ListExecutionsForRun(runId);
        List<SessionSummaryRow> runSessions = [];
        foreach (ExecutionRow exec in execs)
        {
            if (string.IsNullOrEmpty(exec.SessionId))
                continue;

            SessionSummaryRow? session = store.GetSession(exec.SessionId);
            if (session is not null)
                runSessions.Add(session);
        }

        HarnessRollupRow metrics = BuildRollupRow(run.Harness, runSessions, 1, execs);
        return JsonSerializer.SerializeToElement(new
        {
            runId = run.RunId,
            harness = run.Harness,
            label = run.Label,
            groupingBasis = run.GroupingBasis,
            groupingRule = run.GroupingRule,
            sessionCount = runSessions.Count,
            executionCount = execs.Count,
            contextPressureMedian = metrics.ContextPressureMedian,
            contextPressureP95 = metrics.ContextPressureP95,
            cacheHitRate = metrics.CacheHitRate,
            toolYield = metrics.ToolYield,
            delegationOverhead = metrics.DelegationOverhead,
            fieldCoverage = metrics.FieldCoverage,
            measurability = metrics.Measurability,
        });
    }

    /// <summary>Computes structured comparison across all persisted harness rollups.</summary>
    public static JsonElement CompareHarnesses(CanonStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<HarnessRollupRow> rollups = store.ListHarnessRollups();
        if (rollups.Count == 0)
            rollups = ProjectRunsAndHarnesses(store);

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
        });
    }

    /// <summary>Compares two persisted runs (<paramref name="runAId"/> vs <paramref name="runBId"/>).</summary>
    public static JsonElement? CompareRuns(CanonStore store, string runAId, string runBId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(runAId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runBId);

        RunRow? runA = store.GetRun(runAId);
        RunRow? runB = store.GetRun(runBId);
        if (runA is null || runB is null)
            return null;

        RunMetrics a = SummarizeRunMetrics(store, runA);
        RunMetrics b = SummarizeRunMetrics(store, runB);

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
        });
    }

    private sealed record RunMetrics(
        string RunId,
        string Harness,
        string? Started,
        string? Ended,
        int SessionCount,
        int TurnCount,
        long? TotalInput,
        long? TotalOutput,
        double? PeakPressure);

    private static RunMetrics SummarizeRunMetrics(CanonStore store, RunRow run)
    {
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
                continue;

            SessionSummaryRow? session = store.GetSession(exec.SessionId);
            if (session is null)
                continue;

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
                    turns += tc;
            }

            if (TryExtractPeakPressure(session, out double peak, out _))
            {
                if (!hasPressure || peak > maxPressure)
                    maxPressure = peak;
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
                measurability[dim] = NotMeasurableJson(reason);
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
                subagentSessions++;

            if (IsUnnamedWindowSource(session.ContextLimitSource))
                unknownWindowSessions++;

            if (TryExtractPeakPressure(session, out double peak, out bool derived))
            {
                peakPressures.Add(peak);
                if (derived)
                    anyDerivedPressure = true;
            }

            if (TryGetSummaryElement(session, out JsonElement summary))
            {
                if (TryGetLong(summary, "total_input", out long inp))
                {
                    totalInput += inp;
                    if (inp > 0)
                        hasInput = true;
                }

                if (TryGetLong(summary, "total_cache_read", out long cr))
                {
                    totalCacheRead += cr;
                    if (cr > 0)
                        hasCacheRead = true;
                }

                if (summary.TryGetProperty("tools_offered", out JsonElement offered) && offered.ValueKind == JsonValueKind.Array
                    && summary.TryGetProperty("tools_invoked", out JsonElement invoked) && invoked.ValueKind == JsonValueKind.Array)
                {
                    HashSet<string> offeredSet = new(StringComparer.Ordinal);
                    foreach (JsonElement item in offered.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is string name)
                            offeredSet.Add(name);
                    }

                    totalDefinedTools += offeredSet.Count;
                    foreach (JsonElement item in invoked.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is string name && offeredSet.Contains(name))
                            totalInvokedTools++;
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
            coveredDimensions++;
        if (cacheHitRate.HasValue)
            coveredDimensions++;
        if (toolYield.HasValue)
            coveredDimensions++;
        if (delegationOverhead.HasValue)
            coveredDimensions++;
        if (medianPressure.HasValue)
            coveredDimensions++;

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

    private static JsonElement NotMeasurableJson(string reason)
    {
        return JsonSerializer.SerializeToElement(new { status = "not_measurable", reason });
    }

    private static bool IsUnnamedWindowSource(string? source)
    {
        return string.IsNullOrEmpty(source) || string.Equals(source, "default", StringComparison.Ordinal);
    }

    private static bool TryExtractPeakPressure(SessionSummaryRow session, out double peak, out bool derived)
    {
        peak = 0;
        derived = false;
        if (IsUnnamedWindowSource(session.ContextLimitSource))
            return false;

        if (session.Payload.ValueKind != JsonValueKind.Object
            || !session.Payload.TryGetProperty("context", out JsonElement context)
            || context.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (string.Equals(session.ContextLimitSource, "declared", StringComparison.Ordinal))
            derived = true;
        if (context.TryGetProperty("derivedCounts", out JsonElement dc) && dc.ValueKind == JsonValueKind.True)
            derived = true;

        if (!context.TryGetProperty("turns", out JsonElement turns) || turns.ValueKind != JsonValueKind.Array)
            return false;

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
                    peak = val;
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
                continue;

            foreach (string key in keys)
            {
                if (!record.Raw.TryGetValue(key, out JsonElement el))
                    continue;

                if (el.ValueKind == JsonValueKind.String)
                {
                    string? text = el.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(text))
                        return text;
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

    private sealed class RunAccumulator(
        string runId,
        string harness,
        string label,
        string groupingBasis,
        string groupingRule,
        string? workingDirectory)
    {
        private string? _started;
        private string? _ended;
        private int _count;

        public void AddSession(SessionSummaryRow session)
        {
            _count++;
            if (!string.IsNullOrEmpty(session.Started)
                && (_started is null || string.Compare(session.Started, _started, StringComparison.Ordinal) < 0))
            {
                _started = session.Started;
            }

            if (!string.IsNullOrEmpty(session.Ended)
                && (_ended is null || string.Compare(session.Ended, _ended, StringComparison.Ordinal) > 0))
            {
                _ended = session.Ended;
            }
        }

        public RunRow ToRunRow() => new(
            RunId: runId,
            Harness: harness,
            Label: label,
            GroupingBasis: groupingBasis,
            GroupingRule: groupingRule,
            WorkingDirectory: workingDirectory,
            Started: _started,
            Ended: _ended,
            ExecutionCount: _count,
            Payload: null);
    }
}
