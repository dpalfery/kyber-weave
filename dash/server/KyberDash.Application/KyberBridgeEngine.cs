using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Bounded read and analytics query bridge over <see cref="ICanonStoreRepository"/> serving
/// the KyberDash HTTP and MCP contracts.
/// </summary>
public class KyberBridgeEngine
{
    /// <summary>Default byte budget applied to turn and session content responses (32 KiB).</summary>
    public const int ContentResponseBudget = 32_768;

    /// <summary>REST contract schema version expected by desktop tray and web clients.</summary>
    public const int ApiContractVersion = 1;

    /// <summary>Default KyberDash engine version reported by <c>/api/kyber/version</c> and <c>/api/kyber/meta</c>.</summary>
    public const string DefaultVersion = "0.21.0";

    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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

    private readonly ICanonStoreRepository store;
    private readonly ISettingsRepository settings;
    private readonly IPricingRepository pricing;
    private readonly CanonSessionProjectorEngine projector;
    private readonly CanonRollupsEngine rollups;
    private readonly Func<DateTimeOffset> clock;
    private readonly string version;
    private readonly string instanceId;
    private readonly IProjectionStore? predictionStore;

    /// <summary>
    /// Initializes a new <see cref="KyberBridgeEngine"/>.
    /// </summary>
    /// <param name="store">Canonical store repository.</param>
    /// <param name="settings">Operator settings repository.</param>
    /// <param name="pricing">Model rate-table repository.</param>
    /// <param name="projector">Session projection engine.</param>
    /// <param name="rollups">Run and harness rollup engine.</param>
    /// <param name="version">Engine build version string.</param>
    /// <param name="clock">Optional UTC clock override for deterministic timestamps.</param>
    /// <param name="instanceId">Optional process instance identifier for analytics freshness metadata.</param>
    /// <param name="predictionStore">Optional typed prediction store. When provided, predictions persist
    /// through <see cref="IProjectionStore"/> as <c>prediction</c> rows with optional
    /// <c>prediction_comparison</c>, <c>prediction_observed_delta</c>, and
    /// <c>prediction_calibration</c> child rows; otherwise they use the legacy repository.</param>
    public KyberBridgeEngine(
        ICanonStoreRepository store,
        ISettingsRepository settings,
        IPricingRepository pricing,
        CanonSessionProjectorEngine projector,
        CanonRollupsEngine rollups,
        string version = DefaultVersion,
        Func<DateTimeOffset>? clock = null,
        string? instanceId = null,
        IProjectionStore? predictionStore = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(rollups);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        this.store = store;
        this.settings = settings;
        this.pricing = pricing;
        this.projector = projector;
        this.rollups = rollups;
        this.version = version;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.instanceId = string.IsNullOrWhiteSpace(instanceId)
            ? string.Create(CultureInfo.InvariantCulture, $"kyberdash-{Environment.ProcessId}")
            : instanceId;
        this.predictionStore = predictionStore;
    }

    /// <summary>Ensures dirty sessions, runs, executions, and harness rollups are projected up to date.</summary>
    public void EnsureProjected()
    {
        bool builtOrPruned = false;
        if (!projector.IsProjectionCurrent())
        {
            ProjectionPassReport report = projector.ProjectIncremental();
            builtOrPruned = report.Built > 0 || report.Pruned > 0;
        }

        if (builtOrPruned
            || (store.ListSessions().Count > 0 && store.ListHarnessRollups().Count == 0))
        {
            rollups.ProjectRunsAndHarnesses();
        }
    }

    /// <summary>Returns hierarchical root-first sessions with nested subagents and optional <paramref name="limit"/>.</summary>
    /// <param name="limit">Maximum root sessions returned when positive.</param>
    /// <param name="harness">Optional harness filter.</param>
    /// <returns>Serialized session tree array.</returns>
    public JsonElement GetSessions(int? limit = null, string? harness = null)
    {
        EnsureProjected();
        IReadOnlyList<SessionSummaryRow> rows = store.ListSessions(harness);
        Dictionary<string, SessionNode> byId = new(StringComparer.Ordinal);
        List<SessionNode> orderedNodes = [];

        foreach (SessionSummaryRow row in rows)
        {
            JsonElement? summary = ParseSummary(row);
            JsonElement? totalInput = TryGetOptionalProperty(summary, "total_input");
            JsonElement? totalOutput = TryGetOptionalProperty(summary, "total_output");
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
        {
            node.Subagents.Sort((a, b) => string.Compare(a.Started, b.Started, StringComparison.Ordinal));
        }

        roots.Sort((a, b) => string.Compare(b.Started, a.Started, StringComparison.Ordinal));

        if (limit.HasValue && limit.Value > 0 && roots.Count > limit.Value)
        {
            roots = roots.GetRange(0, limit.Value);
        }

        return JsonSerializer.SerializeToElement(roots.Select(ToSessionDto).ToList(), CamelCaseJsonOptions);
    }

    /// <summary>Returns the hydrated full session payload (with materialized turns), or <see langword="null"/> if missing.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <returns>Full session JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetSessionDetail(string sessionId)
    {
        EnsureProjected();
        SessionSummaryRow? session = store.GetSession(sessionId);
        if (session is null)
        {
            return null;
        }

        Dictionary<string, object?> root = new(StringComparer.Ordinal);
        if (session.Payload.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in session.Payload.EnumerateObject())
            {
                root[prop.Name] = prop.Value;
            }
        }

        root["session_id"] = session.SessionId;
        root["sessionId"] = session.SessionId;
        root["harness"] = session.Harness;
        root["label"] = session.Label;
        root["is_subagent"] = session.IsSubagent;
        root["parent_session"] = session.ParentSession;
        root["agent_name"] = session.AgentName;
        root["repo"] = session.Repo;
        root["branch"] = session.Branch;
        root["started"] = session.Started;
        root["ended"] = session.Ended;
        root["problems_count"] = session.ProblemsCount ?? 0;
        root["context_limit_source"] = session.ContextLimitSource;

        if (!root.ContainsKey("summary"))
        {
            root["summary"] = ParseSummary(session);
        }

        if (!root.ContainsKey("problems"))
        {
            root["problems"] = Array.Empty<object>();
        }

        IReadOnlyList<JsonElement> turns = store.ListSessionTurnPayloads(sessionId);
        if (!root.TryGetValue("turns", out object? existingTurnsObj)
            || existingTurnsObj is not JsonElement existingTurnsEl
            || existingTurnsEl.ValueKind != JsonValueKind.Array
            || existingTurnsEl.GetArrayLength() == 0)
        {
            root["turns"] = turns;
        }

        root["payload"] = session.Payload;
        return JsonSerializer.SerializeToElement(root, CamelCaseJsonOptions);
    }

    /// <summary>Returns the bounded session summary (without full turn bodies), or <see langword="null"/> if missing.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <returns>Summary JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetSessionSummary(string sessionId)
    {
        EnsureProjected();
        SessionSummaryRow? session = store.GetSession(sessionId);
        if (session is null)
        {
            return null;
        }

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
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="turnIndex">Zero-based turn index.</param>
    /// <returns>Turn figures JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetTurnFigures(string sessionId, int turnIndex)
    {
        EnsureProjected();
        JsonElement? turn = store.GetSessionTurnPayload(sessionId, turnIndex);
        if (!turn.HasValue)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(new
        {
            sessionId,
            turnIndex,
            turn = turn.Value,
        });
    }

    /// <summary>Returns bounded or full text/parts content for a single turn.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="turnIndex">Zero-based turn index.</param>
    /// <param name="budget">Byte budget applied when <paramref name="full"/> is false.</param>
    /// <param name="full">Whether to return the full payload untruncated.</param>
    /// <returns>Turn content JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetTurnContent(string sessionId, int turnIndex, int budget = ContentResponseBudget, bool full = false)
    {
        EnsureProjected();
        JsonElement? turn = store.GetSessionTurnPayload(sessionId, turnIndex);
        if (!turn.HasValue)
        {
            return null;
        }

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
        }, CamelCaseJsonOptions);
    }

    /// <summary>Returns bounded or full session content optionally filtered by turn, span, or canonical part key.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="turnIndex">Optional zero-based turn filter.</param>
    /// <param name="spanId">Optional span identifier filter.</param>
    /// <param name="part">Optional canonical content part key.</param>
    /// <param name="full">Whether to omit truncation preview slicing.</param>
    /// <returns>Session content JSON element, or <see langword="null"/> when the session is absent.</returns>
    public JsonElement? GetSessionContent(
        string sessionId,
        int? turnIndex = null,
        string? spanId = null,
        string? part = null,
        bool full = false)
    {
        EnsureProjected();
        SessionSummaryRow? session = store.GetSession(sessionId);
        if (session is null)
        {
            return null;
        }

        IReadOnlyList<CanonicalRecord> records = store.ListRecordsBySessionKey(sessionId);
        IEnumerable<CanonicalRecord> filtered = records;
        if (!string.IsNullOrEmpty(spanId))
        {
            filtered = filtered.Where(r => string.Equals(r.SpanId, spanId, StringComparison.Ordinal));
        }

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
                // One flat entry per captured part, the shape the part-filtered branch and the
                // dashboard's content reader both use. A span that captured nothing contributes
                // no entry, rather than a part-less wrapper the reader cannot render.
                IEnumerable<(string Name, string Text, ContentPart? Source)> captured = record.Parts is not null
                    ? record.Parts.Select(p => (p.Part, p.Text, (ContentPart?)p))
                    : record.Content.Select(c => (c.Key, c.Value, (ContentPart?)null));
                foreach ((string name, string text, ContentPart? source) in captured)
                {
                    Dictionary<string, object> entry = new(StringComparer.Ordinal)
                    {
                        ["spanId"] = record.SpanId,
                        ["index"] = index,
                        ["part"] = name,
                        ["text"] = text ?? string.Empty,
                    };
                    if (source?.Tokens is long tokens) entry["tokens"] = tokens;
                    if (source?.Server is string server) entry["server"] = server;
                    if (source?.Resident is bool resident) entry["resident"] = resident;
                    if (source?.Truncated is bool clipped) entry["truncated"] = clipped;
                    chunks.Add(entry);
                }
            }

            index++;
        }

        string rawJson = JsonSerializer.Serialize(chunks, CamelCaseJsonOptions);
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
                parts = chunks,
                items = chunks,
            }, CamelCaseJsonOptions);
        }

        return JsonSerializer.SerializeToElement(new
        {
            sessionId,
            truncated = true,
            budget = ContentResponseBudget,
            totalBytes,
            preview = TruncateUtf8(rawJson, ContentResponseBudget),
            // A truncated answer must stay inside the budget, so it withholds the full arrays and
            // carries the preview instead.
            parts = Array.Empty<object>(),
            items = Array.Empty<object>(),
        }, CamelCaseJsonOptions);
    }

    /// <summary>Returns stored span attributes and token/cost blocks for <paramref name="spanId"/>.</summary>
    /// <param name="spanId">Span identifier.</param>
    /// <returns>Span attributes JSON element, or <see langword="null"/> when absent.</returns>
    public JsonElement? GetSpanAttributes(string spanId)
    {
        CanonicalRecord? record = store.GetRecord(spanId);
        if (record is null)
        {
            return null;
        }

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
        }, CamelCaseJsonOptions);
    }

    /// <summary>Returns filtered, paginated findings alongside total and per-detector counts.</summary>
    /// <param name="runId">Optional run filter.</param>
    /// <param name="sessionId">Optional session filter.</param>
    /// <param name="detector">Optional detector identifier filter.</param>
    /// <param name="harness">Optional harness filter.</param>
    /// <param name="limit">Maximum findings returned per page.</param>
    /// <param name="offset">Zero-based pagination offset.</param>
    /// <param name="page">One-based page number echoed in the response.</param>
    /// <returns>Paginated findings JSON element.</returns>
    public JsonElement GetFindings(
        string? runId = null,
        string? sessionId = null,
        string? detector = null,
        string? harness = null,
        int limit = 100,
        int offset = 0,
        int page = 1)
    {
        EnsureProjected();
        IReadOnlyList<Finding> all = store.ListFindings(sessionId: sessionId, runId: runId);
        IEnumerable<Finding> scoped = all;
        if (!string.IsNullOrWhiteSpace(harness))
        {
            string normHarness = CanonPricing.NormalizeHarnessName(harness.Trim());
            HashSet<string> matchingSessionIds = [.. store.ListSessions(normHarness).Select(s => s.SessionId)];
            HashSet<string> matchingRunIds = [.. store.ListRuns(normHarness).Select(r => r.RunId)];
            scoped = scoped.Where(f =>
                (f.SessionId is not null && matchingSessionIds.Contains(f.SessionId))
                || (f.RunId is not null && matchingRunIds.Contains(f.RunId)));
        }

        List<Finding> baseList = [.. scoped];
        Dictionary<string, int> detectorCounts = new(StringComparer.Ordinal);
        foreach (string detectorId in CanonFindings.DetectorIds)
        {
            detectorCounts[detectorId] = 0;
        }

        foreach (Finding finding in baseList)
        {
            detectorCounts.TryGetValue(finding.DetectorId, out int current);
            detectorCounts[finding.DetectorId] = current + 1;
        }

        IEnumerable<Finding> filtered = baseList;
        if (!string.IsNullOrWhiteSpace(detector))
        {
            filtered = filtered.Where(f => string.Equals(f.DetectorId, detector.Trim(), StringComparison.Ordinal));
        }

        List<Finding> list = [.. filtered];
        int total = list.Count;
        int safeLimit = Math.Clamp(limit, 1, 500);
        int safeOffset = Math.Max(0, offset);
        List<Finding> pageItems = [.. list.Skip(safeOffset).Take(safeLimit)];

        return JsonSerializer.SerializeToElement(new
        {
            findings = pageItems,
            data = pageItems,
            total,
            limit = safeLimit,
            offset = safeOffset,
            page,
            detectorCounts,
            unknownWindowSessions = 0,
        }, CamelCaseJsonOptions);
    }

    /// <summary>Returns a single finding by <paramref name="findingId"/>, or <see langword="null"/> if absent.</summary>
    /// <param name="findingId">Finding identifier.</param>
    /// <returns>Finding record, or <see langword="null"/>.</returns>
    public Finding? GetFindingById(string findingId)
    {
        EnsureProjected();
        return store.GetFinding(findingId);
    }

    /// <summary>Returns all harness efficiency rollups (projecting runs and rollups if missing).</summary>
    /// <returns>Harnesses payload JSON element.</returns>
    public JsonElement GetHarnesses()
    {
        EnsureProjected();
        IReadOnlyList<HarnessRollupRow> harnessRollups = store.ListHarnessRollups();
        if (harnessRollups.Count == 0)
        {
            harnessRollups = rollups.ProjectRunsAndHarnesses();
        }

        HarnessAggregationContext context = BuildHarnessAggregationContext();
        return JsonSerializer.SerializeToElement(new
        {
            harnesses = harnessRollups.Select(r => BuildHarnessSummaryDto(r, context)),
        }, CamelCaseJsonOptions);
    }

    /// <summary>Returns one harness rollup by <paramref name="harnessId"/>, or <see langword="null"/> if absent.</summary>
    /// <param name="harnessId">Harness identifier.</param>
    /// <returns>Harness rollup JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetHarnessById(string harnessId)
    {
        EnsureProjected();
        string norm = CanonPricing.NormalizeHarnessName(harnessId);
        HarnessRollupRow? row = store.GetHarnessRollup(norm);
        if (row is null)
        {
            return null;
        }

        HarnessAggregationContext context = BuildHarnessAggregationContext();
        return JsonSerializer.SerializeToElement(BuildHarnessSummaryDto(row, context), CamelCaseJsonOptions);
    }

    /// <summary>Lists all projected runs.</summary>
    /// <param name="harness">Optional harness filter.</param>
    /// <returns>Runs payload JSON element.</returns>
    public JsonElement GetRuns(string? harness = null)
    {
        EnsureProjected();
        IReadOnlyList<RunRow> runRows = store.ListRuns(harness);
        Dictionary<string, int> findingCounts = CountFindingsByRun();
        Dictionary<string, SessionSummaryRow> sessionsById = store.ListSessions()
            .ToDictionary(static s => s.SessionId, StringComparer.Ordinal);

        List<object> mappedRuns = new(runRows.Count);
        foreach (RunRow run in runRows)
        {
            IReadOnlyList<ExecutionRow> execs = store.ListExecutionsForRun(run.RunId);
            RunSessionFigures figures = SummarizeRunSessions(execs, sessionsById);
            mappedRuns.Add(new
            {
                runId = run.RunId,
                harness = run.Harness,
                label = run.Label,
                groupingBasis = run.GroupingBasis,
                groupingRule = run.GroupingRule,
                workingDirectory = run.WorkingDirectory,
                started = run.Started,
                ended = run.Ended,
                executionCount = execs.Count > 0 ? execs.Count : run.ExecutionCount,
                turnCount = figures.TurnCount,
                totalInput = figures.TotalInput,
                totalOutput = figures.TotalOutput,
                costUsd = figures.CostUsd,
                findingCount = findingCounts.GetValueOrDefault(run.RunId),
                payload = run.Payload,
            });
        }

        return JsonSerializer.SerializeToElement(new { runs = mappedRuns }, CamelCaseJsonOptions);
    }

    /// <summary>Returns one run alongside its executions and scorecard, or <see langword="null"/> if absent.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <returns>Run detail JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetRunById(string runId)
    {
        EnsureProjected();
        RunRow? run = store.GetRun(runId);
        if (run is null)
        {
            return null;
        }

        IReadOnlyList<ExecutionRow> executions = store.ListExecutionsForRun(runId);
        IReadOnlyList<Finding> findings = store.ListFindings(runId: runId);
        JsonElement scorecard = rollups.BuildScorecard(runId);

        Dictionary<string, SessionSummaryRow> sessionsById = new(StringComparer.Ordinal);
        foreach (ExecutionRow exec in executions)
        {
            if (string.IsNullOrEmpty(exec.SessionId) || sessionsById.ContainsKey(exec.SessionId))
            {
                continue;
            }

            SessionSummaryRow? session = store.GetSession(exec.SessionId);
            if (session is not null)
            {
                sessionsById[exec.SessionId] = session;
            }
        }

        RunSessionFigures figures = SummarizeRunSessions(executions, sessionsById);
        List<object> enrichedExecutions = new(executions.Count);
        foreach (ExecutionRow exec in executions)
        {
            SessionSummaryFigures? sFig = !string.IsNullOrEmpty(exec.SessionId)
                && sessionsById.TryGetValue(exec.SessionId, out SessionSummaryRow? s)
                    ? ExtractSessionSummaryFigures(s)
                    : null;
            enrichedExecutions.Add(new
            {
                executionId = exec.ExecutionId,
                runId = exec.RunId,
                sessionId = exec.SessionId,
                parentExecutionId = exec.ParentExecutionId,
                harness = exec.Harness,
                agentName = exec.AgentName,
                isRoot = exec.IsRoot,
                started = exec.Started,
                ended = exec.Ended,
                parentLinkage = exec.ParentLinkage,
                turnCount = sFig?.TurnCount,
                costUsd = sFig?.CostUsd,
                payload = exec.Payload,
            });
        }

        List<object> executionTree = BuildExecutionTree(executions, sessionsById);
        List<object> turns = ExtractRunTurns(executions);

        return JsonSerializer.SerializeToElement(new
        {
            run = new
            {
                runId = run.RunId,
                harness = run.Harness,
                label = run.Label,
                groupingBasis = run.GroupingBasis,
                groupingRule = run.GroupingRule,
                workingDirectory = run.WorkingDirectory,
                started = run.Started,
                ended = run.Ended,
                executionCount = executions.Count > 0 ? executions.Count : run.ExecutionCount,
                turnCount = figures.TurnCount,
                totalInput = figures.TotalInput,
                totalOutput = figures.TotalOutput,
                costUsd = figures.CostUsd,
                findingCount = findings.Count,
                payload = run.Payload,
            },
            executions = enrichedExecutions,
            executionTree,
            findings,
            turns,
            scorecard,
        }, CamelCaseJsonOptions);
    }

    /// <summary>Returns the cross-harness comparison table.</summary>
    /// <returns>Comparison JSON element.</returns>
    public JsonElement GetCompare()
    {
        EnsureProjected();
        return rollups.CompareHarnesses();
    }

    /// <summary>Compares two runs by ID, or returns <see langword="null"/> if either is missing.</summary>
    /// <param name="runA">Baseline run identifier.</param>
    /// <param name="runB">Comparison run identifier.</param>
    /// <returns>Comparison JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetCompareRuns(string runA, string runB)
    {
        EnsureProjected();
        return rollups.CompareRuns(runA, runB);
    }

    /// <summary>Returns paginated metadata-only quarantine entries.</summary>
    /// <param name="harness">Optional harness filter.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="offset">Zero-based row offset.</param>
    /// <param name="page">One-based page number echoed in the payload.</param>
    /// <returns>Quarantine page JSON element.</returns>
    public JsonElement GetQuarantinePage(string? harness, int limit, int offset, int page)
    {
        int total = store.CountQuarantine(harness);
        IReadOnlyList<QuarantineRow> rows = store.ListQuarantinePage(harness, limit, offset);
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
    /// <param name="spanId">Span identifier.</param>
    /// <returns>Quarantine detail JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetQuarantineDetail(string spanId)
    {
        QuarantineRow? row = store.GetQuarantineItem(spanId);
        if (row is null)
        {
            return null;
        }

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
    /// <param name="harness">Optional harness filter.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="offset">Zero-based row offset.</param>
    /// <param name="page">One-based page number echoed in the payload.</param>
    /// <returns>Problems page JSON element.</returns>
    public JsonElement GetProblemsPage(string? harness, int limit, int offset, int page)
    {
        int total = store.CountProblems(harness);
        IReadOnlyList<StoredProblemRow> rows = store.ListProblems(harness, limit, offset);
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
    /// <param name="rawId">Numeric problem identifier encoded as text.</param>
    /// <returns>Problem detail JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetProblemDetail(string rawId)
    {
        if (!int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        {
            return null;
        }

        StoredProblemRow? row = store.GetProblemById(id);
        if (row is null)
        {
            return null;
        }

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
    /// <returns>Store metadata JSON element.</returns>
    public JsonElement GetMeta() =>
        JsonSerializer.SerializeToElement(new
        {
            schemaVersion = CanonSchema.SchemaVersion,
            detectorVersion = CanonSchema.DetectorVersion,
            storeGeneration = store.ComputeStoreGeneration(),
            recordCount = store.CountRecords(),
            sessionCount = store.ListSessions().Count,
            findingCount = store.ListFindings().Count,
            version,
            apiVersion = ApiContractVersion,
        });

    /// <summary>Returns the store-free build and REST contract version for <c>/api/kyber/version</c>.</summary>
    /// <returns>Version JSON element.</returns>
    public JsonElement GetVersion() =>
        JsonSerializer.SerializeToElement(new
        {
            version,
            apiVersion = ApiContractVersion,
        });

    /// <summary>Returns per-harness field coverage metrics.</summary>
    /// <returns>Coverage JSON element.</returns>
    public JsonElement GetCoverage()
    {
        EnsureProjected();
        IReadOnlyList<HarnessRollupRow> harnessRollups = store.ListHarnessRollups();
        IReadOnlyList<SourceCheckpoint> checkpoints = store.ListSourceCheckpoints();
        object refresh = BuildRefreshCoverage();
        object ingest = BuildIngestCoverage();
        List<object> quarantineByReason = [.. store.ListQuarantine()
            .GroupBy(static q => string.IsNullOrEmpty(q.Reason) ? "unknown" : q.Reason, StringComparer.Ordinal)
            .Select(static g => new { reason = g.Key, count = g.Count() })
            .OrderByDescending(static x => x.count)
            .ThenBy(static x => x.reason, StringComparer.Ordinal)];
        bool receiverEnabled = settings.ReadSettings().ReceiverHosted;
        object receiver = new
        {
            enabled = receiverEnabled,
            endpoint = (string?)null,
            status = "unknown",
            listening = (bool?)null,
            hostedByUs = (bool?)null,
            paused = (bool?)null,
            checkedAt = (string?)null,
            lastAcceptedAt = (string?)null,
            failures5m = (int?)null,
            lastError = (string?)null,
            lastCode = (string?)null,
            lastErrorAt = (string?)null,
            healthy = false,
        };

        return JsonSerializer.SerializeToElement(new
        {
            refresh,
            ingest,
            quarantineByReason,
            checkpoints,
            receiver,
            dimensions = CanonRollupsEngine.CoverageDimensions,
            harnesses = harnessRollups.Select(r => new
            {
                harness = r.Harness,
                fieldCoverage = r.FieldCoverage,
                sampleCount = r.SampleCount,
                measurability = r.Measurability,
            }),
        }, CamelCaseJsonOptions);
    }

    /// <summary>Builds a bounded diagnostic report for <c>/api/kyber/report</c>.</summary>
    /// <param name="harness">Optional harness filter.</param>
    /// <param name="sessionId">Optional session filter.</param>
    /// <param name="runId">Optional run filter.</param>
    /// <param name="limit">Maximum findings included (clamped to 1..200).</param>
    /// <returns>Diagnostic report JSON element.</returns>
    public JsonElement GetReport(string? harness = null, string? sessionId = null, string? runId = null, int limit = 50)
    {
        EnsureProjected();
        IReadOnlyList<SessionSummaryRow> sessions = store.ListSessions(harness);
        if (!string.IsNullOrEmpty(sessionId))
        {
            sessions = [.. sessions.Where(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal))];
        }

        IReadOnlyList<Finding> findings = store.ListFindings(sessionId: sessionId, runId: runId);
        IReadOnlyList<HarnessRollupRow> harnesses = store.ListHarnessRollups();

        int boundedLimit = Math.Clamp(limit, 1, 200);
        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = ApiContractVersion,
            generatedAt = clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            storeGeneration = store.ComputeStoreGeneration(),
            sessionCount = sessions.Count,
            findingCount = findings.Count,
            harnessCount = harnesses.Count,
            harnesses,
            findings = findings.Take(boundedLimit),
        }, CamelCaseJsonOptions);
    }

    /// <summary>Returns timeline span trees for <paramref name="sessionId"/> (or all sessions if omitted).</summary>
    /// <param name="sessionId">Optional session identifier.</param>
    /// <returns>Timeline trees JSON element.</returns>
    public JsonElement GetTimeline(string? sessionId = null)
    {
        EnsureProjected();
        IReadOnlyList<SessionSummaryRow> sessions = string.IsNullOrWhiteSpace(sessionId)
            ? store.ListSessions().Take(25).ToList()
            : store.GetSession(sessionId) is SessionSummaryRow single
                ? [single]
                : [];

        List<object> trees = [];
        foreach (SessionSummaryRow session in sessions)
        {
            IReadOnlyList<CanonicalRecord> records = store.ListRecordsBySessionKey(session.SessionId);
            TimelineNode timeline = CanonContextAnalysis.BuildTimeline(records);
            trees.Add(new { sessionId = session.SessionId, harness = session.Harness, timeline });
        }

        return JsonSerializer.SerializeToElement(new { sessions = trees }, CamelCaseJsonOptions);
    }

    /// <summary>Lists recorded predictions.</summary>
    /// <param name="runId">Optional run filter.</param>
    /// <returns>Serialized predictions array.</returns>
    public JsonElement GetPredictions(string? runId = null)
    {
        if (predictionStore is not null)
        {
            return JsonSerializer.SerializeToElement(
                predictionStore.ListPredictions(runId).Select(ToPredictionRow),
                CamelCaseJsonOptions);
        }

        return JsonSerializer.SerializeToElement(store.ListPredictions(runId), CamelCaseJsonOptions);
    }

    /// <summary>Records a prediction from a finding/run request body.</summary>
    /// <param name="body">Request JSON body.</param>
    /// <param name="created">Created prediction row when valid.</param>
    /// <param name="errorMessage">Validation error message when invalid.</param>
    /// <returns><see langword="true"/> when persisted; otherwise <see langword="false"/>.</returns>
    public bool TryRecordPrediction(JsonElement body, out PredictionRow? created, out string? errorMessage)
    {
        created = null;
        errorMessage = null;
        if (body.ValueKind != JsonValueKind.Object)
        {
            errorMessage = "Prediction body must be a JSON object";
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
            errorMessage = "findingId and runId are required";
            return false;
        }

        long predictedWaste = body.TryGetProperty("predictedWasteTokens", out JsonElement wEl) && wEl.TryGetInt64(out long w)
            ? w
            : 0;
        double confidence = body.TryGetProperty("confidence", out JsonElement cEl) && cEl.TryGetDouble(out double c)
            ? Math.Clamp(c, 0.0, 1.0)
            : 0.5;
        string? comparisonRunId = body.TryGetProperty("comparisonRunId", out JsonElement cmpEl)
            && cmpEl.ValueKind == JsonValueKind.String
            ? cmpEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(comparisonRunId))
        {
            comparisonRunId = null;
        }

        long? observedDeltaTokens = body.TryGetProperty("observedDeltaTokens", out JsonElement deltaEl)
            && deltaEl.ValueKind == JsonValueKind.Number
            && deltaEl.TryGetInt64(out long delta)
            ? delta
            : null;
        double? calibrationScore = body.TryGetProperty("calibrationScore", out JsonElement calEl)
            && calEl.ValueKind == JsonValueKind.Number
            && calEl.TryGetDouble(out double score)
            ? Math.Clamp(score, 0.0, 1.0)
            : null;
        string id = string.Create(CultureInfo.InvariantCulture, $"pred-{Guid.NewGuid():N}");
        string nowIso = clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        string tier = confidence >= 0.8 ? "high" : confidence >= 0.5 ? "medium" : "low";

        created = new PredictionRow(
            Id: id,
            FindingId: findingId,
            RunId: runId,
            PredictedWasteTokens: predictedWaste,
            Confidence: confidence,
            ConfidenceTier: tier,
            ErrorBar: null,
            CreatedAt: nowIso,
            ComparisonRunId: comparisonRunId,
            ObservedDeltaTokens: observedDeltaTokens,
            CalibrationScore: calibrationScore,
            Payload: null);
        if (predictionStore is not null)
        {
            predictionStore.ReplacePrediction(new StoredPrediction(
                PredictionId: id,
                FindingId: findingId,
                RunId: runId,
                PredictedWasteTokens: predictedWaste,
                Confidence: confidence,
                ConfidenceTier: tier,
                CreatedAtUtc: nowIso,
                ComparisonRunId: comparisonRunId,
                ObservedDeltaTokens: observedDeltaTokens,
                CalibrationScore: calibrationScore));
        }
        else
        {
            store.PutPrediction(created);
        }

        return true;
    }

    /// <summary>Maps a typed prediction row back to the API prediction shape.</summary>
    /// <param name="prediction">Typed prediction row.</param>
    /// <returns>API prediction row with absent optionals left unset.</returns>
    private static PredictionRow ToPredictionRow(StoredPrediction prediction) =>
        new(
            Id: prediction.PredictionId,
            FindingId: prediction.FindingId,
            RunId: prediction.RunId,
            PredictedWasteTokens: prediction.PredictedWasteTokens,
            Confidence: prediction.Confidence,
            ConfidenceTier: prediction.ConfidenceTier,
            ErrorBar: null,
            CreatedAt: prediction.CreatedAtUtc,
            ComparisonRunId: prediction.ComparisonRunId,
            ObservedDeltaTokens: prediction.ObservedDeltaTokens,
            CalibrationScore: prediction.CalibrationScore,
            Payload: null);

    /// <summary>Returns prediction calibration summary across recorded predictions.</summary>
    /// <returns>Calibration summary JSON element.</returns>
    public JsonElement GetCalibration()
    {
        IReadOnlyList<PredictionRow> predictions = predictionStore is not null
            ? [.. predictionStore.ListPredictions().Select(ToPredictionRow)]
            : store.ListPredictions();
        List<PredictionRow> scored = [.. predictions.Where(p => p.CalibrationScore.HasValue)];
        double? meanScore = scored.Count > 0 ? Math.Round(scored.Average(p => p.CalibrationScore!.Value), 4) : null;

        return JsonSerializer.SerializeToElement(new
        {
            totalPredictions = predictions.Count,
            evaluatedCount = scored.Count,
            meanCalibrationScore = meanScore,
            predictions,
        }, CamelCaseJsonOptions);
    }

    /// <summary>Returns the current model pricing catalog snapshot.</summary>
    /// <returns>Model catalog snapshot payload.</returns>
    public JsonElement GetModelCatalog() => pricing.GetModelCatalogSnapshot();

    /// <summary>Executes an <c>/api/kyber/analytics/*</c> operation returning the versioned <c>AnalyticsEnvelope</c>.</summary>
    /// <param name="operation">Operation name.</param>
    /// <param name="request">Optional request payload.</param>
    /// <returns>HTTP status code and versioned analytics envelope.</returns>
    public (int StatusCode, JsonElement Envelope) ExecuteAnalytics(string operation, JsonElement? request)
    {
        try
        {
            return ExecuteAnalyticsCore(operation, request);
        }
        catch (Exception ex) when (ex is SqliteExecutionException or CircuitBreakerOpenException or IOException or TimeoutException)
        {
            return BuildAnalyticsErrorResult(
                503,
                "ANALYTICS_STORE_UNAVAILABLE",
                ex.Message,
                "The canonical analytics store is currently unavailable.",
                "Verify the KyberDash canonical store is accessible and retry.");
        }
        catch (Exception ex) when (ex is ArgumentException or DashValidationException)
        {
            return BuildAnalyticsErrorResult(
                400,
                "ANALYTICS_INVALID_ARGUMENT",
                ex.Message,
                "The analytics request contains an invalid argument.",
                "Check the analytics operation name and request payload and retry.");
        }
        catch (Exception ex)
        {
            return BuildAnalyticsErrorResult(
                500,
                "ANALYTICS_INTERNAL",
                ex.Message,
                "An unexpected internal analytics error occurred.",
                "Inspect KyberDash server logs and retry the analytics request.");
        }
    }

    private (int StatusCode, JsonElement Envelope) ExecuteAnalyticsCore(string operation, JsonElement? request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        string trimmedOp = operation.Trim();
        string normalizedOp = AnalyticsOperations.FirstOrDefault(op => string.Equals(op, trimmedOp, StringComparison.OrdinalIgnoreCase))
            ?? trimmedOp;

        string nowIso = FormatTimestamp(clock());
        uint generation = store.ComputeStoreGeneration();
        object freshness = CreateAnalyticsFreshness(nowIso, generation);
        object defaultScope = CreateDefaultAnalyticsScope();

        if (request.HasValue && request.Value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
        {
            return (400, BuildAnalyticsEnvelope(defaultScope, null, new { }, freshness, new { }, "invalid_request", "The request must be a JSON object.", "Send a JSON object matching the operation catalog."));
        }

        if (!AnalyticsOperations.Contains(normalizedOp, StringComparer.Ordinal))
        {
            return (400, BuildAnalyticsEnvelope(defaultScope, null, new { }, freshness, new { }, "unsupported_query", $"Unsupported analytics operation '{operation}'.", "Query /api/kyber/analytics/catalog for supported operations."));
        }

        EnsureProjected();

        if (string.Equals(normalizedOp, "status", StringComparison.Ordinal))
        {
            object statusData = new
            {
                value = new
                {
                    ready = true,
                    storeGeneration = generation,
                    sessionCount = store.ListSessions().Count,
                    recordCount = store.CountRecords(),
                    findingCount = store.ListFindings().Count,
                },
            };
            return (200, BuildAnalyticsEnvelope(defaultScope, statusData, new { }, freshness, new { }, null, null, null));
        }

        if (string.Equals(normalizedOp, "catalog", StringComparison.Ordinal))
        {
            object catalogData = new
            {
                operations = AnalyticsOperations,
                dimensions = new[] { "harness", "model", "repo", "outcome" },
                metrics = new[] { "sessions", "turns", "total_input", "total_output", "cache_read" },
                source = new { harnesses = store.ListObservedHarnesses() },
            };
            return (200, BuildAnalyticsEnvelope(defaultScope, catalogData, new { }, freshness, new { }, null, null, null));
        }

        string? harnessFilter = request.HasValue && request.Value.ValueKind == JsonValueKind.Object
            && request.Value.TryGetProperty("harness", out JsonElement hEl) && hEl.ValueKind == JsonValueKind.String
                ? hEl.GetString()
                : null;
        IReadOnlyList<SessionSummaryRow> sessions = store.ListSessions(harnessFilter);

        if (string.Equals(normalizedOp, "search-sessions", StringComparison.Ordinal))
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

        if (string.Equals(normalizedOp, "inspect", StringComparison.Ordinal))
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

        if (string.Equals(normalizedOp, "findings", StringComparison.Ordinal))
        {
            IReadOnlyList<Finding> findings = store.ListFindings();
            return (200, BuildAnalyticsEnvelope(defaultScope, new { items = findings }, new { }, freshness, new { returnedCount = findings.Count, nextCursor = (string?)null }, null, null, null));
        }

        if (string.Equals(normalizedOp, "compare", StringComparison.Ordinal))
        {
            return (200, BuildAnalyticsEnvelope(defaultScope, new { deltas = rollups.CompareHarnesses() }, new { }, freshness, new { }, null, null, null));
        }

        if (string.Equals(normalizedOp, "outcomes", StringComparison.Ordinal))
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

    private (int StatusCode, JsonElement Envelope) BuildAnalyticsErrorResult(
        int statusCode,
        string errorCode,
        string? rawMessage,
        string fallbackMessage,
        string hint)
    {
        DateTimeOffset timestamp;
        try
        {
            timestamp = clock();
        }
        catch (Exception)
        {
            timestamp = DateTimeOffset.UtcNow;
        }

        object freshness = CreateAnalyticsFreshness(FormatTimestamp(timestamp), 0u);
        object defaultScope = CreateDefaultAnalyticsScope();
        string message = string.IsNullOrWhiteSpace(rawMessage) ? fallbackMessage : rawMessage;

        return (statusCode, BuildAnalyticsEnvelope(defaultScope, null, new { }, freshness, new { }, errorCode, message, hint));
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private object CreateAnalyticsFreshness(string generatedAt, uint generation) =>
        new
        {
            generatedAt,
            instanceId,
            projectionGeneration = generation.ToString(CultureInfo.InvariantCulture),
            storeGeneration = generation,
        };

    private static object CreateDefaultAnalyticsScope() =>
        new
        {
            window = new { from = (string?)null, to = (string?)null },
            harnesses = Array.Empty<string>(),
        };

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
        }, CamelCaseJsonOptions);
    }

    private static JsonElement? ParseSummary(SessionSummaryRow row)
    {
        if (!string.IsNullOrEmpty(row.SummaryJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(row.SummaryJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return doc.RootElement.Clone();
                }
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

    private static JsonElement? TryGetOptionalProperty(JsonElement? obj, string name)
    {
        if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!obj.Value.TryGetProperty(name, out JsonElement el)
            || el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return el.Clone();
    }

    private static string? TryGetString(JsonElement? obj, string name)
    {
        if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return obj.Value.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static double? TryGetNullableDouble(JsonElement? obj, string name)
    {
        if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return obj.Value.TryGetProperty(name, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetDouble(out double val)
            && double.IsFinite(val)
                ? val
                : null;
    }

    private HarnessAggregationContext BuildHarnessAggregationContext()
    {
        Dictionary<string, int> sessionCounts = new(StringComparer.Ordinal);
        Dictionary<string, string> sessionHarnessById = new(StringComparer.Ordinal);
        foreach (SessionSummaryRow session in store.ListSessions())
        {
            string norm = CanonPricing.NormalizeHarnessName(session.Harness);
            sessionCounts[norm] = sessionCounts.GetValueOrDefault(norm) + 1;
            sessionHarnessById[session.SessionId] = norm;
        }

        Dictionary<string, int> runCounts = new(StringComparer.Ordinal);
        Dictionary<string, string> runHarnessById = new(StringComparer.Ordinal);
        foreach (RunRow run in store.ListRuns())
        {
            string norm = CanonPricing.NormalizeHarnessName(run.Harness);
            runCounts[norm] = runCounts.GetValueOrDefault(norm) + 1;
            runHarnessById[run.RunId] = norm;
        }

        Dictionary<string, int> findingCounts = new(StringComparer.Ordinal);
        foreach (Finding finding in store.ListFindings())
        {
            string? resolved = finding.SessionId is not null && sessionHarnessById.TryGetValue(finding.SessionId, out string? sh)
                ? sh
                : finding.RunId is not null && runHarnessById.TryGetValue(finding.RunId, out string? rh)
                    ? rh
                    : null;
            if (resolved is not null)
            {
                findingCounts[resolved] = findingCounts.GetValueOrDefault(resolved) + 1;
            }
        }

        Dictionary<string, List<SourceCheckpoint>> checkpointsByHarness = new(StringComparer.Ordinal);
        foreach (SourceCheckpoint cp in store.ListSourceCheckpoints())
        {
            string norm = CanonPricing.NormalizeHarnessName(cp.HarnessId);
            if (!checkpointsByHarness.TryGetValue(norm, out List<SourceCheckpoint>? list))
            {
                list = [];
                checkpointsByHarness[norm] = list;
            }

            list.Add(cp);
        }

        return new HarnessAggregationContext(sessionCounts, runCounts, findingCounts, checkpointsByHarness);
    }

    private static object BuildHarnessSummaryDto(HarnessRollupRow row, HarnessAggregationContext context)
    {
        string norm = CanonPricing.NormalizeHarnessName(row.Harness);
        int sessionCount = context.SessionCounts.GetValueOrDefault(norm, 0);
        int runCount = context.RunCounts.GetValueOrDefault(norm, 0);
        int findingCount = context.FindingCounts.GetValueOrDefault(norm, 0);
        IReadOnlyList<SourceCheckpoint> cps = context.CheckpointsByHarness.TryGetValue(norm, out List<SourceCheckpoint>? matchedCps)
            ? matchedCps
            : [];

        return new
        {
            harness = row.Harness,
            family = CanonPricing.HarnessFamily(row.Harness),
            sampleCount = row.SampleCount,
            sessionCount,
            runCount,
            findingCount,
            contextPressureMedian = row.ContextPressureMedian,
            contextPressureP95 = row.ContextPressureP95,
            cacheHitRate = row.CacheHitRate,
            toolYield = row.ToolYield,
            delegationOverhead = row.DelegationOverhead,
            fieldCoverage = row.FieldCoverage,
            measurability = row.Measurability,
            noDataReason = row.SampleCount == 0 ? ExtractNoDataReason(row) : null,
            checkpointSummary = BuildCheckpointSummary(cps),
            scorecard = CanonRollupsEngine.BuildHarnessScorecard(row),
            payload = row.Payload,
        };
    }

    private static string? ExtractNoDataReason(HarnessRollupRow row)
    {
        if (row.Payload is { ValueKind: JsonValueKind.Object } payload)
        {
            if (payload.TryGetProperty("noDataReason", out JsonElement ndEl)
                && ndEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(ndEl.GetString()))
            {
                return ndEl.GetString();
            }

            if (payload.TryGetProperty("reason", out JsonElement rEl)
                && rEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(rEl.GetString()))
            {
                return rEl.GetString();
            }
        }

        return null;
    }

    private static object BuildCheckpointSummary(IReadOnlyList<SourceCheckpoint> checkpoints)
    {
        int ok = 0;
        int partial = 0;
        int failed = 0;
        int unavailable = 0;
        foreach (SourceCheckpoint cp in checkpoints)
        {
            switch (cp.LastStatus)
            {
                case "ok":
                    ok++;
                    break;
                case "partial":
                    partial++;
                    break;
                case "failed":
                    failed++;
                    break;
                case "unavailable":
                    unavailable++;
                    break;
            }
        }

        return new { ok, partial, failed, unavailable };
    }

    private Dictionary<string, int> CountFindingsByRun()
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (Finding finding in store.ListFindings())
        {
            if (!string.IsNullOrEmpty(finding.RunId))
            {
                counts[finding.RunId] = counts.GetValueOrDefault(finding.RunId) + 1;
            }
        }

        return counts;
    }

    private static RunSessionFigures SummarizeRunSessions(
        IReadOnlyList<ExecutionRow> executions,
        Dictionary<string, SessionSummaryRow> sessionsById)
    {
        HashSet<string> seenSessions = new(StringComparer.Ordinal);
        int turnCount = 0;
        bool hasTurns = false;
        long totalInput = 0;
        bool hasInput = false;
        long totalOutput = 0;
        bool hasOutput = false;
        double costUsd = 0;
        bool hasCost = false;

        foreach (ExecutionRow exec in executions)
        {
            if (string.IsNullOrEmpty(exec.SessionId) || !seenSessions.Add(exec.SessionId))
            {
                continue;
            }

            if (!sessionsById.TryGetValue(exec.SessionId, out SessionSummaryRow? session))
            {
                continue;
            }

            SessionSummaryFigures sFig = ExtractSessionSummaryFigures(session);
            if (sFig.TurnCount.HasValue)
            {
                turnCount += sFig.TurnCount.Value;
                hasTurns = true;
            }

            if (sFig.TotalInput.HasValue)
            {
                totalInput += sFig.TotalInput.Value;
                hasInput = true;
            }

            if (sFig.TotalOutput.HasValue)
            {
                totalOutput += sFig.TotalOutput.Value;
                hasOutput = true;
            }

            if (sFig.CostUsd.HasValue)
            {
                costUsd += sFig.CostUsd.Value;
                hasCost = true;
            }
        }

        return new RunSessionFigures(
            hasTurns ? turnCount : null,
            hasInput ? totalInput : null,
            hasOutput ? totalOutput : null,
            hasCost ? Math.Round(costUsd, 6) : null);
    }

    private static SessionSummaryFigures ExtractSessionSummaryFigures(SessionSummaryRow session)
    {
        JsonElement? summary = ParseSummary(session);
        int? turnCount = TryGetNullableInt(summary, "turn_count");
        long? totalInput = TryGetNullableLong(summary, "total_input");
        long? totalOutput = TryGetNullableLong(summary, "total_output");
        double? costUsd = ExtractUsdCost(ExtractCost(summary));
        return new SessionSummaryFigures(turnCount, totalInput, totalOutput, costUsd);
    }

    private static List<object> BuildExecutionTree(
        IReadOnlyList<ExecutionRow> executions,
        Dictionary<string, SessionSummaryRow> sessionsById)
    {
        Dictionary<string, ExecutionTreeNode> nodesById = new(StringComparer.Ordinal);
        List<ExecutionTreeNode> ordered = new(executions.Count);
        foreach (ExecutionRow exec in executions)
        {
            SessionSummaryFigures? sFig = !string.IsNullOrEmpty(exec.SessionId)
                && sessionsById.TryGetValue(exec.SessionId, out SessionSummaryRow? s)
                    ? ExtractSessionSummaryFigures(s)
                    : null;
            ExecutionTreeNode node = new(exec, sFig?.TurnCount, sFig?.CostUsd, []);
            nodesById[exec.ExecutionId] = node;
            ordered.Add(node);
        }

        HashSet<string> attached = new(StringComparer.Ordinal);
        foreach (ExecutionTreeNode node in ordered)
        {
            string? parentId = node.Row.ParentExecutionId;
            if (string.IsNullOrEmpty(parentId)
                || string.Equals(parentId, node.Row.ExecutionId, StringComparison.Ordinal)
                || !nodesById.TryGetValue(parentId, out ExecutionTreeNode? parent))
            {
                continue;
            }

            if (IsExecutionAncestorOrSelf(node.Row.ExecutionId, parentId, nodesById))
            {
                continue;
            }

            parent.Children.Add(node);
            attached.Add(node.Row.ExecutionId);
        }

        List<ExecutionTreeNode> roots = [.. ordered.Where(n => !attached.Contains(n.Row.ExecutionId))];
        return [.. roots.Select(ToExecutionTreeDto)];
    }

    private static bool IsExecutionAncestorOrSelf(
        string candidateId,
        string parentId,
        Dictionary<string, ExecutionTreeNode> nodesById)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? cursor = parentId;
        while (!string.IsNullOrEmpty(cursor) && seen.Add(cursor))
        {
            if (string.Equals(cursor, candidateId, StringComparison.Ordinal))
            {
                return true;
            }

            cursor = nodesById.TryGetValue(cursor, out ExecutionTreeNode? n) ? n.Row.ParentExecutionId : null;
        }

        return false;
    }

    private static object ToExecutionTreeDto(ExecutionTreeNode node)
    {
        return new
        {
            executionId = node.Row.ExecutionId,
            runId = node.Row.RunId,
            sessionId = node.Row.SessionId,
            parentExecutionId = node.Row.ParentExecutionId,
            harness = node.Row.Harness,
            agentName = node.Row.AgentName,
            isRoot = node.Row.IsRoot,
            started = node.Row.Started,
            ended = node.Row.Ended,
            parentLinkage = node.Row.ParentLinkage,
            turnCount = node.TurnCount,
            costUsd = node.CostUsd,
            payload = node.Row.Payload,
            children = node.Children.Select(ToExecutionTreeDto).ToList(),
        };
    }

    private List<object> ExtractRunTurns(IReadOnlyList<ExecutionRow> executions)
    {
        HashSet<string> seenSessions = new(StringComparer.Ordinal);
        List<object> turns = [];
        foreach (ExecutionRow exec in executions)
        {
            if (string.IsNullOrEmpty(exec.SessionId) || !seenSessions.Add(exec.SessionId))
            {
                continue;
            }

            IReadOnlyList<JsonElement> payloads = store.ListSessionTurnPayloads(exec.SessionId);
            for (int i = 0; i < payloads.Count; i++)
            {
                JsonElement turn = payloads[i];
                int turnIndex = TryGetNullableInt(turn, "turnIndex") ?? TryGetNullableInt(turn, "index") ?? i;
                long? inputTokens = TryGetNullableLong(turn, "inputTokens") ?? TryGetNullableLong(turn, "input");
                long? outputTokens = TryGetNullableLong(turn, "outputTokens") ?? TryGetNullableLong(turn, "output");
                long? tokens = TryGetNullableLong(turn, "tokens") ?? (inputTokens.HasValue && outputTokens.HasValue ? inputTokens.Value + outputTokens.Value : null);
                double? costUsd = TryGetNullableDouble(turn, "costUsd");
                if (!costUsd.HasValue && turn.ValueKind == JsonValueKind.Object && turn.TryGetProperty("cost", out JsonElement costEl))
                {
                    costUsd = ExtractUsdCost(costEl);
                }

                turns.Add(new
                {
                    turnIndex,
                    executionId = exec.ExecutionId,
                    sessionId = exec.SessionId,
                    model = TryGetString(turn, "model"),
                    tokens,
                    inputTokens,
                    outputTokens,
                    costUsd,
                    contextPressure = TryGetNullableDouble(turn, "contextPressure") ?? TryGetNullableDouble(turn, "pressure"),
                    cacheHitRatio = TryGetNullableDouble(turn, "cacheHitRatio") ?? TryGetNullableDouble(turn, "cache_hit_ratio"),
                    timestamp = TryGetString(turn, "timestamp"),
                });
            }
        }

        return turns;
    }

    private object BuildRefreshCoverage()
    {
        IReadOnlyList<RefreshRunRow> runs = store.ListRefreshRuns(50);
        RefreshRunRow? success = runs.Where(static r => string.Equals(r.Status, "success", StringComparison.Ordinal))
            .OrderByDescending(static r => r.StartedAt, StringComparer.Ordinal)
            .FirstOrDefault();
        RefreshRunRow? failure = runs.Where(static r => string.Equals(r.Status, "failure", StringComparison.Ordinal))
            .OrderByDescending(static r => r.StartedAt, StringComparer.Ordinal)
            .FirstOrDefault();
        RefreshRunRow? running = runs.Where(static r => string.Equals(r.Status, "running", StringComparison.Ordinal))
            .OrderByDescending(static r => r.StartedAt, StringComparer.Ordinal)
            .FirstOrDefault();

        string? lastCleanAt = store.GetMetadata("last_clean_at");
        bool superseded = false;
        if (success is not null
            && !string.IsNullOrEmpty(lastCleanAt)
            && DateTimeOffset.TryParse(success.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset successStart)
            && DateTimeOffset.TryParse(lastCleanAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset cleanStamp))
        {
            superseded = successStart <= cleanStamp;
        }

        RefreshRunRow? windowRun = superseded ? null : success;
        int? historyWeeks = windowRun?.HistoryWeeks;
        string? coveredThrough = null;
        string? coveredFrom = null;
        if (windowRun is not null
            && historyWeeks is int weeks and > 0
            && DateTimeOffset.TryParse(windowRun.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset throughDto))
        {
            coveredThrough = windowRun.StartedAt;
            coveredFrom = (throughDto - TimeSpan.FromDays(7 * weeks))
                .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        return new
        {
            lastSuccessAt = success is null ? null : (success.CompletedAt ?? success.StartedAt),
            lastFailure = failure is null
                ? null
                : new { at = failure.CompletedAt ?? failure.StartedAt, summary = failure.Summary ?? "refresh failed" },
            inProgress = running is null
                ? null
                : new { pid = running.Pid, since = running.StartedAt },
            historyWeeks,
            coveredFrom,
            coveredThrough,
        };
    }

    private object BuildIngestCoverage()
    {
        IReadOnlyDictionary<string, int> recordCounts = store.CountRecordsBySource();
        Dictionary<string, (int Total, string? LastAt)> logSums = new(StringComparer.Ordinal);
        string? lastReceivedAt = null;
        foreach (IngestLogEntry entry in store.GetIngestLog())
        {
            logSums.TryGetValue(entry.Source, out (int Total, string? LastAt) existing);
            string? newer = existing.LastAt is null || string.Compare(entry.Timestamp, existing.LastAt, StringComparison.Ordinal) > 0
                ? entry.Timestamp
                : existing.LastAt;
            logSums[entry.Source] = (existing.Total + entry.Count, newer);
            if (lastReceivedAt is null || string.Compare(entry.Timestamp, lastReceivedAt, StringComparison.Ordinal) > 0)
            {
                lastReceivedAt = entry.Timestamp;
            }
        }

        if (recordCounts.Count == 0 && logSums.Count == 0)
        {
            return new
            {
                status = "unknown",
                reason = "no receiver activity recorded",
                sources = Array.Empty<object>(),
                lastReceivedAt = (string?)null,
            };
        }

        List<string> keys = [.. recordCounts.Keys.Concat(logSums.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        List<object> sources = new(keys.Count);
        foreach (string key in keys)
        {
            (string display, string kind) = FormatSourceDisplay(key);
            logSums.TryGetValue(key, out (int Total, string? LastAt) log);
            sources.Add(new
            {
                source = key,
                display,
                kind,
                recordCount = recordCounts.GetValueOrDefault(key),
                ingestedCount = log.Total,
                lastReceivedAt = log.LastAt,
            });
        }

        return new
        {
            status = "known",
            sources,
            lastReceivedAt,
        };
    }

    private static (string Display, string Kind) FormatSourceDisplay(string source)
    {
        const string fileSourcePrefix = "codeburn/";
        if (source.StartsWith(fileSourcePrefix, StringComparison.Ordinal))
        {
            return (source[fileSourcePrefix.Length..], "local-file");
        }

        if (string.Equals(source.Trim(), "unattributed", StringComparison.OrdinalIgnoreCase))
        {
            return ("unattributed (legacy)", "legacy-unattributed");
        }

        return (source, "otlp");
    }

    private static long? TryGetNullableLong(JsonElement? obj, string name)
    {
        if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return obj.Value.TryGetProperty(name, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt64(out long val)
                ? val
                : null;
    }

    private static int? TryGetNullableInt(JsonElement? obj, string name)
    {
        if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

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
            {
                items.Add(s);
            }
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
            {
                return true;
            }

            cursor = byId.TryGetValue(cursor, out SessionNode? node) ? node.ParentSession : null;
        }

        return false;
    }

    private static object ToSessionDto(SessionNode node)
    {
        return new
        {
            session_id = node.SessionId,
            sessionId = node.SessionId,
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
        {
            return text;
        }

        int end = maxBytes;
        while (end > 0 && (utf8[end] & 0b1100_0000) == 0b1000_0000)
        {
            end--;
        }

        return Encoding.UTF8.GetString(utf8, 0, end);
    }

    private record HarnessAggregationContext(
        Dictionary<string, int> SessionCounts,
        Dictionary<string, int> RunCounts,
        Dictionary<string, int> FindingCounts,
        Dictionary<string, List<SourceCheckpoint>> CheckpointsByHarness);

    private record RunSessionFigures(
        int? TurnCount,
        long? TotalInput,
        long? TotalOutput,
        double? CostUsd);

    private record SessionSummaryFigures(
        int? TurnCount,
        long? TotalInput,
        long? TotalOutput,
        double? CostUsd);

    private record ExecutionTreeNode(
        ExecutionRow Row,
        int? TurnCount,
        double? CostUsd,
        List<ExecutionTreeNode> Children);

    private record SessionNode(
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
        JsonElement? TotalInput,
        JsonElement? TotalOutput,
        double? CostUsd,
        JsonElement Cost,
        IReadOnlyList<string> Models,
        int Problems,
        List<SessionNode> Subagents);
}
