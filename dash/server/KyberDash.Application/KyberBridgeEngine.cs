using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Bounded read and analytics query bridge over the relational KyberDash store
/// (<see cref="IRelationalStoreReader"/>, <see cref="IProjectionStore"/>, and
/// <see cref="IOperationalStore"/>) serving the KyberDash HTTP and MCP contracts.
/// </summary>
/// <remarks>
/// <para>
/// Every route answers from typed store rows: the engine reads only the relational store
/// contracts and parses no persisted store JSON. Session figures come from
/// <see cref="IRelationalStoreReader.GetSessionAggregate"/>,
/// <see cref="IRelationalStoreReader.ListSessionAggregates"/>, and
/// <see cref="IRelationalStoreReader.SessionTurnSummaries"/> priced at read time through
/// <see cref="IPricingRepository.LoadPricingTables"/> as the <see cref="IModelPricingLookup"/>;
/// run, harness, finding, and prediction reads come from <see cref="IProjectionStore"/>; settings,
/// quarantine, problems, and audit reads come from <see cref="IOperationalStore"/>.
/// </para>
/// <para>
/// A session whose read fails is isolated: its answer carries
/// <c>projection_status = "PROJECTION_FAILED"</c> while the remaining sessions answer normally,
/// and every isolated failure is surfaced through <see cref="GetCoverage"/>. Harness-reported and
/// read-time published costs are reported as separate <c>cost_reported</c> and <c>cost</c> blocks
/// with a structured <c>cost_basis_mismatch</c> verdict, and are never blended into one figure.
/// </para>
/// </remarks>
public class KyberBridgeEngine
{
    /// <summary>Default byte budget applied to turn and session content responses (32 KiB).</summary>
    public const int ContentResponseBudget = 32_768;

    /// <summary>REST contract schema version expected by desktop tray and web clients.</summary>
    public const int ApiContractVersion = 1;

    /// <summary>Default KyberDash engine version reported by <c>/api/kyber/version</c> and <c>/api/kyber/meta</c>.</summary>
    public const string DefaultVersion = "0.21.0";

    /// <summary>Session marker answered when a single session's projection read fails.</summary>
    private const string ProjectionFailedStatus = "PROJECTION_FAILED";

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

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

    private readonly IRelationalStoreReader reader;
    private readonly IProjectionStore projections;
    private readonly IOperationalStore operationalStore;
    private readonly IPricingRepository pricing;
    private readonly CanonSessionProjectorEngine projector;
    private readonly CanonRollupsEngine rollups;
    private readonly Func<DateTimeOffset> clock;
    private readonly string version;
    private readonly string instanceId;
    private readonly Lock projectionFailureLock = new();
    private readonly Dictionary<string, string> projectionFailures = new(StringComparer.Ordinal);
    private string? lastProjectionPassFault;

    /// <summary>
    /// Initializes a new <see cref="KyberBridgeEngine"/>.
    /// </summary>
    /// <param name="reader">Typed relational store reader answering session aggregates and records.</param>
    /// <param name="projections">Projection store holding sessions, runs, harness rollups, findings, and predictions.</param>
    /// <param name="operationalStore">Operational store holding settings, quarantine, problems, and audit rows.</param>
    /// <param name="pricing">Model rate-table repository used as the read-time pricing lookup.</param>
    /// <param name="projector">Session projection engine.</param>
    /// <param name="rollups">Run and harness rollup engine.</param>
    /// <param name="version">Engine build version string.</param>
    /// <param name="clock">Optional UTC clock override for deterministic timestamps.</param>
    /// <param name="instanceId">Optional process instance identifier for analytics freshness metadata.</param>
    public KyberBridgeEngine(
        IRelationalStoreReader reader,
        IProjectionStore projections,
        IOperationalStore operationalStore,
        IPricingRepository pricing,
        CanonSessionProjectorEngine projector,
        CanonRollupsEngine rollups,
        string version = DefaultVersion,
        Func<DateTimeOffset>? clock = null,
        string? instanceId = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(operationalStore);
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(rollups);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        this.reader = reader;
        this.projections = projections;
        this.operationalStore = operationalStore;
        this.pricing = pricing;
        this.projector = projector;
        this.rollups = rollups;
        this.version = version;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.instanceId = string.IsNullOrWhiteSpace(instanceId)
            ? string.Create(CultureInfo.InvariantCulture, $"kyberdash-{Environment.ProcessId}")
            : instanceId;
    }

    /// <summary>Ensures dirty sessions, runs, executions, and harness rollups are projected up to date.</summary>
    /// <remarks>
    /// A transient store fault in the projection pass is isolated instead of failing every read:
    /// sessions projected before the fault still answer, and the fault is surfaced through
    /// <see cref="GetCoverage"/>. Per-session read faults are isolated the same way, answering
    /// <c>PROJECTION_FAILED</c> for the broken session only.
    /// </remarks>
    public void EnsureProjected()
    {
        bool builtOrPruned = false;
        if (!projector.IsProjectionCurrent())
        {
            try
            {
                ProjectionPassReport report = projector.ProjectIncremental();
                builtOrPruned = report.Built > 0 || report.Pruned > 0;
            }
            catch (Exception ex) when (IsTransientStoreFault(ex))
            {
                lock (projectionFailureLock)
                {
                    lastProjectionPassFault = ex.Message;
                }
            }
        }

        if (builtOrPruned
            || (projections.ListSessionProjections().Count > 0 && projections.ListHarnessRollups().Count == 0))
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
        IModelPricingLookup lookup = pricing.LoadPricingTables();
        string? normalizedHarness = NormalizeHarnessFilter(harness);
        IReadOnlyList<StoredSessionProjection> rows = projections.ListSessionProjections(normalizedHarness);
        Dictionary<string, SessionNode> byId = new(StringComparer.Ordinal);
        List<SessionNode> orderedNodes = new(rows.Count);

        foreach (StoredSessionProjection row in rows)
        {
            CollectedSession? session = reader.GetSession(row.SessionId);
            (SessionAggregateView? aggregate, _, string? projectionStatus) = ReadSessionFigures(row.SessionId, lookup);
            string harnessId = ResolveHarness(aggregate, session);
            string label = !string.IsNullOrWhiteSpace(row.Label)
                ? row.Label
                : CanonPricing.FormatSessionTitle(new SessionTitleInput(row.SessionId, row.AgentName, harnessId, row.Label));

            SessionNode node = new(
                SessionId: row.SessionId,
                Harness: harnessId,
                Label: label,
                IsSubagent: row.IsSubagent,
                ParentSession: row.ParentSessionId,
                AgentName: row.AgentName,
                Repo: session?.Workspace?.Repository,
                Branch: session?.Workspace?.Branch,
                Started: row.StartedAtUtc,
                Ended: row.EndedAtUtc,
                TurnCount: aggregate?.TurnCount,
                RequestCount: aggregate?.RequestCount,
                TotalInput: aggregate?.TotalInput,
                TotalOutput: aggregate?.TotalOutput,
                CostUsd: ExtractUsdCost(aggregate?.PublishedCost),
                Cost: aggregate?.PublishedCost,
                CostReported: aggregate?.ReportedCost,
                CostBasisMismatch: aggregate?.CostBasisMismatch,
                Models: aggregate?.Models ?? [],
                Problems: aggregate?.Problems.Count ?? 0,
                ProjectionStatus: projectionStatus,
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
        StoredSessionProjection? projection = projections.GetSessionProjection(sessionId);
        if (projection is null)
        {
            return null;
        }

        CollectedSession? session = reader.GetSession(sessionId);
        IModelPricingLookup lookup = pricing.LoadPricingTables();
        (SessionAggregateView? aggregate, IReadOnlyList<ReadTimeTurnSummary> turns, string? projectionStatus) =
            ReadSessionFigures(sessionId, lookup);

        Dictionary<string, object?> root = new(StringComparer.Ordinal)
        {
            ["session_id"] = projection.SessionId,
            ["sessionId"] = projection.SessionId,
            ["harness"] = ResolveHarness(aggregate, session),
            ["label"] = projection.Label,
            ["is_subagent"] = projection.IsSubagent,
            ["parent_session"] = projection.ParentSessionId,
            ["agent_name"] = projection.AgentName,
            ["repo"] = session?.Workspace?.Repository,
            ["branch"] = session?.Workspace?.Branch,
            ["started"] = projection.StartedAtUtc,
            ["ended"] = projection.EndedAtUtc,
            ["problems_count"] = aggregate?.Problems.Count ?? 0,
            ["context_limit_source"] = ResolveContextLimitSource(projection),
            ["summary"] = aggregate is null ? null : BuildSessionSummary(aggregate),
            ["problems"] = aggregate?.Problems ?? Array.Empty<Problem>(),
            ["turns"] = turns.Select(ToTurnDto).ToList(),
            ["tools"] = aggregate is null ? Array.Empty<object>() : ToolRows(aggregate),
            ["timeline"] = TimelineChildren(projection.SessionId),
            ["requests"] = aggregate is null ? Array.Empty<object>() : RequestRows(aggregate),
        };

        if (projectionStatus is not null)
        {
            root["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(root, CamelCaseJsonOptions);
    }

    /// <summary>Returns the bounded session summary (without full turn bodies), or <see langword="null"/> if missing.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <returns>Summary JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetSessionSummary(string sessionId)
    {
        EnsureProjected();
        StoredSessionProjection? projection = projections.GetSessionProjection(sessionId);
        if (projection is null)
        {
            return null;
        }

        CollectedSession? session = reader.GetSession(sessionId);
        IModelPricingLookup lookup = pricing.LoadPricingTables();
        (SessionAggregateView? aggregate, _, string? projectionStatus) = ReadSessionFigures(sessionId, lookup);

        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["session_id"] = projection.SessionId,
            ["harness"] = ResolveHarness(aggregate, session),
            ["label"] = projection.Label,
            ["is_subagent"] = projection.IsSubagent,
            ["parent_session"] = projection.ParentSessionId,
            ["agent_name"] = projection.AgentName,
            ["repo"] = session?.Workspace?.Repository,
            ["branch"] = session?.Workspace?.Branch,
            ["started"] = projection.StartedAtUtc,
            ["ended"] = projection.EndedAtUtc,
            ["problems"] = aggregate?.Problems.Count ?? 0,
            ["context_limit_source"] = ResolveContextLimitSource(projection),
            ["summary"] = aggregate is null ? null : BuildSessionSummary(aggregate),
        };

        if (projectionStatus is not null)
        {
            payload["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>Returns turn-level figures for <paramref name="turnIndex"/> in <paramref name="sessionId"/>, or <see langword="null"/>.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="turnIndex">Zero-based turn index.</param>
    /// <returns>Turn figures JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetTurnFigures(string sessionId, int turnIndex)
    {
        EnsureProjected();
        IReadOnlyList<ReadTimeTurnSummary> turns = TrySessionTurnSummaries(sessionId);
        if (turnIndex < 0 || turnIndex >= turns.Count)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(new
        {
            sessionId,
            turnIndex,
            turn = ToTurnDto(turns[turnIndex]),
        }, CamelCaseJsonOptions);
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
        IReadOnlyList<ReadTimeTurnSummary> turns = TrySessionTurnSummaries(sessionId);
        if (turnIndex < 0 || turnIndex >= turns.Count)
        {
            return null;
        }

        Dictionary<string, object?> turn = ToTurnDto(turns[turnIndex]);
        string serialized = JsonSerializer.Serialize(turn, CamelCaseJsonOptions);
        int totalBytes = Encoding.UTF8.GetByteCount(serialized);
        int effectiveBudget = budget > 0 ? budget : ContentResponseBudget;
        bool truncated = !full && totalBytes > effectiveBudget;
        string slice = truncated ? TruncateUtf8(serialized, effectiveBudget) : serialized;

        return JsonSerializer.SerializeToElement(new
        {
            sessionId,
            turnIndex,
            content = truncated ? slice : null,
            turn = truncated ? null : turn,
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
        StoredSessionProjection? projection = projections.GetSessionProjection(sessionId);
        if (projection is null)
        {
            return null;
        }

        List<CanonicalRecord> records = [.. reader.Records(sessionKey: sessionId).Select(CanonRecordHydration.Hydrate)];
        records.Sort(CompareRecords);
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
    /// <remarks>
    /// The relational store keeps no raw attribute bag; <c>attributes</c> carries the typed
    /// attributes a hydrated record still names (declared context window, model, agent).
    /// </remarks>
    public JsonElement? GetSpanAttributes(string spanId)
    {
        CollectedRecord? record = reader.GetRecord(spanId);
        if (record is null)
        {
            return null;
        }

        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(record);
        AttachCapturedPrompt(record, hydrated);
        return JsonSerializer.SerializeToElement(new
        {
            spanId = hydrated.SpanId,
            traceId = hydrated.TraceId,
            sessionId = hydrated.SessionId,
            harness = hydrated.Harness,
            source = hydrated.Source,
            name = hydrated.Name,
            op = hydrated.Op,
            kind = hydrated.Kind,
            timestamp = hydrated.Timestamp,
            tokens = hydrated.Tokens,
            cost = hydrated.Cost,
            attributes = hydrated.Raw ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
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
        IReadOnlyList<StoredFinding> all = projections.ListFindings(runId: runId, sessionId: sessionId);
        IEnumerable<StoredFinding> scoped = all;
        if (!string.IsNullOrWhiteSpace(harness))
        {
            string normHarness = CanonPricing.NormalizeHarnessName(harness.Trim());
            HashSet<string> matchingSessionIds = [.. reader.Sessions()
                .Where(s => string.Equals(CanonPricing.NormalizeHarnessName(s.HarnessId), normHarness, StringComparison.Ordinal))
                .Select(s => s.SessionKey)];
            HashSet<string> matchingRunIds = [.. projections.ListRuns()
                .Where(r => string.Equals(CanonPricing.NormalizeHarnessName(r.HarnessId), normHarness, StringComparison.Ordinal))
                .Select(r => r.RunId)];
            scoped = scoped.Where(f =>
                (f.SessionId is not null && matchingSessionIds.Contains(f.SessionId))
                || (f.RunId is not null && matchingRunIds.Contains(f.RunId)));
        }

        List<StoredFinding> baseList = [.. scoped];
        Dictionary<string, int> detectorCounts = new(StringComparer.Ordinal);
        foreach (string detectorId in CanonFindings.DetectorIds)
        {
            detectorCounts[detectorId] = 0;
        }

        foreach (StoredFinding finding in baseList)
        {
            detectorCounts.TryGetValue(finding.DetectorId, out int current);
            detectorCounts[finding.DetectorId] = current + 1;
        }

        IEnumerable<StoredFinding> filtered = baseList;
        if (!string.IsNullOrWhiteSpace(detector))
        {
            filtered = filtered.Where(f => string.Equals(f.DetectorId, detector.Trim(), StringComparison.Ordinal));
        }

        List<StoredFinding> list = [.. filtered];
        int total = list.Count;
        int safeLimit = Math.Clamp(limit, 1, 500);
        int safeOffset = Math.Max(0, offset);
        List<Finding> pageItems = [.. list.Skip(safeOffset).Take(safeLimit).Select(ToFindingRow)];

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
        StoredFinding? stored = projections.GetFinding(findingId);
        return stored is null ? null : ToFindingRow(stored);
    }

    /// <summary>Returns all harness efficiency rollups (projecting runs and rollups if missing).</summary>
    /// <returns>Harnesses payload JSON element.</returns>
    public JsonElement GetHarnesses()
    {
        EnsureProjected();
        List<HarnessRollupRow> harnessRollups = [.. projections.ListHarnessRollups().Select(ToHarnessRollupRow)];
        if (harnessRollups.Count == 0)
        {
            harnessRollups = [.. rollups.ProjectRunsAndHarnesses()];
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
        StoredHarnessRollup? stored = projections.GetHarnessRollup(norm);
        if (stored is null)
        {
            return null;
        }

        HarnessAggregationContext context = BuildHarnessAggregationContext();
        return JsonSerializer.SerializeToElement(BuildHarnessSummaryDto(ToHarnessRollupRow(stored), context), CamelCaseJsonOptions);
    }

    /// <summary>Lists all projected runs.</summary>
    /// <param name="harness">Optional harness filter.</param>
    /// <returns>Runs payload JSON element.</returns>
    public JsonElement GetRuns(string? harness = null)
    {
        EnsureProjected();
        IModelPricingLookup lookup = pricing.LoadPricingTables();
        string? normalizedHarness = NormalizeHarnessFilter(harness);
        IReadOnlyList<StoredRun> runRows = projections.ListRuns(normalizedHarness);
        Dictionary<string, string> runBySession = BuildRunBySessionIndex(runRows);
        Dictionary<string, int> findingCounts = CountFindingsByRun(runBySession);
        Dictionary<string, SessionAggregateView> aggregatesById = BuildAggregateIndex(lookup);

        List<object> mappedRuns = new(runRows.Count);
        foreach (StoredRun run in runRows)
        {
            IReadOnlyList<StoredExecution> execs = projections.ListExecutionsForRun(run.RunId);
            RunSessionFigures figures = SummarizeRunSessions(execs, aggregatesById);
            mappedRuns.Add(new
            {
                runId = run.RunId,
                harness = run.HarnessId,
                label = run.Label,
                groupingBasis = run.GroupingBasis,
                groupingRule = run.GroupingRule,
                workingDirectory = run.WorkingDirectory,
                started = run.StartedAtUtc,
                ended = run.EndedAtUtc,
                executionCount = execs.Count > 0 ? execs.Count : run.ExecutionCount,
                turnCount = figures.TurnCount,
                totalInput = figures.TotalInput,
                totalOutput = figures.TotalOutput,
                costUsd = figures.CostUsd,
                findingCount = findingCounts.GetValueOrDefault(run.RunId),
                payload = (object?)null,
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
        StoredRun? run = projections.GetRun(runId);
        if (run is null)
        {
            return null;
        }

        IModelPricingLookup lookup = pricing.LoadPricingTables();
        IReadOnlyList<StoredExecution> executions = projections.ListExecutionsForRun(runId);
        HashSet<string> runSessionIds = [.. executions
            .Where(static e => !string.IsNullOrEmpty(e.SessionId))
            .Select(static e => e.SessionId!)];
        // A derived run groups sessions rather than being named on the finding rows, so a run's
        // findings are the ones naming the run or any of its sessions.
        List<StoredFinding> findings = [.. projections.ListFindings()
            .Where(f => string.Equals(f.RunId, runId, StringComparison.Ordinal)
                || (f.SessionId is not null && runSessionIds.Contains(f.SessionId)))];
        JsonElement scorecard = rollups.BuildScorecard(runId);
        Dictionary<string, SessionAggregateView> aggregatesById = BuildAggregateIndex(lookup);

        RunSessionFigures figures = SummarizeRunSessions(executions, aggregatesById);
        List<object> enrichedExecutions = new(executions.Count);
        foreach (StoredExecution exec in executions)
        {
            (int? TurnCount, double? CostUsd) sessionFigures = SessionFiguresOf(exec, aggregatesById);
            enrichedExecutions.Add(new
            {
                executionId = exec.ExecutionId,
                runId = exec.RunId,
                sessionId = exec.SessionId,
                parentExecutionId = exec.ParentExecutionId,
                harness = exec.HarnessId,
                agentName = exec.AgentName,
                isRoot = exec.IsRoot,
                started = exec.StartedAtUtc,
                ended = exec.EndedAtUtc,
                parentLinkage = (object?)null,
                turnCount = sessionFigures.TurnCount,
                costUsd = sessionFigures.CostUsd,
                payload = (object?)null,
            });
        }

        List<object> executionTree = BuildExecutionTree(executions, aggregatesById);
        List<Dictionary<string, object?>> turns = ExtractRunTurns(executions, lookup);

        return JsonSerializer.SerializeToElement(new
        {
            run = new
            {
                runId = run.RunId,
                harness = run.HarnessId,
                label = run.Label,
                groupingBasis = run.GroupingBasis,
                groupingRule = run.GroupingRule,
                workingDirectory = run.WorkingDirectory,
                started = run.StartedAtUtc,
                ended = run.EndedAtUtc,
                executionCount = executions.Count > 0 ? executions.Count : run.ExecutionCount,
                turnCount = figures.TurnCount,
                totalInput = figures.TotalInput,
                totalOutput = figures.TotalOutput,
                costUsd = figures.CostUsd,
                findingCount = findings.Count,
                payload = (object?)null,
            },
            executions = enrichedExecutions,
            executionTree,
            findings = findings.Select(ToFindingRow),
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
        int total = operationalStore.CountQuarantinedSpans(harness);
        IReadOnlyList<StoredQuarantinedSpan> rows = operationalStore.ListQuarantinedSpans(harness, limit, offset);
        // The typed quarantine row keeps no raw payload, so no entry advertises stored evidence.
        List<object> entries = [.. rows.Select(r => (object)new
        {
            spanId = r.SpanId,
            observedNamespaces = r.ObservedNamespaces,
            reason = r.Reason,
            source = r.Source,
            name = r.Name,
            timestamp = r.TimestampUtc,
            harness = r.HarnessId,
            evidenceAvailable = false,
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
        StoredQuarantinedSpan? row = operationalStore.GetQuarantinedSpan(spanId);
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
            timestamp = row.TimestampUtc,
            harness = row.HarnessId,
            rawPayload = (string?)null,
            evidenceAvailable = false,
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
        int total = operationalStore.CountIngestProblems(harness);
        IReadOnlyList<StoredIngestProblem> rows = operationalStore.ListIngestProblems(harness, limit: limit, offset: offset);
        List<object> problems = [.. rows.Select(p => (object)ToProblemDto(p))];

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
        if (!long.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
        {
            return null;
        }

        StoredIngestProblem? row = operationalStore.GetIngestProblem(id);
        if (row is null)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(new
        {
            id = row.ProblemId,
            spanId = row.Problem.SpanId ?? string.Empty,
            severity = row.Problem.Severity,
            code = row.Problem.Code,
            message = row.Problem.Message,
            location = row.Problem.Location,
            sessionId = row.Problem.SessionKey,
            harness = row.Problem.HarnessId,
            timestamp = row.Problem.TimestampUtc,
            stack = row.Problem.Stack,
        });
    }

    /// <summary>Returns store metadata and REST contract version for <c>/api/kyber/meta</c>.</summary>
    /// <returns>Store metadata JSON element.</returns>
    public JsonElement GetMeta()
    {
        StoreStateView? storeState = operationalStore.GetStoreState();
        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = storeState?.SchemaVersion ?? SqliteRelationalStore.SchemaVersion,
            detectorVersion = storeState?.DetectorVersion ?? SqliteRelationalStore.DetectorVersion,
            storeGeneration = operationalStore.GetWriteMarker(),
            recordCount = reader.CountRecords(),
            sessionCount = reader.Sessions().Count,
            findingCount = projections.ListFindings().Count,
            version,
            apiVersion = ApiContractVersion,
        });
    }

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
        List<HarnessRollupRow> harnessRollups = [.. projections.ListHarnessRollups().Select(ToHarnessRollupRow)];
        IReadOnlyList<SourceCheckpoint> checkpoints = operationalStore.ListSourceCheckpoints();
        object refresh = BuildRefreshCoverage();
        object ingest = BuildIngestCoverage();
        List<object> quarantineByReason = [.. operationalStore.ListQuarantinedSpans()
            .GroupBy(static q => string.IsNullOrEmpty(q.Reason) ? "unknown" : q.Reason, StringComparer.Ordinal)
            .Select(static g => new { reason = g.Key, count = g.Count() })
            .OrderByDescending(static x => x.count)
            .ThenBy(static x => x.reason, StringComparer.Ordinal)];
        bool receiverEnabled = operationalStore.ReadSettings().ReceiverHosted;
        bool isPaused = operationalStore.ReadSettings().JobsPaused;
        string nowIso = clock().UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        object receiver = new
        {
            enabled = receiverEnabled,
            endpoint = receiverEnabled ? "http://127.0.0.1:4318" : null,
            status = !receiverEnabled ? "disabled" : isPaused ? "paused" : "listening",
            listening = receiverEnabled && !isPaused,
            hostedByUs = receiverEnabled,
            paused = isPaused,
            checkedAt = nowIso,
            lastAcceptedAt = (string?)null,
            failures5m = 0,
            lastError = (string?)null,
            lastCode = (string?)null,
            lastErrorAt = (string?)null,
            healthy = receiverEnabled && !isPaused,
        };
        object projectionHealth = BuildProjectionHealth();

        return JsonSerializer.SerializeToElement(new
        {
            refresh,
            ingest,
            quarantineByReason,
            checkpoints,
            receiver,
            projection = projectionHealth,
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
        string? normalizedHarness = NormalizeHarnessFilter(harness);
        List<CollectedSession> sessions = [.. reader.Sessions(normalizedHarness)];
        if (!string.IsNullOrEmpty(sessionId))
        {
            sessions = [.. sessions.Where(s => string.Equals(s.SessionKey, sessionId, StringComparison.Ordinal))];
        }

        IReadOnlyList<StoredFinding> findings = projections.ListFindings(sessionId: sessionId, runId: runId);
        List<HarnessRollupRow> harnesses = [.. projections.ListHarnessRollups().Select(ToHarnessRollupRow)];

        int boundedLimit = Math.Clamp(limit, 1, 200);
        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = ApiContractVersion,
            generatedAt = clock().UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            storeGeneration = operationalStore.GetWriteMarker(),
            sessionCount = sessions.Count,
            findingCount = findings.Count,
            harnessCount = harnesses.Count,
            harnesses,
            findings = findings.Take(boundedLimit).Select(ToFindingRow),
        }, CamelCaseJsonOptions);
    }

    /// <summary>Returns timeline span trees for <paramref name="sessionId"/> (or all sessions if omitted).</summary>
    /// <param name="sessionId">Optional session identifier.</param>
    /// <returns>Timeline trees JSON element.</returns>
    public JsonElement GetTimeline(string? sessionId = null)
    {
        EnsureProjected();
        List<CollectedSession> sessions = string.IsNullOrWhiteSpace(sessionId)
            ? [.. reader.Sessions().Take(25)]
            : reader.GetSession(sessionId) is CollectedSession single
                ? [single]
                : [];

        List<object> trees = [];
        foreach (CollectedSession session in sessions)
        {
            List<CanonicalRecord> records = [.. reader.Records(sessionKey: session.SessionKey).Select(CanonRecordHydration.Hydrate)];
            records.Sort(CompareRecords);
            TimelineNode timeline = CanonContextAnalysis.BuildTimeline(records);
            trees.Add(new { sessionId = session.SessionKey, harness = session.HarnessId, timeline });
        }

        return JsonSerializer.SerializeToElement(new { sessions = trees }, CamelCaseJsonOptions);
    }

    /// <summary>Lists recorded predictions.</summary>
    /// <param name="runId">Optional run filter.</param>
    /// <returns>Serialized predictions array.</returns>
    public JsonElement GetPredictions(string? runId = null) =>
        JsonSerializer.SerializeToElement(
            projections.ListPredictions(runId).Select(ToPredictionRow),
            CamelCaseJsonOptions);

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
        string nowIso = clock().UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string tier = confidence >= 0.8 ? "high" : confidence >= 0.5 ? "medium" : "low";

        projections.ReplacePrediction(new StoredPrediction(
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
        List<PredictionRow> predictions = [.. projections.ListPredictions().Select(ToPredictionRow)];
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

    /// <summary>Returns the catalog-driven stored-fields view for a session, or <see langword="null"/> if missing.</summary>
    /// <param name="sessionKey">Canonical session identifier.</param>
    /// <returns>Stored-fields JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetStoredFields(string sessionKey)
    {
        EnsureProjected();
        if (reader.GetSession(sessionKey) is null)
        {
            return null;
        }

        StoredFieldsView view = reader.StoredFields(sessionKey);
        return JsonSerializer.SerializeToElement(view, CamelCaseJsonOptions);
    }

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
        long generation = operationalStore.GetWriteMarker();
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
                    sessionCount = reader.Sessions().Count,
                    recordCount = reader.CountRecords(),
                    findingCount = projections.ListFindings().Count,
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
                source = new { harnesses = reader.Harnesses() },
            };
            return (200, BuildAnalyticsEnvelope(defaultScope, catalogData, new { }, freshness, new { }, null, null, null));
        }

        string? harnessFilter = request.HasValue && request.Value.ValueKind == JsonValueKind.Object
            && request.Value.TryGetProperty("harness", out JsonElement hEl) && hEl.ValueKind == JsonValueKind.String
                ? hEl.GetString()
                : null;
        string? normalizedFilter = NormalizeHarnessFilter(harnessFilter);

        if (string.Equals(normalizedOp, "search-sessions", StringComparison.Ordinal))
        {
            string? queryText = request.HasValue && request.Value.ValueKind == JsonValueKind.Object
                && request.Value.TryGetProperty("query", out JsonElement qEl) && qEl.ValueKind == JsonValueKind.String
                    ? qEl.GetString()
                    : null;

            IEnumerable<CollectedSession> matched = reader.Sessions(normalizedFilter);
            if (!string.IsNullOrWhiteSpace(queryText))
            {
                matched = matched.Where(s =>
                    s.SessionKey.Contains(queryText, StringComparison.OrdinalIgnoreCase)
                    || (s.Title?.Contains(queryText, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (s.Workspace?.Repository?.Contains(queryText, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            List<CollectedSession> items = [.. matched.Take(50)];
            object searchData = new
            {
                items = items.Select(s =>
                {
                    StoredSessionProjection? projection = projections.GetSessionProjection(s.SessionKey);
                    return new
                    {
                        sessionId = s.SessionKey,
                        harness = s.HarnessId,
                        label = projection?.Label ?? s.Title,
                        started = projection?.StartedAtUtc,
                        ended = projection?.EndedAtUtc,
                        repo = s.Workspace?.Repository,
                    };
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
            IReadOnlyList<StoredFinding> findings = projections.ListFindings();
            return (200, BuildAnalyticsEnvelope(defaultScope, new { items = findings.Select(ToFindingRow) }, new { }, freshness, new { returnedCount = findings.Count, nextCursor = (string?)null }, null, null, null));
        }

        if (string.Equals(normalizedOp, "compare", StringComparison.Ordinal))
        {
            return (200, BuildAnalyticsEnvelope(defaultScope, new { deltas = rollups.CompareHarnesses() }, new { }, freshness, new { }, null, null, null));
        }

        IModelPricingLookup lookup = pricing.LoadPricingTables();
        IReadOnlyList<SessionAggregateView> aggregates = reader.ListSessionAggregates(normalizedFilter, lookup);

        if (string.Equals(normalizedOp, "outcomes", StringComparison.Ordinal))
        {
            // The relational schema keeps no outcome rows, so an outcome stays not-measurable
            // rather than being guessed from unrelated telemetry.
            List<object> outcomes = [.. aggregates.Select(a => (object)new
            {
                sessionId = a.SessionKey,
                harness = a.HarnessId,
                outcome = "not_measurable",
            })];
            return (200, BuildAnalyticsEnvelope(defaultScope, new { items = outcomes }, new { }, freshness, new { returnedCount = outcomes.Count, nextCursor = (string?)null }, null, null, null));
        }

        // operation == "query"
        List<object> groups = [.. aggregates
            .GroupBy(a => a.HarnessId, StringComparer.Ordinal)
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

        object freshness = CreateAnalyticsFreshness(FormatTimestamp(timestamp), 0L);
        object defaultScope = CreateDefaultAnalyticsScope();
        string message = string.IsNullOrWhiteSpace(rawMessage) ? fallbackMessage : rawMessage;

        return (statusCode, BuildAnalyticsEnvelope(defaultScope, null, new { }, freshness, new { }, errorCode, message, hint));
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private object CreateAnalyticsFreshness(string generatedAt, long generation) =>
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

    /// <summary>Reads a session's aggregate and turn summaries, isolating any fault to that session.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="lookup">Read-time pricing tables.</param>
    /// <returns>The aggregate when readable, the turn summaries when readable, and the isolation marker when either read failed.</returns>
    private (SessionAggregateView? Aggregate, IReadOnlyList<ReadTimeTurnSummary> Turns, string? ProjectionStatus) ReadSessionFigures(
        string sessionKey,
        IModelPricingLookup lookup)
    {
        SessionAggregateView? aggregate = null;
        IReadOnlyList<ReadTimeTurnSummary> turns = [];
        string? status = null;

        try
        {
            aggregate = reader.GetSessionAggregate(sessionKey, lookup);
        }
        catch (Exception ex)
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            status = ProjectionFailedStatus;
        }

        try
        {
            turns = reader.SessionTurnSummaries(sessionKey, lookup);
        }
        catch (Exception ex)
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            status = ProjectionFailedStatus;
        }

        return (aggregate, turns, status);
    }

    private IReadOnlyList<ReadTimeTurnSummary> TrySessionTurnSummaries(string sessionKey)
    {
        try
        {
            return reader.SessionTurnSummaries(sessionKey, pricing.LoadPricingTables());
        }
        catch (Exception ex)
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            return [];
        }
    }

    private void RecordProjectionFailure(string sessionKey, string message)
    {
        lock (projectionFailureLock)
        {
            projectionFailures[sessionKey] = message;
        }
    }

    private object BuildProjectionHealth()
    {
        string? lastError;
        List<string> failedSessions;
        lock (projectionFailureLock)
        {
            lastError = lastProjectionPassFault ?? (projectionFailures.Count > 0 ? projectionFailures.Values.First() : null);
            failedSessions = [.. projectionFailures.Keys.Order(StringComparer.Ordinal)];
        }

        return new
        {
            status = lastError is null ? "ok" : ProjectionFailedStatus,
            lastError,
            failedSessions,
        };
    }

    private static bool IsTransientStoreFault(Exception ex) =>
        ex is SqliteExecutionException or CircuitBreakerOpenException or IOException or TimeoutException;

    private static string? NormalizeHarnessFilter(string? harness) =>
        string.IsNullOrWhiteSpace(harness) ? null : CanonPricing.NormalizeHarnessName(harness.Trim());

    private static string ResolveHarness(SessionAggregateView? aggregate, CollectedSession? session) =>
        aggregate?.HarnessId ?? session?.HarnessId ?? string.Empty;

    private static string? ResolveContextLimitSource(StoredSessionProjection projection) =>
        projection.Context?.ContextLimitSource ?? projection.ContextUnmeasurableReason;

    private List<TimelineNode> TimelineChildren(string sessionId)
    {
        List<CollectedRecord> stored = [.. reader.Records(sessionKey: sessionId)];
        List<CanonicalRecord> records = new(stored.Count);
        foreach (CollectedRecord storedRecord in stored)
        {
            CanonicalRecord hydrated = CanonRecordHydration.Hydrate(storedRecord);
            AttachCapturedPrompt(storedRecord, hydrated);
            records.Add(hydrated);
        }

        records.Sort(CompareRecords);
        if (records.Count == 0)
        {
            return [];
        }

        return [.. CanonContextAnalysis.BuildTimeline(records).Children];
    }

    /// <summary>
    /// Puts a captured Factory prompt back on the span attribute the inspector reads.
    /// The relational row keeps the text as a content part, and content that was withheld
    /// never becomes a part, so a consent-off turn stays without the prompt.
    /// </summary>
    private static void AttachCapturedPrompt(CollectedRecord stored, CanonicalRecord hydrated)
    {
        if (!string.Equals(stored.HarnessId, "droid", StringComparison.OrdinalIgnoreCase)
            || hydrated.Raw is not Dictionary<string, JsonElement> raw)
        {
            return;
        }

        string? prompt = CapturedPromptText(stored);
        if (prompt is null)
        {
            return;
        }

        raw[FactoryKeys.PromptText] = JsonSerializer.SerializeToElement(prompt);
    }

    private static string? CapturedPromptText(CollectedRecord stored)
    {
        foreach (CollectedContentPart part in stored.Parts)
        {
            if (part.Bucket is not (ContentBucket.UserPrompt or ContentBucket.UserMessage or ContentBucket.ConversationHistory))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.PlainText))
            {
                continue;
            }

            return part.PlainText;
        }

        return null;
    }

    private static List<Dictionary<string, object?>> RequestRows(SessionAggregateView aggregate)
    {
        List<Dictionary<string, object?>> rows = new(aggregate.Requests.Count);
        foreach (ReadTimeSessionRequestSummary request in aggregate.Requests)
        {
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = request.RequestSpanId,
                ["timestamp"] = request.TimestampUtc,
                ["turns"] = request.Turns,
                ["model"] = request.Model,
                ["prompt"] = request.PromptPreview,
            });
        }

        return rows;
    }

    private static List<Dictionary<string, object?>> ToolRows(SessionAggregateView aggregate)
    {
        List<Dictionary<string, object?>> rows = new(aggregate.Tools.Count);
        foreach (ReadTimeSessionToolSummary tool in aggregate.Tools)
        {
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = tool.Name,
                ["server"] = tool.Server,
                ["invocations"] = tool.Invocations,
                ["schema_tokens"] = tool.SchemaTokens,
                ["turns_resident"] = tool.TurnsResident,
                ["total_schema_cost"] = tool.TotalSchemaCost,
                ["invoked"] = tool.Invoked,
            });
        }

        return rows;
    }

    private static object BuildSessionSummary(SessionAggregateView aggregate) =>
        new
        {
            turn_count = aggregate.TurnCount,
            request_count = aggregate.RequestCount,
            total_input = aggregate.TotalInput,
            total_output = aggregate.TotalOutput,
            total_cache_read = aggregate.TotalCacheRead,
            total_cache_creation = aggregate.TotalCacheCreation,
            duration_ms = aggregate.DurationMs,
            models = aggregate.Models,
            cache_hit_ratio = aggregate.CacheHitRatio,
            cache_creation_coverage = aggregate.CacheCreationCoverage,
            tool_calls = aggregate.ToolCalls,
            tools_invoked = aggregate.ToolsInvoked,
            tools_offered = aggregate.ToolsOffered,
            has_tool_definitions = aggregate.HasToolDefinitions,
            cost = aggregate.PublishedCost,
            cost_reported = aggregate.ReportedCost,
            cost_basis_mismatch = aggregate.CostBasisMismatch,
            coverage = new
            {
                schema_measurable = aggregate.Coverage.SchemaMeasurable,
                context_measurable = aggregate.Coverage.ContextMeasurable,
            },
            problems = aggregate.Problems,
        };

    private static Dictionary<string, object?> ToTurnDto(ReadTimeTurnSummary turn) => new(StringComparer.Ordinal)
    {
        ["turnIndex"] = turn.Position,
        ["spanId"] = turn.SpanId,
        ["timestamp"] = turn.TimestampUtc,
        ["model"] = turn.Model,
        ["inputTokens"] = turn.InputTokens,
        ["outputTokens"] = turn.OutputTokens,
        ["tokens"] = turn.InputTokens.HasValue && turn.OutputTokens.HasValue
            ? turn.InputTokens.Value + turn.OutputTokens.Value
            : null,
        ["freshInputTokens"] = turn.FreshInputTokens,
        ["cacheReadTokens"] = turn.CacheReadTokens,
        ["cacheCreationTokens"] = turn.CacheCreationTokens,
        ["reasoningTokens"] = turn.ReasoningTokens,
        ["durationMs"] = turn.DurationMs,
        ["cost"] = turn.PublishedCost,
        ["costUsd"] = ExtractUsdCost(turn.PublishedCost),
        ["costReported"] = turn.ReportedCost,
    };

    private static object ToProblemDto(StoredIngestProblem row) => new
    {
        id = row.ProblemId,
        spanId = row.Problem.SpanId ?? string.Empty,
        severity = row.Problem.Severity,
        code = row.Problem.Code,
        message = row.Problem.Message,
        location = row.Problem.Location,
        sessionId = row.Problem.SessionKey,
        harness = row.Problem.HarnessId,
        timestamp = row.Problem.TimestampUtc,
    };

    private static Finding ToFindingRow(StoredFinding finding) =>
        new(
            Id: finding.FindingId,
            DetectorId: finding.DetectorId,
            Title: finding.Title,
            Mechanism: finding.Mechanism,
            EvidenceLinks: finding.EvidenceLinks,
            Confidence: finding.Confidence,
            EstimatedWasteTokens: finding.EstimatedWasteTokens,
            Recommendation: finding.Recommendation,
            ErrorBar: finding.ErrorBar,
            OutcomeRiskCaveat: finding.OutcomeRiskCaveat,
            RunId: finding.RunId,
            SessionId: finding.SessionId,
            RankScore: finding.RankScore,
            MeasurementClass: finding.MeasurementClass,
            ConfidenceBasis: finding.ConfidenceBasis,
            WhatWouldRaiseIt: finding.WhatWouldRaiseIt,
            Payload: finding.CompactionHazard is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["compactionHazard"] = new
                    {
                        contextLimit = finding.CompactionHazard.ContextLimit,
                        contextLimitSource = finding.CompactionHazard.ContextLimitSource,
                    },
                });

    private static double? ExtractUsdCost(ReadTimeCostSummary? cost) =>
        cost is not null
        && cost.Status == CostStatus.Priced
        && string.Equals(cost.Currency, "USD", StringComparison.Ordinal)
        && cost.Value is double value
        && double.IsFinite(value)
            ? value
            : null;

    private Dictionary<string, SessionAggregateView> BuildAggregateIndex(IModelPricingLookup lookup)
    {
        Dictionary<string, SessionAggregateView> aggregates = new(StringComparer.Ordinal);
        foreach (StoredSessionProjection projection in projections.ListSessionProjections())
        {
            try
            {
                SessionAggregateView? aggregate = reader.GetSessionAggregate(projection.SessionId, lookup);
                if (aggregate is not null)
                {
                    aggregates[projection.SessionId] = aggregate;
                }
            }
            catch (Exception ex)
            {
                RecordProjectionFailure(projection.SessionId, ex.Message);
            }
        }

        return aggregates;
    }

    private static (int? TurnCount, double? CostUsd) SessionFiguresOf(
        StoredExecution execution,
        Dictionary<string, SessionAggregateView> aggregatesById)
    {
        if (string.IsNullOrEmpty(execution.SessionId)
            || !aggregatesById.TryGetValue(execution.SessionId, out SessionAggregateView? aggregate))
        {
            return (null, null);
        }

        return (aggregate.TurnCount, ExtractUsdCost(aggregate.PublishedCost));
    }

    private static RunSessionFigures SummarizeRunSessions(
        IReadOnlyList<StoredExecution> executions,
        Dictionary<string, SessionAggregateView> aggregatesById)
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

        foreach (StoredExecution exec in executions)
        {
            if (string.IsNullOrEmpty(exec.SessionId) || !seenSessions.Add(exec.SessionId))
            {
                continue;
            }

            if (!aggregatesById.TryGetValue(exec.SessionId, out SessionAggregateView? aggregate))
            {
                continue;
            }

            turnCount += aggregate.TurnCount;
            hasTurns = true;

            if (aggregate.TotalInput.HasValue)
            {
                totalInput += aggregate.TotalInput.Value;
                hasInput = true;
            }

            if (aggregate.TotalOutput.HasValue)
            {
                totalOutput += aggregate.TotalOutput.Value;
                hasOutput = true;
            }

            if (ExtractUsdCost(aggregate.PublishedCost) is double sessionCost)
            {
                costUsd += sessionCost;
                hasCost = true;
            }
        }

        return new RunSessionFigures(
            hasTurns ? turnCount : null,
            hasInput ? totalInput : null,
            hasOutput ? totalOutput : null,
            hasCost ? Math.Round(costUsd, 6) : null);
    }

    private Dictionary<string, string> BuildRunBySessionIndex(IReadOnlyList<StoredRun> runRows)
    {
        Dictionary<string, string> runBySession = new(StringComparer.Ordinal);
        foreach (StoredRun run in runRows)
        {
            foreach (StoredExecution exec in projections.ListExecutionsForRun(run.RunId))
            {
                if (!string.IsNullOrEmpty(exec.SessionId))
                {
                    runBySession.TryAdd(exec.SessionId, run.RunId);
                }
            }
        }

        return runBySession;
    }

    private Dictionary<string, int> CountFindingsByRun(Dictionary<string, string> runBySession)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (StoredFinding finding in projections.ListFindings())
        {
            string? runId = finding.RunId
                ?? (finding.SessionId is not null ? runBySession.GetValueOrDefault(finding.SessionId) : null);
            if (runId is not null)
            {
                counts[runId] = counts.GetValueOrDefault(runId) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Maps a typed harness rollup onto the legacy rollup row shape the harness summary DTO and
    /// scorecard still consume, mirroring the rollups engine's own translation.
    /// </summary>
    /// <param name="rollup">Typed harness rollup.</param>
    /// <returns>Legacy rollup row carrying the typed metrics.</returns>
    private static HarnessRollupRow ToHarnessRollupRow(StoredHarnessRollup rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);

        string pressureReason = $"No measurable turns found in recorded sessions for harness \"{rollup.HarnessId}\".";
        Dictionary<string, JsonElement> measurability = new(StringComparer.Ordinal)
        {
            ["context_pressure"] = MetricTagOf(rollup, "context_pressure_median", pressureReason),
            ["context_pressure_median"] = MetricTagOf(rollup, "context_pressure_median", pressureReason),
            ["context_pressure_p95"] = MetricTagOf(rollup, "context_pressure_p95", pressureReason),
            ["cache_hit_rate"] = MetricTagOf(rollup, "cache_hit_rate", $"Cache-read telemetry not observed for harness \"{rollup.HarnessId}\"."),
            ["tool_yield"] = MetricTagOf(rollup, "tool_yield", $"No tool definitions recorded for harness \"{rollup.HarnessId}\"."),
            ["delegation_overhead"] = MetricTagOf(rollup, "delegation_overhead", $"No subagent executions observed for harness \"{rollup.HarnessId}\"."),
            ["field_coverage"] = JsonSerializer.SerializeToElement("measured"),
        };

        return new HarnessRollupRow(
            Harness: rollup.HarnessId,
            SampleCount: Math.Max(rollup.SessionCount, Math.Max(rollup.RunCount, rollup.ExecutionCount)),
            ContextPressureMedian: CanonPipelineShared.RollupMetricValue(rollup, "context_pressure_median"),
            ContextPressureP95: CanonPipelineShared.RollupMetricValue(rollup, "context_pressure_p95"),
            CacheHitRate: CanonPipelineShared.RollupMetricValue(rollup, "cache_hit_rate"),
            ToolYield: CanonPipelineShared.RollupMetricValue(rollup, "tool_yield"),
            DelegationOverhead: CanonPipelineShared.RollupMetricValue(rollup, "delegation_overhead"),
            FieldCoverage: ComputeFieldCoverageOf(rollup),
            Measurability: measurability,
            Payload: JsonSerializer.SerializeToElement(new
            {
                sessionCount = rollup.SessionCount,
                runCount = rollup.RunCount,
                executionCount = rollup.ExecutionCount,
                unknownWindowSessions = rollup.UnknownWindowSessions,
                windowSessionsTotal = rollup.WindowSessionsTotal,
            }));
    }

    private static JsonElement MetricTagOf(StoredHarnessRollup rollup, string metric, string missingReason)
    {
        foreach (StoredHarnessRollupMetric entry in rollup.Metrics)
        {
            if (string.Equals(entry.Metric, metric, StringComparison.Ordinal))
            {
                return JsonSerializer.SerializeToElement(entry.Basis);
            }
        }

        return JsonSerializer.SerializeToElement(new { status = "not_measurable", reason = missingReason });
    }

    private static double ComputeFieldCoverageOf(StoredHarnessRollup rollup)
    {
        int covered = 0;
        if (CanonPipelineShared.RollupMetricValue(rollup, "context_pressure_median").HasValue
            || CanonPipelineShared.RollupMetricValue(rollup, "context_pressure_p95").HasValue)
        {
            covered++;
        }

        if (CanonPipelineShared.RollupMetricValue(rollup, "cache_hit_rate").HasValue)
        {
            covered++;
        }

        if (CanonPipelineShared.RollupMetricValue(rollup, "tool_yield").HasValue)
        {
            covered++;
        }

        if (CanonPipelineShared.RollupMetricValue(rollup, "delegation_overhead").HasValue)
        {
            covered++;
        }

        return Math.Round((double)covered / CanonRollupsEngine.CoverageDimensions.Count, 4);
    }

    private HarnessAggregationContext BuildHarnessAggregationContext()
    {
        Dictionary<string, int> sessionCounts = new(StringComparer.Ordinal);
        Dictionary<string, string> sessionHarnessById = new(StringComparer.Ordinal);
        foreach (CollectedSession session in reader.Sessions())
        {
            string norm = CanonPricing.NormalizeHarnessName(session.HarnessId);
            sessionCounts[norm] = sessionCounts.GetValueOrDefault(norm) + 1;
            sessionHarnessById[session.SessionKey] = norm;
        }

        Dictionary<string, int> runCounts = new(StringComparer.Ordinal);
        Dictionary<string, string> runHarnessById = new(StringComparer.Ordinal);
        foreach (StoredRun run in projections.ListRuns())
        {
            string norm = CanonPricing.NormalizeHarnessName(run.HarnessId);
            runCounts[norm] = runCounts.GetValueOrDefault(norm) + 1;
            runHarnessById[run.RunId] = norm;
        }

        Dictionary<string, int> findingCounts = new(StringComparer.Ordinal);
        foreach (StoredFinding finding in projections.ListFindings())
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
        foreach (SourceCheckpoint cp in operationalStore.ListSourceCheckpoints())
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
            // The typed rollup row keeps no reason payload, so a data-less harness states its
            // reason here instead of a stored JSON blob.
            noDataReason = row.SampleCount == 0
                ? $"No measurable sessions recorded for harness \"{row.Harness}\"."
                : null,
            checkpointSummary = BuildCheckpointSummary(cps),
            scorecard = CanonRollupsEngine.BuildHarnessScorecard(row),
            payload = row.Payload,
        };
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

    private List<object> BuildExecutionTree(
        IReadOnlyList<StoredExecution> executions,
        Dictionary<string, SessionAggregateView> aggregatesById)
    {
        Dictionary<string, ExecutionTreeNode> nodesById = new(StringComparer.Ordinal);
        List<ExecutionTreeNode> ordered = new(executions.Count);
        foreach (StoredExecution exec in executions)
        {
            (int? TurnCount, double? CostUsd) sessionFigures = SessionFiguresOf(exec, aggregatesById);
            ExecutionTreeNode node = new(exec, sessionFigures.TurnCount, sessionFigures.CostUsd, []);
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
            harness = node.Row.HarnessId,
            agentName = node.Row.AgentName,
            isRoot = node.Row.IsRoot,
            started = node.Row.StartedAtUtc,
            ended = node.Row.EndedAtUtc,
            parentLinkage = (object?)null,
            turnCount = node.TurnCount,
            costUsd = node.CostUsd,
            payload = (object?)null,
            children = node.Children.Select(ToExecutionTreeDto).ToList(),
        };
    }

    private List<Dictionary<string, object?>> ExtractRunTurns(
        IReadOnlyList<StoredExecution> executions,
        IModelPricingLookup lookup)
    {
        HashSet<string> seenSessions = new(StringComparer.Ordinal);
        List<Dictionary<string, object?>> turns = [];
        foreach (StoredExecution exec in executions)
        {
            if (string.IsNullOrEmpty(exec.SessionId) || !seenSessions.Add(exec.SessionId))
            {
                continue;
            }

            IReadOnlyList<ReadTimeTurnSummary> sessionTurns;
            try
            {
                sessionTurns = reader.SessionTurnSummaries(exec.SessionId, lookup);
            }
            catch (Exception ex)
            {
                RecordProjectionFailure(exec.SessionId, ex.Message);
                continue;
            }

            foreach (ReadTimeTurnSummary turn in sessionTurns)
            {
                Dictionary<string, object?> dto = ToTurnDto(turn);
                dto["executionId"] = exec.ExecutionId;
                dto["sessionId"] = exec.SessionId;
                turns.Add(dto);
            }
        }

        return turns;
    }

    private object BuildRefreshCoverage()
    {
        IReadOnlyList<RefreshRunRow> runs = operationalStore.ListRefreshRuns(50);
        RefreshRunRow? success = runs.Where(static r => string.Equals(r.Status, "success", StringComparison.Ordinal))
            .OrderByDescending(static r => r.StartedAt, StringComparer.Ordinal)
            .FirstOrDefault();
        RefreshRunRow? failure = runs.Where(static r => string.Equals(r.Status, "failure", StringComparison.Ordinal))
            .OrderByDescending(static r => r.StartedAt, StringComparer.Ordinal)
            .FirstOrDefault();
        RefreshRunRow? running = runs.Where(static r => string.Equals(r.Status, "running", StringComparison.Ordinal))
            .OrderByDescending(static r => r.StartedAt, StringComparer.Ordinal)
            .FirstOrDefault();

        string? lastCleanAt = operationalStore.GetLastCleanAtUtc();
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
                .UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
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
        Dictionary<string, int> recordCounts = new(StringComparer.Ordinal);
        foreach (IGrouping<string, StoredSource> group in reader.Sources()
                     .GroupBy(static s => s.Source.Locator, StringComparer.Ordinal))
        {
            int count = 0;
            foreach (StoredSource source in group)
            {
                count += reader.Records(sourceId: source.SourceId).Count;
            }

            recordCounts[group.Key] = count;
        }

        Dictionary<string, (int Total, string? LastAt)> logSums = new(StringComparer.Ordinal);
        string? lastReceivedAt = null;
        foreach (IngestLogEntry entry in operationalStore.ListIngestLog())
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

    private static int CompareRecords(CanonicalRecord left, CanonicalRecord right)
    {
        int byTimestamp = string.Compare(left.Timestamp, right.Timestamp, StringComparison.Ordinal);
        return byTimestamp != 0 ? byTimestamp : string.Compare(left.SpanId, right.SpanId, StringComparison.Ordinal);
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

    /// <summary>
    /// A measured total stays a number, including zero. A session that has turns and no input
    /// counter publishes the not-measurable declaration the sessions list already promised:
    /// a missing number is not a measured zero, and the reason names the harness.
    /// </summary>
    private static object? TokenFigure(long? total, int? turnCount, string harness)
    {
        if (total is not null || turnCount is null or 0)
        {
            return total;
        }

        string named = string.IsNullOrWhiteSpace(harness) ? "The harness" : harness;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["availability"] = "not_measurable",
            ["reason"] = $"{named} telemetry did not report token usage for this session.",
        };
    }

    private static Dictionary<string, object?> ToSessionDto(SessionNode node)
    {
        Dictionary<string, object?> dto = new(StringComparer.Ordinal)
        {
            ["session_id"] = node.SessionId,
            ["sessionId"] = node.SessionId,
            ["harness"] = node.Harness,
            ["label"] = node.Label,
            ["is_subagent"] = node.IsSubagent,
            ["parent_session"] = node.ParentSession,
            ["agent_name"] = node.AgentName,
            ["repo"] = node.Repo,
            ["branch"] = node.Branch,
            ["started"] = node.Started,
            ["ended"] = node.Ended,
            ["turn_count"] = node.TurnCount,
            ["request_count"] = node.RequestCount,
            ["total_input"] = TokenFigure(node.TotalInput, node.TurnCount, node.Harness),
            ["total_output"] = node.TotalOutput,
            ["cost_usd"] = node.CostUsd,
            ["cost"] = node.Cost is null ? new { basis = "unknown", status = "no_rate" } : node.Cost,
            ["cost_reported"] = node.CostReported,
            ["cost_basis_mismatch"] = node.CostBasisMismatch,
            ["models"] = node.Models,
            ["problems"] = node.Problems,
            ["subagents"] = node.Subagents.Select(ToSessionDto).ToList(),
        };
        if (node.ProjectionStatus is not null)
        {
            dto["projection_status"] = node.ProjectionStatus;
        }

        return dto;
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

    private sealed record HarnessAggregationContext(
        Dictionary<string, int> SessionCounts,
        Dictionary<string, int> RunCounts,
        Dictionary<string, int> FindingCounts,
        Dictionary<string, List<SourceCheckpoint>> CheckpointsByHarness);

    private sealed record RunSessionFigures(
        int? TurnCount,
        long? TotalInput,
        long? TotalOutput,
        double? CostUsd);

    private sealed record ExecutionTreeNode(
        StoredExecution Row,
        int? TurnCount,
        double? CostUsd,
        List<ExecutionTreeNode> Children);

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
        ReadTimeCostSummary? Cost,
        ReadTimeCostSummary? CostReported,
        ReadTimeCostBasisMismatch? CostBasisMismatch,
        IReadOnlyList<string> Models,
        int Problems,
        string? ProjectionStatus,
        List<SessionNode> Subagents);
}
