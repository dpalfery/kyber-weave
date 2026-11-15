using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KyberWeave.Core.Dash;

/// <summary>
/// Bounded read/analytics query bridge over <see cref="CanonStore"/> serving the REST and
/// analytics contracts defined by <c>dash/src/server/bridge.ts</c> and
/// <c>dash/src/analysis/query/service.ts</c>.
/// </summary>
public sealed class KyberBridge
{
    /// <summary>Default byte budget applied to turn and session content responses (32 KiB).</summary>
    public const int ContentResponseBudget = 32_768;

    /// <summary>REST contract schema version expected by desktop tray and web clients.</summary>
    public const int ApiContractVersion = 1;

    /// <summary>Default KyberDash engine version reported by <c>/api/kyber/version</c> and <c>/api/kyber/meta</c>.</summary>
    public const string DefaultVersion = "0.21.0";

    private static readonly string[] AnalyticsOperations =
    [
        "status",
        "catalog",
        "query",
        "search-sessions",
        "inspect",
        "findings",
        "compare",
        "outcomes",
    ];

    private readonly CanonStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _version;
    private readonly string _instanceId;

    /// <summary>Initializes a new <see cref="KyberBridge"/> over <paramref name="store"/>.</summary>
    public KyberBridge(
        CanonStore store,
        Func<DateTimeOffset>? clock = null,
        string version = DefaultVersion,
        string? instanceId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _version = version;
        _instanceId = string.IsNullOrWhiteSpace(instanceId)
            ? string.Create(CultureInfo.InvariantCulture, $"kyberdash-{Environment.ProcessId}")
            : instanceId;
    }

    /// <summary>Ensures dirty sessions, runs, executions, and harness rollups are projected up to date.</summary>
    public void EnsureProjected()
    {
        ProjectionPassReport report = CanonSessionProjector.ProjectIncremental(_store);
        if (report.Built > 0 || report.Pruned > 0
            || (_store.ListSessions().Count > 0 && _store.ListHarnessRollups().Count == 0))
        {
            CanonRollups.ProjectRunsAndHarnesses(_store);
        }
    }

    /// <summary>Returns hierarchical root-first sessions with nested subagents and optional <paramref name="limit"/>.</summary>
    public JsonElement GetSessions(int? limit = null, string? harness = null)
    {
        EnsureProjected();
        IReadOnlyList<SessionSummaryRow> rows = _store.ListSessions(harness);
        Dictionary<string, SessionNode> byId = new(StringComparer.Ordinal);
        List<SessionNode> orderedNodes = [];

        foreach (SessionSummaryRow row in rows)
        {
            JsonElement? summary = ParseSummary(row);
            long? totalInput = TryGetNullableLong(summary, "total_input");
            long? totalOutput = TryGetNullableLong(summary, "total_output");
            int? turnCount = TryGetNullableInt(summary, "turn_count");
            int? requestCount = TryGetNullableInt(summary, "request_count");
            IReadOnlyList<string> models = ExtractStringArray(summary, "models");
            JsonElement cost = ExtractCost(summary);
            double? costUsd = ExtractUsdCost(cost);

            string label = !string.IsNullOrWhiteSpace(row.Label)
                ? row.Label!
                : CanonPricing.FormatSessionTitle(new SessionTitleInput(row.SessionId, row.AgentName, row.Harness, row.Label));

            SessionNode node = new(
                SessionId: row.SessionId,
                Harness: row.Harness,
                Label: label,
                IsSubagent: row.IsSubagent,
                ParentSession: row.ParentSession,
                AgentName: row.AgentName,
                Repo: row.Repo,
                Branch: row.Branch,
                Started: row.Started,
                Ended: row.Ended,
                TurnCount: turnCount,
                RequestCount: requestCount,
                TotalInput: totalInput,
                TotalOutput: totalOutput,
                CostUsd: costUsd,
                Cost: cost,
                Models: models,
                Problems: row.ProblemsCount ?? 0,
                Subagents: []);

            byId[row.SessionId] = node;
            orderedNodes.Add(node);
        }

        HashSet<string> attachedChildren = new(StringComparer.Ordinal);
        foreach (SessionNode node in orderedNodes)
        {
            if ((!node.IsSubagent && string.IsNullOrEmpty(node.ParentSession))
                || string.IsNullOrEmpty(node.ParentSession)
                || !byId.TryGetValue(node.ParentSession, out SessionNode? parent)
                || IsAncestorOrSelf(node.SessionId, parent.SessionId, byId))
            {
                continue;
            }

            parent.Subagents.Add(node);
            attachedChildren.Add(node.SessionId);
        }

        List<SessionNode> roots = [.. orderedNodes.Where(n => !attachedChildren.Contains(n.SessionId))];
        foreach (SessionNode node in orderedNodes)
            node.Subagents.Sort((a, b) => string.Compare(a.Started, b.Started, StringComparison.Ordinal));
        roots.Sort((a, b) => string.Compare(b.Started, a.Started, StringComparison.Ordinal));

        if (limit.HasValue && limit.Value > 0 && roots.Count > limit.Value)
            roots = roots.GetRange(0, limit.Value);

        return JsonSerializer.SerializeToElement(roots.Select(ToSessionDto).ToList());
    }

    /// <summary>Returns the hydrated full session payload (with materialized turns), or <see langword="null"/> if missing.</summary>
    public JsonElement? GetSessionDetail(string sessionId)
    {
        EnsureProjected();
        SessionSummaryRow? session = _store.GetSession(sessionId);
        if (session is null)
            return null;

        IReadOnlyList<JsonElement> turns = _store.ListSessionTurnPayloads(sessionId);
        return JsonSerializer.SerializeToElement(new
        {
            session_id = session.SessionId,
            harness = session.Harness,
            label = session.Label,
            is_subagent = session.IsSubagent,
            parent_session = session.ParentSession,
            agent_name = session.AgentName,
            repo = session.Repo,
            branch = session.Branch,
            started = session.Started,
            ended = session.Ended,
            problems = session.ProblemsCount ?? 0,
            context_limit_source = session.ContextLimitSource,
            summary = ParseSummary(session),
            payload = session.Payload,
            turns,
        });
    }

    /// <summary>Returns the bounded session summary (without full turn bodies), or <see langword="null"/> if missing.</summary>
    public JsonElement? GetSessionSummary(string sessionId)
    {
        EnsureProjected();
        SessionSummaryRow? session = _store.GetSession(sessionId);
        if (session is null)
            return null;

        return JsonSerializer.SerializeToElement(new
        {
            session_id = session.SessionId,
            harness = session.Harness,
            label = session.Label,
            is_subagent = session.IsSubagent,
            parent_session = session.ParentSession,
            agent_name = session.AgentName,
            repo = session.Repo,
            branch = session.Branch,
            started = session.Started,
            ended = session.Ended,
            problems = session.ProblemsCount ?? 0,
            context_limit_source = session.ContextLimitSource,
            summary = ParseSummary(session),
        });
    }

    /// <summary>Returns turn-level figures for <paramref name="turnIndex"/> in <paramref name="sessionId"/>, or <see langword="null"/>.</summary>
    public JsonElement? GetTurnFigures(string sessionId, int turnIndex)
    {
        EnsureProjected();
        JsonElement? turn = _store.GetSessionTurnPayload(sessionId, turnIndex);
        if (!turn.HasValue)
            return null;

        return JsonSerializer.SerializeToElement(new
        {
            sessionId,
            turnIndex,
            turn = turn.Value,
        });
    }

    /// <summary>Returns bounded or full text/parts content for a single turn.</summary>
    public JsonElement? GetTurnContent(string sessionId, int turnIndex, int budget = ContentResponseBudget, bool full = false)
    {
        EnsureProjected();
        JsonElement? turn = _store.GetSessionTurnPayload(sessionId, turnIndex);
        if (!turn.HasValue)
            return null;

        string serialized = turn.Value.GetRawText();
        int totalBytes = Encoding.UTF8.GetByteCount(serialized);
        int effectiveBudget = budget > 0 ? budget : ContentResponseBudget;
        bool truncated = !full && totalBytes > effectiveBudget;
        string slice = truncated ? TruncateUtf8(serialized, effectiveBudget) : serialized;

        return JsonSerializer.SerializeToElement(new
        {
            sessionId,
            turnIndex,
            content = truncated ? slice : null,
            turn = truncated ? (JsonElement?)null : turn.Value,
            truncated,
            budget = effectiveBudget,
            totalBytes,
        });
    }

    /// <summary>Returns bounded or full session content optionally filtered by turn, span, or canonical part key.</summary>
    public JsonElement? GetSessionContent(
        string sessionId,
        int? turnIndex = null,
        string? spanId = null,
        string? part = null,
        bool full = false)
    {
        EnsureProjected();
        SessionSummaryRow? session = _store.GetSession(sessionId);
        if (session is null)
            return null;

        IReadOnlyList<CanonicalRecord> records = _store.GetRecordsForSessionKey(sessionId);
        IEnumerable<CanonicalRecord> filtered = records;
        if (!string.IsNullOrEmpty(spanId))
            filtered = filtered.Where(r => string.Equals(r.SpanId, spanId, StringComparison.Ordinal));

        List<object> chunks = [];
        int index = 0;
        foreach (CanonicalRecord record in filtered)
        {
            if (turnIndex.HasValue && index != turnIndex.Value)
            {
                index++;
                continue;
            }

            if (!string.IsNullOrEmpty(part))
            {
                string? partVal = record.Parts is not null
                    ? string.Join("\n", record.Parts.Where(p => string.Equals(p.Part, part, StringComparison.Ordinal)).Select(p => p.Text))
                    : record.Content.TryGetValue(part, out string? v)
                        ? v
                        : null;
                chunks.Add(new { spanId = record.SpanId, index, part, text = partVal ?? string.Empty });
            }
            else
            {
                chunks.Add(new
                {
                    spanId = record.SpanId,
                    index,
                    timestamp = record.Timestamp,
                    content = record.Content,
                    parts = record.Parts,
                });
            }

            index++;
        }

        string rawJson = JsonSerializer.Serialize(chunks);
        int totalBytes = Encoding.UTF8.GetByteCount(rawJson);
        bool truncated = !full && totalBytes > ContentResponseBudget;

        if (!truncated)
        {
            return JsonSerializer.SerializeToElement(new
            {
                sessionId,
                truncated = false,
                budget = ContentResponseBudget,
                totalBytes,
                items = chunks,
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            sessionId,
            truncated = true,
            budget = ContentResponseBudget,
            totalBytes,
            preview = TruncateUtf8(rawJson, ContentResponseBudget),
        });
    }

    /// <summary>Returns stored span attributes and token/cost blocks for <paramref name="spanId"/>.</summary>
    public JsonElement? GetSpanAttributes(string spanId)
    {
        CanonicalRecord? record = _store.GetRecord(spanId);
        if (record is null)
            return null;

        return JsonSerializer.SerializeToElement(new
        {
            spanId = record.SpanId,
            traceId = record.TraceId,
            sessionId = record.SessionId,
            harness = record.Harness,
            source = record.Source,
            name = record.Name,
            op = record.Op,
            kind = record.Kind,
            timestamp = record.Timestamp,
            tokens = record.Tokens,
            cost = record.Cost,
            attributes = record.Raw ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        });
    }

    /// <summary>Returns filtered, paginated findings alongside total count.</summary>
    public JsonElement GetFindings(
        string? runId = null,
        string? sessionId = null,
        string? detector = null,
        string? harness = null,
        int limit = 100,
        int offset = 0)
    {
        EnsureProjected();
        IReadOnlyList<Finding> all = _store.ListFindings(sessionId, runId);
        IEnumerable<Finding> filtered = all;
        if (!string.IsNullOrWhiteSpace(detector))
            filtered = filtered.Where(f => string.Equals(f.DetectorId, detector.Trim(), StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(harness))
        {
            string normHarness = CanonPricing.NormalizeHarnessName(harness.Trim());
            HashSet<string> matchingSessionIds = [.. _store.ListSessions(normHarness).Select(s => s.SessionId)];
            filtered = filtered.Where(f => f.SessionId is not null && matchingSessionIds.Contains(f.SessionId));
        }

        List<Finding> list = [.. filtered];
        int total = list.Count;
        int safeLimit = Math.Clamp(limit, 1, 500);
        int safeOffset = Math.Max(0, offset);
        List<Finding> page = [.. list.Skip(safeOffset).Take(safeLimit)];

        return JsonSerializer.SerializeToElement(new
        {
            findings = page,
            data = page,
            total,
            limit = safeLimit,
            offset = safeOffset,
        });
    }

    /// <summary>Returns a single finding by <paramref name="findingId"/>, or <see langword="null"/> if absent.</summary>
    public Finding? GetFindingById(string findingId)
    {
        EnsureProjected();
        return _store.GetFinding(findingId);
    }

    /// <summary>Returns all harness efficiency rollups (projecting runs and rollups if missing).</summary>
    public JsonElement GetHarnesses()
    {
        EnsureProjected();
        IReadOnlyList<HarnessRollupRow> rollups = _store.ListHarnessRollups();
        if (rollups.Count == 0)
            rollups = CanonRollups.ProjectRunsAndHarnesses(_store);

        return JsonSerializer.SerializeToElement(rollups.Select(r => new
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
            payload = r.Payload,
        }));
    }

    /// <summary>Returns one harness rollup by <paramref name="harnessId"/>, or <see langword="null"/> if absent.</summary>
    public JsonElement? GetHarnessById(string harnessId)
    {
        EnsureProjected();
        string norm = CanonPricing.NormalizeHarnessName(harnessId);
        HarnessRollupRow? row = _store.GetHarnessRollup(norm);
        if (row is null)
            return null;

        return JsonSerializer.SerializeToElement(new
        {
            harness = row.Harness,
            sampleCount = row.SampleCount,
            contextPressureMedian = row.ContextPressureMedian,
            contextPressureP95 = row.ContextPressureP95,
            cacheHitRate = row.CacheHitRate,
            toolYield = row.ToolYield,
            delegationOverhead = row.DelegationOverhead,
            fieldCoverage = row.FieldCoverage,
            measurability = row.Measurability,
            payload = row.Payload,
        });
    }

    /// <summary>Lists all projected runs.</summary>
    public JsonElement GetRuns(string? harness = null)
    {
        EnsureProjected();
        IReadOnlyList<RunRow> runs = _store.ListRuns(harness);
        return JsonSerializer.SerializeToElement(runs);
    }

    /// <summary>Returns one run alongside its executions and scorecard, or <see langword="null"/> if absent.</summary>
    public JsonElement? GetRunById(string runId)
    {
        EnsureProjected();
        RunRow? run = _store.GetRun(runId);
        if (run is null)
            return null;

        IReadOnlyList<ExecutionRow> executions = _store.ListExecutionsForRun(runId);
        JsonElement scorecard = CanonRollups.BuildScorecard(_store, runId);
        return JsonSerializer.SerializeToElement(new
        {
            run,
            executions,
            scorecard,
        });
    }

    /// <summary>Returns the cross-harness comparison table.</summary>
    public JsonElement GetCompare()
    {
        EnsureProjected();
        return CanonRollups.CompareHarnesses(_store);
    }

    /// <summary>Compares two runs by ID, or returns <see langword="null"/> if either is missing.</summary>
    public JsonElement? GetCompareRuns(string runA, string runB)
    {
        EnsureProjected();
        return CanonRollups.CompareRuns(_store, runA, runB);
    }

    /// <summary>Returns paginated metadata-only quarantine entries.</summary>
    public JsonElement GetQuarantinePage(string? harness, int limit, int offset, int page)
    {
        int total = _store.CountQuarantine(harness);
        IReadOnlyList<QuarantineRow> rows = _store.ListQuarantine(harness, limit, offset);
        List<object> entries = [.. rows.Select(r => (object)new
        {
            spanId = r.SpanId,
            observedNamespaces = r.ObservedNamespaces,
            reason = r.Reason,
            source = r.Source,
            name = r.Name,
            timestamp = r.Timestamp,
            harness = r.Harness,
            evidenceAvailable = !string.IsNullOrEmpty(r.RawPayload),
        })];

        return JsonSerializer.SerializeToElement(new
        {
            entries,
            data = entries,
            total,
            page,
            limit,
        });
    }

    /// <summary>Returns full detail for one quarantined span, or <see langword="null"/> if absent.</summary>
    public JsonElement? GetQuarantineDetail(string spanId)
    {
        QuarantineRow? row = _store.GetQuarantined(spanId);
        if (row is null)
            return null;

        return JsonSerializer.SerializeToElement(new
        {
            spanId = row.SpanId,
            observedNamespaces = row.ObservedNamespaces,
            reason = row.Reason,
            source = row.Source,
            name = row.Name,
            timestamp = row.Timestamp,
            harness = row.Harness,
            rawPayload = row.RawPayload,
            evidenceAvailable = !string.IsNullOrEmpty(row.RawPayload),
        });
    }

    /// <summary>Returns paginated metadata-only problems.</summary>
    public JsonElement GetProblemsPage(string? harness, int limit, int offset, int page)
    {
        int total = _store.CountProblems(harness);
        IReadOnlyList<StoredProblemRow> rows = _store.ListProblems(harness, limit, offset);
        List<object> problems = [.. rows.Select(p => (object)new
        {
            id = p.Id,
            spanId = p.SpanId,
            severity = p.Severity,
            code = p.Code,
            message = p.Message,
            location = p.Location,
            sessionId = p.SessionId,
            harness = p.Harness,
            timestamp = p.Timestamp,
        })];

        return JsonSerializer.SerializeToElement(new
        {
            problems,
            data = problems,
            total,
            page,
            limit,
        });
    }

    /// <summary>Returns full detail (including stored stack) for one problem by numeric ID.</summary>
    public JsonElement? GetProblemDetail(string rawId)
    {
        if (!int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return null;

        StoredProblemRow? row = _store.GetProblemById(id);
        if (row is null)
            return null;

        return JsonSerializer.SerializeToElement(new
        {
            id = row.Id,
            spanId = row.SpanId,
            severity = row.Severity,
            code = row.Code,
            message = row.Message,
            location = row.Location,
            sessionId = row.SessionId,
            harness = row.Harness,
            timestamp = row.Timestamp,
            stack = row.Stack,
        });
    }

    /// <summary>Returns store metadata and REST contract version for <c>/api/kyber/meta</c>.</summary>
    public JsonElement GetMeta()
    {
        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = CanonSchema.SchemaVersion,
            detectorVersion = CanonSchema.DetectorVersion,
            storeGeneration = _store.ComputeStoreGeneration(),
            recordCount = _store.CountRecords(),
            sessionCount = _store.ListSessions().Count,
            findingCount = _store.ListFindings().Count,
            version = _version,
            apiVersion = ApiContractVersion,
        });
    }

    /// <summary>Returns the store-free build and REST contract version for <c>/api/kyber/version</c>.</summary>
    public JsonElement GetVersion()
    {
        return JsonSerializer.SerializeToElement(new
        {
            version = _version,
            apiVersion = ApiContractVersion,
        });
    }

    /// <summary>Returns per-harness field coverage metrics.</summary>
    public JsonElement GetCoverage()
    {
        EnsureProjected();
        IReadOnlyList<HarnessRollupRow> rollups = _store.ListHarnessRollups();
        return JsonSerializer.SerializeToElement(new
        {
            dimensions = CanonRollups.CoverageDimensions,
            harnesses = rollups.Select(r => new
            {
                harness = r.Harness,
                fieldCoverage = r.FieldCoverage,
                sampleCount = r.SampleCount,
                measurability = r.Measurability,
            }),
        });
    }

    /// <summary>Builds a bounded diagnostic report for <c>/api/kyber/report</c>.</summary>
    public JsonElement GetReport(string? harness = null, string? sessionId = null, string? runId = null, int limit = 50)
    {
        EnsureProjected();
        IReadOnlyList<SessionSummaryRow> sessions = _store.ListSessions(harness);
        if (!string.IsNullOrEmpty(sessionId))
            sessions = [.. sessions.Where(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal))];

        IReadOnlyList<Finding> findings = _store.ListFindings(sessionId, runId);
        IReadOnlyList<HarnessRollupRow> harnesses = _store.ListHarnessRollups();

        int boundedLimit = Math.Clamp(limit, 1, 200);
        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = ApiContractVersion,
            generatedAt = _clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            storeGeneration = _store.ComputeStoreGeneration(),
            sessionCount = sessions.Count,
            findingCount = findings.Count,
            harnesses,
            findings = findings.Take(boundedLimit),
        });
    }

    /// <summary>Returns timeline span trees for <paramref name="sessionId"/> (or all sessions if omitted).</summary>
    public JsonElement GetTimeline(string? sessionId = null)
    {
        EnsureProjected();
        IReadOnlyList<SessionSummaryRow> sessions = string.IsNullOrWhiteSpace(sessionId)
            ? _store.ListSessions().Take(25).ToList()
            : _store.GetSession(sessionId) is SessionSummaryRow single
                ? [single]
                : [];

        List<object> trees = [];
        foreach (SessionSummaryRow session in sessions)
        {
            IReadOnlyList<CanonicalRecord> records = _store.GetRecordsForSessionKey(session.SessionId);
            TimelineNode timeline = CanonContextAnalysis.BuildTimeline(records);
            trees.Add(new { sessionId = session.SessionId, harness = session.Harness, timeline });
        }

        return JsonSerializer.SerializeToElement(new { sessions = trees });
    }

    /// <summary>Lists recorded predictions.</summary>
    public JsonElement GetPredictions(string? runId = null)
    {
        return JsonSerializer.SerializeToElement(_store.ListPredictions(runId));
    }

    /// <summary>Records a prediction from a finding/run request body.</summary>
    public bool TryRecordPrediction(JsonElement body, out PredictionRow? created, out string? error)
    {
        created = null;
        error = null;
        if (body.ValueKind != JsonValueKind.Object)
        {
            error = "Prediction body must be a JSON object";
            return false;
        }

        string? findingId = body.TryGetProperty("findingId", out JsonElement fEl) && fEl.ValueKind == JsonValueKind.String
            ? fEl.GetString()
            : null;
        string? runId = body.TryGetProperty("runId", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String
            ? rEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(findingId) || string.IsNullOrWhiteSpace(runId))
        {
            error = "findingId and runId are required";
            return false;
        }

        long predictedWaste = body.TryGetProperty("predictedWasteTokens", out JsonElement wEl) && wEl.TryGetInt64(out long w)
            ? w
            : 0;
        double confidence = body.TryGetProperty("confidence", out JsonElement cEl) && cEl.TryGetDouble(out double c)
            ? Math.Clamp(c, 0.0, 1.0)
            : 0.5;
        string id = string.Create(CultureInfo.InvariantCulture, $"pred-{Guid.NewGuid():N}");
        string nowIso = _clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        created = new PredictionRow(
            Id: id,
            FindingId: findingId,
            RunId: runId,
            PredictedWasteTokens: predictedWaste,
            Confidence: confidence,
            ConfidenceTier: confidence >= 0.8 ? "high" : confidence >= 0.5 ? "medium" : "low",
            ErrorBar: null,
            CreatedAt: nowIso);
        _store.UpsertPrediction(created);
        return true;
    }

    /// <summary>Returns prediction calibration summary across recorded predictions.</summary>
    public JsonElement GetCalibration()
    {
        IReadOnlyList<PredictionRow> predictions = _store.ListPredictions();
        List<PredictionRow> scored = [.. predictions.Where(p => p.CalibrationScore.HasValue)];
        double? meanScore = scored.Count > 0 ? Math.Round(scored.Average(p => p.CalibrationScore!.Value), 4) : null;

        return JsonSerializer.SerializeToElement(new
        {
            totalPredictions = predictions.Count,
            evaluatedCount = scored.Count,
            meanCalibrationScore = meanScore,
            predictions,
        });
    }

    /// <summary>Executes an <c>/api/kyber/analytics/*</c> operation returning the versioned <c>AnalyticsEnvelope</c>.</summary>
    public (int StatusCode, JsonElement Envelope) ExecuteAnalytics(string operation, JsonElement? request)
    {
        string nowIso = _clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        uint generation = _store.ComputeStoreGeneration();
        object freshness = new
        {
            generatedAt = nowIso,
            instanceId = _instanceId,
            projectionGeneration = generation.ToString(CultureInfo.InvariantCulture),
            storeGeneration = generation,
        };
        object defaultScope = new
        {
            window = new { from = (string?)null, to = (string?)null },
            harnesses = Array.Empty<string>(),
        };

        if (request.HasValue && request.Value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
        {
            return (400, BuildAnalyticsEnvelope(defaultScope, null, new { }, freshness, new { }, "invalid_request", "The request must be a JSON object.", "Send a JSON object matching the operation catalog."));
        }

        if (!AnalyticsOperations.Contains(operation, StringComparer.Ordinal))
        {
            return (400, BuildAnalyticsEnvelope(defaultScope, null, new { }, freshness, new { }, "unsupported_query", $"Unsupported analytics operation '{operation}'.", "Query /api/kyber/analytics/catalog for supported operations."));
        }

        EnsureProjected();

        if (string.Equals(operation, "status", StringComparison.Ordinal))
        {
            object statusData = new
            {
                value = new
                {
                    ready = true,
                    storeGeneration = generation,
                    sessionCount = _store.ListSessions().Count,
                    recordCount = _store.CountRecords(),
                    findingCount = _store.ListFindings().Count,
                },
            };
            return (200, BuildAnalyticsEnvelope(defaultScope, statusData, new { }, freshness, new { }, null, null, null));
        }

        if (string.Equals(operation, "catalog", StringComparison.Ordinal))
        {
            object catalogData = new
            {
                operations = AnalyticsOperations,
                dimensions = new[] { "harness", "model", "repo", "outcome" },
                metrics = new[] { "sessions", "turns", "total_input", "total_output", "cache_read" },
                source = new { harnesses = _store.ListObservedHarnesses() },
            };
            return (200, BuildAnalyticsEnvelope(defaultScope, catalogData, new { }, freshness, new { }, null, null, null));
        }

        string? harnessFilter = request.HasValue && request.Value.ValueKind == JsonValueKind.Object
            && request.Value.TryGetProperty("harness", out JsonElement hEl) && hEl.ValueKind == JsonValueKind.String
                ? hEl.GetString()
                : null;
        IReadOnlyList<SessionSummaryRow> sessions = _store.ListSessions(harnessFilter);

        if (string.Equals(operation, "search-sessions", StringComparison.Ordinal))
        {
            string? queryText = request.HasValue && request.Value.ValueKind == JsonValueKind.Object
                && request.Value.TryGetProperty("query", out JsonElement qEl) && qEl.ValueKind == JsonValueKind.String
                    ? qEl.GetString()
                    : null;

            IEnumerable<SessionSummaryRow> matched = sessions;
            if (!string.IsNullOrWhiteSpace(queryText))
            {
                matched = matched.Where(s =>
                    s.SessionId.Contains(queryText, StringComparison.OrdinalIgnoreCase)
                    || (s.Label?.Contains(queryText, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (s.Repo?.Contains(queryText, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            List<SessionSummaryRow> items = [.. matched.Take(50)];
            object searchData = new
            {
                items = items.Select(s => new
                {
                    sessionId = s.SessionId,
                    harness = s.Harness,
                    label = s.Label,
                    started = s.Started,
                    ended = s.Ended,
                    repo = s.Repo,
                }),
            };
            return (200, BuildAnalyticsEnvelope(defaultScope, searchData, new { }, freshness, new { returnedCount = items.Count, nextCursor = (string?)null }, null, null, null));
        }

        if (string.Equals(operation, "inspect", StringComparison.Ordinal))
        {
            string? sessionId = request.HasValue && request.Value.ValueKind == JsonValueKind.Object
                && request.Value.TryGetProperty("sessionId", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.String
                    ? sEl.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return (400, BuildAnalyticsEnvelope(defaultScope, null, new { }, freshness, new { }, "invalid_request", "sessionId is required for inspect.", "Provide { \"sessionId\": \"...\" }."));
            }

            JsonElement? summary = GetSessionSummary(sessionId);
            if (!summary.HasValue)
            {
                return (404, BuildAnalyticsEnvelope(defaultScope, null, new { }, freshness, new { }, "not_found", $"Session '{sessionId}' was not found.", "Verify the sessionId from search-sessions."));
            }

            return (200, BuildAnalyticsEnvelope(defaultScope, new { session = summary.Value }, new { }, freshness, new { }, null, null, null));
        }

        if (string.Equals(operation, "findings", StringComparison.Ordinal))
        {
            IReadOnlyList<Finding> findings = _store.ListFindings();
            return (200, BuildAnalyticsEnvelope(defaultScope, new { items = findings }, new { }, freshness, new { returnedCount = findings.Count, nextCursor = (string?)null }, null, null, null));
        }

        if (string.Equals(operation, "compare", StringComparison.Ordinal))
        {
            return (200, BuildAnalyticsEnvelope(defaultScope, new { deltas = CanonRollups.CompareHarnesses(_store) }, new { }, freshness, new { }, null, null, null));
        }

        if (string.Equals(operation, "outcomes", StringComparison.Ordinal))
        {
            List<object> outcomes = [.. sessions.Select(s => (object)new
            {
                sessionId = s.SessionId,
                harness = s.Harness,
                outcome = ExtractOutcome(s),
            })];
            return (200, BuildAnalyticsEnvelope(defaultScope, new { items = outcomes }, new { }, freshness, new { returnedCount = outcomes.Count, nextCursor = (string?)null }, null, null, null));
        }

        // operation == "query"
        List<object> groups = [.. sessions
            .GroupBy(s => s.Harness, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (object)new
            {
                key = new { harness = g.Key },
                sessions = g.Count(),
            })];
        return (200, BuildAnalyticsEnvelope(defaultScope, new { groups }, new { }, freshness, new { returnedCount = groups.Count, nextCursor = (string?)null }, null, null, null));
    }

    private static JsonElement BuildAnalyticsEnvelope(
        object scope,
        object? data,
        object coverage,
        object freshness,
        object pagination,
        string? errorCode,
        string? errorMessage,
        string? errorHint)
    {
        object? error = errorCode is null
            ? null
            : new { code = errorCode, message = errorMessage, hint = errorHint };

        return JsonSerializer.SerializeToElement(new
        {
            contractVersion = ApiContractVersion,
            scope,
            data,
            measurement = new { },
            coverage,
            freshness,
            evidence = Array.Empty<object>(),
            pagination,
            error,
        });
    }

    private static JsonElement? ParseSummary(SessionSummaryRow row)
    {
        if (!string.IsNullOrEmpty(row.SummaryJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(row.SummaryJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
        }

        if (row.Payload.ValueKind == JsonValueKind.Object
            && row.Payload.TryGetProperty("summary", out JsonElement embedded)
            && embedded.ValueKind == JsonValueKind.Object)
        {
            return embedded.Clone();
        }

        return null;
    }

    private static string ExtractOutcome(SessionSummaryRow row)
    {
        if (row.Payload.ValueKind == JsonValueKind.Object
            && row.Payload.TryGetProperty("outcome", out JsonElement outEl)
            && outEl.ValueKind == JsonValueKind.Object
            && outEl.TryGetProperty("status", out JsonElement stEl)
            && stEl.ValueKind == JsonValueKind.String)
        {
            return stEl.GetString() ?? "not_measurable";
        }

        return "not_measurable";
    }

    private static long? TryGetNullableLong(JsonElement? obj, string name)
    {
        if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object)
            return null;

        return obj.Value.TryGetProperty(name, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt64(out long val)
                ? val
                : null;
    }

    private static int? TryGetNullableInt(JsonElement? obj, string name)
    {
        if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object)
            return null;

        return obj.Value.TryGetProperty(name, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out int val)
                ? val
                : null;
    }

    private static IReadOnlyList<string> ExtractStringArray(JsonElement? obj, string name)
    {
        if (!obj.HasValue
            || obj.Value.ValueKind != JsonValueKind.Object
            || !obj.Value.TryGetProperty(name, out JsonElement el)
            || el.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> items = [];
        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is string s)
                items.Add(s);
        }

        return items;
    }

    private static JsonElement ExtractCost(JsonElement? summary)
    {
        if (summary.HasValue
            && summary.Value.ValueKind == JsonValueKind.Object
            && summary.Value.TryGetProperty("cost", out JsonElement costEl)
            && costEl.ValueKind == JsonValueKind.Object)
        {
            return costEl.Clone();
        }

        return JsonSerializer.SerializeToElement(new { basis = "unknown", status = "no_rate" });
    }

    private static double? ExtractUsdCost(JsonElement cost)
    {
        if (cost.ValueKind == JsonValueKind.Object
            && cost.TryGetProperty("status", out JsonElement statusEl)
            && string.Equals(statusEl.GetString(), "priced", StringComparison.Ordinal)
            && cost.TryGetProperty("currency", out JsonElement currEl)
            && string.Equals(currEl.GetString(), "USD", StringComparison.Ordinal)
            && cost.TryGetProperty("value", out JsonElement valEl)
            && valEl.ValueKind == JsonValueKind.Number
            && valEl.TryGetDouble(out double val)
            && double.IsFinite(val))
        {
            return val;
        }

        return null;
    }

    private static bool IsAncestorOrSelf(string candidateId, string parentId, Dictionary<string, SessionNode> byId)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? cursor = parentId;
        while (!string.IsNullOrEmpty(cursor) && seen.Add(cursor))
        {
            if (string.Equals(cursor, candidateId, StringComparison.Ordinal))
                return true;

            cursor = byId.TryGetValue(cursor, out SessionNode? node) ? node.ParentSession : null;
        }

        return false;
    }

    private static object ToSessionDto(SessionNode node)
    {
        return new
        {
            session_id = node.SessionId,
            harness = node.Harness,
            label = node.Label,
            is_subagent = node.IsSubagent,
            parent_session = node.ParentSession,
            agent_name = node.AgentName,
            repo = node.Repo,
            branch = node.Branch,
            started = node.Started,
            ended = node.Ended,
            turn_count = node.TurnCount,
            request_count = node.RequestCount,
            total_input = node.TotalInput,
            total_output = node.TotalOutput,
            cost_usd = node.CostUsd,
            cost = node.Cost,
            models = node.Models,
            problems = node.Problems,
            subagents = node.Subagents.Select(ToSessionDto).ToList(),
        };
    }

    private static string TruncateUtf8(string text, int maxBytes)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length <= maxBytes)
            return text;

        int end = maxBytes;
        while (end > 0 && (utf8[end] & 0b1100_0000) == 0b1000_0000)
            end--;

        return Encoding.UTF8.GetString(utf8, 0, end);
    }

    private sealed record SessionNode(
        string SessionId,
        string Harness,
        string Label,
        bool IsSubagent,
        string? ParentSession,
        string? AgentName,
        string? Repo,
        string? Branch,
        string? Started,
        string? Ended,
        int? TurnCount,
        int? RequestCount,
        long? TotalInput,
        long? TotalOutput,
        double? CostUsd,
        JsonElement Cost,
        IReadOnlyList<string> Models,
        int Problems,
        List<SessionNode> Subagents);
}
