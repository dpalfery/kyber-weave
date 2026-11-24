using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
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
/// contracts and parses no persisted store JSON. The sessions pages — the roots page and a
/// session's subagent page — place and order their rows with paged projection-header reads and
/// price only the keys on the page through the keyed, content-free
/// <see cref="IRelationalStoreReader.SessionListFigures(IReadOnlyCollection{string}, IModelPricingLookup?)"/>
/// read model; the run, harness, and comparison pages price whole harnesses through the
/// harness-scoped <see cref="IRelationalStoreReader.SessionListFigures(string?, IModelPricingLookup?)"/>.
/// The session section pages answer their figures from the content-free
/// <see cref="IRelationalStoreReader.SessionSummaryFigures"/>,
/// <see cref="IRelationalStoreReader.SessionTurnFigures"/>, and
/// <see cref="IRelationalStoreReader.TurnFigures"/> read models priced at read time through
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

    /// <summary>Default number of root sessions on a sessions page.</summary>
    public const int DefaultSessionPageLimit = 100;

    /// <summary>Subagents a sessions page shows inline under each root; the rest are paged by <see cref="GetSubagentPage"/>.</summary>
    public const int SubagentPreviewLimit = 25;

    /// <summary>Default number of subagents on a session's subagent page.</summary>
    public const int DefaultSubagentPageLimit = 25;

    /// <summary>Rows per page the section list routes serve when the request names no limit.</summary>
    public const int DefaultSectionPageLimit = 25;

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

    private static readonly string[] ReportBucketKeys =
    [
        "system_prompt",
        "tool_definitions",
        "instruction_context",
        "conversation_history",
        "tool_result_content",
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
    private readonly string storePath;
    private readonly DashOptions? dashOptions;
    private readonly IOtlpCollectorService? collector;
    private readonly IOtlpListenerState? listener;
    private readonly Lock projectionFailureLock = new();
    private readonly Dictionary<string, string> projectionFailures = new(StringComparer.Ordinal);
    private string? lastProjectionPassFault;
    private string? lastBatchedReadFault;

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
    /// <param name="storePath">Optional backing store path surfaced in report coverage metadata.</param>
    /// <param name="options">Composition options carrying the configured collector port, when the host has one.</param>
    /// <param name="collector">Live collector, when this process hosts one. Absent means admission failures are unknown.</param>
    /// <param name="listener">Bind record for this process's collector socket. Absent means the listener was not observed.</param>
    public KyberBridgeEngine(
        IRelationalStoreReader reader,
        IProjectionStore projections,
        IOperationalStore operationalStore,
        IPricingRepository pricing,
        CanonSessionProjectorEngine projector,
        CanonRollupsEngine rollups,
        string version = DefaultVersion,
        Func<DateTimeOffset>? clock = null,
        string? instanceId = null,
        string? storePath = null,
        DashOptions? options = null,
        IOtlpCollectorService? collector = null,
        IOtlpListenerState? listener = null)
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
        this.storePath = string.IsNullOrWhiteSpace(storePath)
            ? CanonDbPath.Resolve()
            : storePath.Trim();
        dashOptions = options;
        this.collector = collector;
        this.listener = listener;
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
                ProjectionPassReport report = projector.ProjectIncrementalForReads();
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

    /// <summary>Returns one page of root sessions, each with its first subagents and its subagent total.</summary>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Roots on the page.</param>
    /// <param name="harness">
    /// Optional harness filter. It also scopes the root rule, so a session whose parent belongs to
    /// another harness is a root here.
    /// </param>
    /// <param name="search">
    /// Optional text a root's label or session id must contain, matched literally and without
    /// regard to ASCII case.
    /// </param>
    /// <returns>Paging envelope JSON element.</returns>
    /// <remarks>
    /// <para>
    /// The store chooses the page — root resolution, harness filter, search, order and window —
    /// and only the keys the page shows are priced, so a page costs what it shows and not what
    /// the store holds.
    /// </para>
    /// <para>
    /// A root carries its first <see cref="SubagentPreviewLimit"/> subagents inline under
    /// <c>subagents</c> and its full subagent count under <c>subagent_total</c>; the rest are paged
    /// by <see cref="GetSubagentPage"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    public JsonElement GetSessionPage(int page = 1, int limit = DefaultSessionPageLimit, string? harness = null, string? search = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        EnsureProjected();
        IModelPricingLookup lookup = pricing.LoadPricingTables();
        SessionHeaderPageResult headerPage = projections.SessionHeaderPage(NormalizeHarnessFilter(harness), NormalizeSessionSearch(search), page, limit);

        Dictionary<string, StoredSessionHeader> headersById = new(headerPage.Items.Count, StringComparer.Ordinal);
        foreach (StoredSessionHeader root in headerPage.Items)
        {
            headersById[root.SessionId] = root;
        }

        Dictionary<string, SessionChildPreviewEntry> previewsByRoot = new(headerPage.Items.Count, StringComparer.Ordinal);
        if (headerPage.Items.Count > 0)
        {
            // One read answers every root's subagent total and first subagents, whatever the
            // number of roots: never a child read per root.
            List<string> previewChildIds = [];
            foreach (SessionChildPreviewEntry preview in projections.SessionChildPreview(headersById.Keys, SubagentPreviewLimit))
            {
                previewsByRoot[preview.ParentSessionId] = preview;
                previewChildIds.AddRange(preview.ChildSessionIds);
            }

            if (previewChildIds.Count > 0)
            {
                foreach (StoredSessionHeader child in projections.SessionHeadersByKeys(previewChildIds))
                {
                    headersById[child.SessionId] = child;
                }
            }
        }

        (Dictionary<string, SessionListFigure> figuresBySession, HashSet<string> faultedSessions) =
            ReadSessionPageFigures([.. headersById.Keys], lookup);

        List<Dictionary<string, object?>> rows = new(headerPage.Items.Count);
        foreach (StoredSessionHeader root in headerPage.Items)
        {
            SessionNode node = ToSessionNode(root, figuresBySession, faultedSessions);
            int subagentTotal = 0;
            if (previewsByRoot.TryGetValue(root.SessionId, out SessionChildPreviewEntry? preview))
            {
                subagentTotal = preview.TotalChildren;
                foreach (string childId in preview.ChildSessionIds)
                {
                    if (headersById.TryGetValue(childId, out StoredSessionHeader? child))
                    {
                        node.Subagents.Add(ToSessionNode(child, figuresBySession, faultedSessions));
                    }
                }
            }

            Dictionary<string, object?> dto = ToSessionDto(node);
            dto["subagent_total"] = subagentTotal;
            rows.Add(dto);
        }

        return JsonSerializer.SerializeToElement(PagingEnvelope("sessions", rows, headerPage.Total, limit, PageOffset(page, limit), page), CamelCaseJsonOptions);
    }

    /// <summary>Returns one page of a session's subagents.</summary>
    /// <param name="parentId">Session whose subagents are paged.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Subagents on the page.</param>
    /// <returns>Paging envelope JSON element.</returns>
    /// <remarks>
    /// The subagents of <paramref name="parentId"/> are the sessions that attach to it, oldest
    /// start first, over every harness — the same set <see cref="GetSessionPage"/> previews inline,
    /// so a root's <c>subagent_total</c> is what this route pages. Only the page's keys are priced.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="parentId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    public JsonElement GetSubagentPage(string parentId, int page = 1, int limit = DefaultSubagentPageLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        EnsureProjected();
        SessionHeaderPageResult childPage = projections.SessionChildHeaderPage(parentId, page, limit);
        (Dictionary<string, SessionListFigure> figuresBySession, HashSet<string> faultedSessions) = ReadSessionPageFigures(
            [.. childPage.Items.Select(static header => header.SessionId)],
            pricing.LoadPricingTables());

        List<Dictionary<string, object?>> rows = new(childPage.Items.Count);
        foreach (StoredSessionHeader child in childPage.Items)
        {
            rows.Add(ToSessionDto(ToSessionNode(child, figuresBySession, faultedSessions)));
        }

        return JsonSerializer.SerializeToElement(PagingEnvelope("subagents", rows, childPage.Total, limit, PageOffset(page, limit), page), CamelCaseJsonOptions);
    }

    /// <summary>
    /// Reads the figures of exactly the keys a page shows, in one keyed pass. A faulted pass is
    /// isolated: the fault is recorded in the coverage payload's batch slot, and each key on the
    /// page is then read on its own, so one broken session still surfaces as
    /// <c>PROJECTION_FAILED</c> while the rest of the page answers.
    /// </summary>
    /// <param name="sessionKeys">The page's session keys: its rows and, on the sessions page, the subagents shown inline.</param>
    /// <param name="lookup">Read-time pricing tables.</param>
    /// <returns>The figures by session key, and the keys whose figures could not be read.</returns>
    private (Dictionary<string, SessionListFigure> Figures, HashSet<string> FaultedSessions) ReadSessionPageFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup lookup)
    {
        Dictionary<string, SessionListFigure> figuresBySession = new(sessionKeys.Count, StringComparer.Ordinal);
        HashSet<string> faultedSessions = new(StringComparer.Ordinal);
        if (sessionKeys.Count == 0)
        {
            return (figuresBySession, faultedSessions);
        }

        try
        {
            foreach (SessionListFigure figure in reader.SessionListFigures(sessionKeys, lookup))
            {
                figuresBySession[figure.SessionKey] = figure;
            }

            ClearBatchedReadFault();
        }
        catch (Exception ex)
        {
            // A faulted batched read degrades to per-session aggregate reads, so one broken
            // session still surfaces as PROJECTION_FAILED while the remaining sessions answer.
            // The per-session retry is the isolation; the batch fault itself is recorded in the
            // coverage payload so a fault recurring on every poll stays visible even when every
            // retry succeeds.
            RecordBatchedReadFault(ex.Message);
            figuresBySession = [];
            foreach (string sessionKey in sessionKeys)
            {
                try
                {
                    foreach (SessionListFigure figure in reader.SessionListFigures([sessionKey], lookup))
                    {
                        figuresBySession[figure.SessionKey] = figure;
                    }
                }
                catch (Exception ex2)
                {
                    RecordProjectionFailure(sessionKey, ex2.Message);
                    faultedSessions.Add(sessionKey);
                }
            }
        }

        return (figuresBySession, faultedSessions);
    }

    /// <summary>
    /// Shapes one row of a sessions page from its projection header and its figures: identity,
    /// place in the tree and timestamps come from the header, and everything priced from the
    /// figures. A row whose figures could not be read keeps its header and is marked
    /// <c>PROJECTION_FAILED</c>.
    /// </summary>
    /// <param name="header">The session's projection header.</param>
    /// <param name="figuresBySession">The page's figures by session key.</param>
    /// <param name="faultedSessions">Keys whose figures could not be read.</param>
    /// <returns>The session node.</returns>
    private static SessionNode ToSessionNode(StoredSessionHeader header, IReadOnlyDictionary<string, SessionListFigure> figuresBySession, HashSet<string> faultedSessions)
    {
        figuresBySession.TryGetValue(header.SessionId, out SessionListFigure? figure);
        string harnessId = figure?.HarnessId ?? string.Empty;
        string label = !string.IsNullOrWhiteSpace(header.Label)
            ? header.Label
            : CanonPricing.FormatSessionTitle(new SessionTitleInput(header.SessionId, header.AgentName, harnessId, header.Label));

        return new SessionNode(
            SessionId: header.SessionId,
            Harness: harnessId,
            Label: label,
            IsSubagent: header.IsSubagent,
            ParentSession: header.ParentSessionId,
            AgentName: header.AgentName,
            Repo: figure?.Repo,
            Branch: figure?.Branch,
            Started: header.StartedAtUtc,
            Ended: header.EndedAtUtc,
            TurnCount: figure?.TurnCount,
            RequestCount: figure?.RequestCount,
            TotalInput: figure?.TotalInput,
            TotalOutput: figure?.TotalOutput,
            CostUsd: ExtractUsdCost(figure?.PublishedCost),
            Cost: figure?.PublishedCost,
            CostReported: figure?.ReportedCost,
            CostBasisMismatch: figure?.CostBasisMismatch,
            Models: figure?.Models ?? [],
            Problems: figure?.ProblemsCount ?? 0,
            ProjectionStatus: faultedSessions.Contains(header.SessionId) ? ProjectionFailedStatus : null,
            Subagents: []);
    }

    /// <summary>Returns the bounded session summary (without full turn bodies), or <see langword="null"/> if missing.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <returns>Summary JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetSessionSummary(string sessionId)
    {
        EnsureProjected();
        // The section 404 check reads the projection header row, not the whole projection the
        // detail route loads; the projection below answers the payload's span and schema fields.
        if (projections.SessionHeadersByKeys([sessionId]).Count == 0)
        {
            return null;
        }

        StoredSessionProjection? projection = projections.GetSessionProjection(sessionId);
        if (projection is null)
        {
            return null;
        }

        CollectedSession? session = reader.GetSession(sessionId);
        IModelPricingLookup lookup = pricing.LoadPricingTables();
        (SessionSummaryFigure? figures, string? projectionStatus) = ReadSessionSummaryFigures(sessionId, lookup);
        string harness = figures?.HarnessId ?? session?.HarnessId ?? string.Empty;
        (IReadOnlyList<string> distinctSkills, _) =
            CollectSkillCounts(figures?.SkillInvocations ?? []);

        IReadOnlyList<string> offeredTools = figures is null ? [] : OfferedToolNames(figures, projection);
        int unusedToolCount = 0;
        if (figures is not null)
        {
            HashSet<string> invokedTools = new(figures.ToolsInvoked, StringComparer.Ordinal);
            unusedToolCount = offeredTools.Count(toolName => !invokedTools.Contains(toolName));
        }

        int problemCount = figures?.Problems.Count ?? 0;
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["session_id"] = projection.SessionId,
            ["harness"] = harness,
            ["label"] = projection.Label,
            ["is_subagent"] = projection.IsSubagent,
            ["parent_session"] = projection.ParentSessionId,
            ["agent_name"] = projection.AgentName,
            ["repo"] = session?.Workspace?.Repository,
            ["branch"] = session?.Workspace?.Branch,
            ["started"] = projection.StartedAtUtc,
            ["ended"] = projection.EndedAtUtc,
            ["span_count"] = projection.SpanCount,
            ["problems"] = problemCount,
            ["problems_count"] = problemCount,
            ["request_count"] = figures?.RequestCount ?? 0,
            ["context_limit_source"] = ResolveContextLimitSource(projection),
            ["costBasisMismatch"] = BuildCostBasisMismatchObject(figures?.CostBasisMismatch),
            ["summary"] = figures is null ? null : BuildSessionSummary(figures, projection, harness, distinctSkills),
            ["notes"] = BuildNotes(projection, figures, distinctSkills),
            ["reconciliation"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["total"] = figures?.ReconciliationTotal ?? 0,
                ["mismatched"] = figures?.ReconciliationMismatched ?? 0,
            },
            ["tools_offered_count"] = offeredTools.Count,
            ["tools_invoked_count"] = figures?.ToolsInvoked.Count ?? 0,
            ["unused_tools_count"] = unusedToolCount,
            ["skills_count"] = distinctSkills.Count,
        };

        if (projectionStatus is not null)
        {
            payload["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>
    /// Returns the charts section payload — the detail payload's turn list and context object —
    /// from the per-session turn figures and the stored context projection, or
    /// <see langword="null"/> when the session has no projection.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <returns>Charts JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetSessionCharts(string sessionId)
    {
        EnsureProjected();
        // The section 404 check reads the projection header row, not the whole projection the
        // charts payload's context needs below.
        if (projections.SessionHeadersByKeys([sessionId]).Count == 0)
        {
            return null;
        }

        StoredSessionProjection? projection = projections.GetSessionProjection(sessionId);
        if (projection is null)
        {
            return null;
        }

        IModelPricingLookup lookup = pricing.LoadPricingTables();
        (SessionTurnFigureSet? figures, string? projectionStatus) = TrySessionTurnFigures(sessionId, lookup);
        IReadOnlyList<ReadTimeTurnSummary> turns = figures?.Turns ?? [];

        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["sessionId"] = sessionId,
            ["turns"] = turns.Select(ToTurnDto).ToList(),
            ["context"] = BuildContextPayload(projection, figures?.FirstContextBucket, figures?.LastContextBucket, turns),
        };

        if (projectionStatus is not null)
        {
            payload["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>
    /// Returns the requests section payload — one page of the session's request rows, each with
    /// the prompt preview its page read carries — or <see langword="null"/> when the session has
    /// no projection.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>Paging envelope JSON element, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The rows answer from the content-free request figures plus one prompt-preview read scoped
    /// to exactly the page's request spans — the section's sanctioned content read — so the
    /// previews never cost the session's whole conversation.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    public JsonElement? GetSessionRequests(string sessionId, int page = 1, int limit = DefaultSectionPageLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        EnsureProjected();
        // The section 404 check reads the projection header row, not the whole projection the
        // detail route loads.
        if (projections.SessionHeadersByKeys([sessionId]).Count == 0)
        {
            return null;
        }

        (SessionRequestFigurePageResult? pageResult, string? projectionStatus) = TrySessionRequestFigures(sessionId, page, limit);
        IReadOnlyList<ReadTimeSessionRequestSummary> items = pageResult?.Items ?? [];
        Dictionary<string, string?> previews = items.Count == 0
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : reader.RequestPromptPreviews(sessionId, [.. items.Select(static request => request.RequestSpanId)])
                .ToDictionary(static preview => preview.RequestSpanId, static preview => preview.Prompt, StringComparer.Ordinal);
        List<Dictionary<string, object?>> rows = RequestRows(
        [
            .. items.Select(request => previews.TryGetValue(request.RequestSpanId, out string? preview) ? request with { PromptPreview = preview } : request),
        ]);

        Dictionary<string, object?> payload = PagingEnvelope(
            "requests",
            rows,
            pageResult?.Total ?? 0,
            limit,
            PageOffset(page, limit),
            page);
        if (projectionStatus is not null)
        {
            payload["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>
    /// Returns the reconciliation section payload — one page of the session's measurable
    /// reconciliation rows — or <see langword="null"/> when the session has no projection.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>Paging envelope JSON element, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    public JsonElement? GetSessionReconciliation(string sessionId, int page = 1, int limit = DefaultSectionPageLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        EnsureProjected();
        // The section 404 check reads the projection header row, not the whole projection the
        // detail route loads.
        if (projections.SessionHeadersByKeys([sessionId]).Count == 0)
        {
            return null;
        }

        (SessionReconciliationFigurePageResult? pageResult, string? projectionStatus) = TrySessionReconciliationFigures(sessionId, page, limit);
        List<Dictionary<string, object?>> rows = ReconciliationRows(pageResult?.Items ?? []);

        Dictionary<string, object?> payload = PagingEnvelope(
            "reconciliation",
            rows,
            pageResult?.Total ?? 0,
            limit,
            PageOffset(page, limit),
            page);
        if (projectionStatus is not null)
        {
            payload["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>
    /// Returns the problems section payload — one page of the session's stored ingest problems —
    /// or <see langword="null"/> when the session has no projection.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>Paging envelope JSON element, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    public JsonElement? GetSessionProblems(string sessionId, int page = 1, int limit = DefaultSectionPageLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        EnsureProjected();
        // The section 404 check reads the projection header row, not the whole projection the
        // detail route loads.
        if (projections.SessionHeadersByKeys([sessionId]).Count == 0)
        {
            return null;
        }

        (SessionProblemPageResult? pageResult, string? projectionStatus) = TrySessionProblems(sessionId, page, limit);
        Dictionary<string, object?> payload = PagingEnvelope(
            "problems",
            [.. (pageResult?.Items ?? []).Select(static problem => (object)problem)],
            pageResult?.Total ?? 0,
            limit,
            PageOffset(page, limit),
            page);
        if (projectionStatus is not null)
        {
            payload["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>
    /// Returns the tools section payload — one page of the session's ranked tool rows,
    /// never-invoked rows included, with the aggregate priced over every row — or
    /// <see langword="null"/> when the session has no projection.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>Paging envelope JSON element with the section's <c>aggregate</c>, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The aggregate is priced over every row the tool figures read returns, before paging, so a
    /// page never changes it: the largest resident cost, the resident cost total, the invocation
    /// count, the unused count, the unused range, the per-server rollup, the span that carries
    /// the session's tool definitions, and the schema measurability verdict with its reason.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    public JsonElement? GetSessionTools(string sessionId, int page = 1, int limit = DefaultSectionPageLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        EnsureProjected();
        // The section 404 check reads the projection header row, not the whole projection the
        // detail route loads.
        if (projections.SessionHeadersByKeys([sessionId]).Count == 0)
        {
            return null;
        }

        (SessionToolFigureSet? figures, string? projectionStatus) = TrySessionToolFigures(sessionId, pricing.LoadPricingTables());
        List<Dictionary<string, object?>> rows = figures is null
            ? []
            : ToolRows(figures.Tools, figures.ToolsOffered);
        Dictionary<string, long> byServer = new(StringComparer.Ordinal);
        if (figures is not null)
        {
            foreach (StoredSchemaServerTokens server in figures.ByServer)
            {
                byServer[server.ServerName] = server.Tokens;
            }
        }

        Dictionary<string, object?> aggregate = new(StringComparer.Ordinal)
        {
            ["maxCost"] = figures?.MaxCost,
            ["residentCostTotal"] = figures?.ResidentCostTotal ?? 0L,
            ["invocationCount"] = figures?.InvocationCount ?? 0,
            ["unusedCount"] = figures?.UnusedCount ?? 0,
            ["unusedRange"] = figures?.UnusedRange is UnusedSchemaRange range
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tokenResidencies"] = range.TokenResidencies,
                    ["floor"] = range.Floor,
                    ["ceiling"] = range.Ceiling,
                    ["currency"] = range.Currency,
                }
                : null,
            ["byServer"] = byServer,
            ["definitionsSpanId"] = figures?.DefinitionsSpanId,
        };

        // The measurability verdict is the engine's, not the page's: measurable exactly when the
        // stored projection carries a schema — the verdict the summary payload states — with the
        // stored reason, or the honest fallback when none was stored. Invocation rows alone do
        // not make a ranking measurable.
        bool schemaMeasurable = figures?.SchemaMeasurable ?? false;
        aggregate["measurable"] = schemaMeasurable;
        aggregate["reason"] = schemaMeasurable
            ? null
            : !string.IsNullOrWhiteSpace(figures?.SchemaUnmeasurableReason)
                ? figures.SchemaUnmeasurableReason
                : "Schema residency is not measurable because tool definitions were not exported.";

        Dictionary<string, object?> payload = PagingEnvelope(
            "tools",
            PageOf(rows, page, limit),
            rows.Count,
            limit,
            PageOffset(page, limit),
            page);
        payload["aggregate"] = aggregate;
        if (projectionStatus is not null)
        {
            payload["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>
    /// Returns the servers section payload — one page of the session's per-server schema rollup
    /// rows — or <see langword="null"/> when the session has no projection.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>Paging envelope JSON element, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    public JsonElement? GetSessionServers(string sessionId, int page = 1, int limit = DefaultSectionPageLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        EnsureProjected();
        // The section 404 check reads the projection header row, not the whole projection the
        // detail route loads.
        if (projections.SessionHeadersByKeys([sessionId]).Count == 0)
        {
            return null;
        }

        (SessionToolFigureSet? figures, string? projectionStatus) = TrySessionToolFigures(sessionId, pricing.LoadPricingTables());
        List<Dictionary<string, object?>> rows = figures is null
            ? []
            : ServerRows(figures.Tools, figures.Servers);

        Dictionary<string, object?> payload = PagingEnvelope(
            "servers",
            PageOf(rows, page, limit),
            rows.Count,
            limit,
            PageOffset(page, limit),
            page);
        if (projectionStatus is not null)
        {
            payload["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>
    /// Returns the skills section payload — one page of the session's distinct skill rows — or
    /// <see langword="null"/> when the session has no projection.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>The skills payload the detail route carries, with its rows paged, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The rows answer from the stored skill invocations alone; the session's harness identity
    /// names the not-measurable block the rows carry, the one read past the skill table the
    /// section pays.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    public JsonElement? GetSessionSkills(string sessionId, int page = 1, int limit = DefaultSectionPageLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        EnsureProjected();
        // The section 404 check reads the projection header row, not the whole projection the
        // detail route loads.
        if (projections.SessionHeadersByKeys([sessionId]).Count == 0)
        {
            return null;
        }

        (IReadOnlyList<StoredSkillInvocation> skillRows, string? projectionStatus) = TrySessionSkillFigures(sessionId);
        (IReadOnlyList<string> distinctSkills, IReadOnlyDictionary<string, int> skillCounts) = CollectSkillCounts(skillRows);
        CollectedSession? session = reader.GetSession(sessionId);
        string harness = session?.HarnessId ?? string.Empty;
        Dictionary<string, object?> payload = BuildSkillsPayload(distinctSkills, skillCounts, harness);
        if (payload.TryGetValue("rows", out object? rowsValue) && rowsValue is List<Dictionary<string, object?>> allRows)
        {
            payload["rows"] = PageOf(allRows, page, limit);
            payload["total"] = allRows.Count;
        }
        else
        {
            payload["rows"] = new List<Dictionary<string, object?>>();
            payload["total"] = 0;
        }

        payload["limit"] = limit;
        payload["offset"] = PageOffset(page, limit);
        payload["page"] = page;
        if (projectionStatus is not null)
        {
            payload["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>
    /// Returns one page of one level of the session's timeline tree: the top level when no
    /// parent is named, or the children of the node whose span id <paramref name="parentNodeId"/>
    /// names — a synthetic container id such as <c>(auxiliary)</c> or <c>(orphan:...)</c> included.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="parentNodeId">Span id of the node whose children the page lists; the top level when null or empty.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Nodes on the page; 25 unless given.</param>
    /// <returns>
    /// The page's nodes — today's timeline node fields minus <c>children</c>, plus each node's
    /// <c>childCount</c> — with the level's total and the whole tree's bounds, or
    /// <see langword="null"/> when the session or the named parent is not in the tree.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <c>BuildTimeline</c> shapes its tree in memory — synthetic grouping nodes included — so
    /// the level is located in the tree rather than paged out of the store. The per-request cost
    /// is the session's metadata tree, read content-free through
    /// <see cref="IRelationalStoreReader.SessionTimelineRecords"/>, never the session's content
    /// (decision D17).
    /// </para>
    /// <para>
    /// Siblings keep <c>BuildTimeline</c>'s order across pages, so walking every parent's pages
    /// yields today's tree with no node lost or duplicated.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    public JsonElement? GetSessionTimeline(
        string sessionId,
        string? parentNodeId = null,
        int page = 1,
        int limit = DefaultSectionPageLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        EnsureProjected();
        // The section 404 check reads the projection header row, not the whole projection the
        // detail route loads.
        if (projections.SessionHeadersByKeys([sessionId]).Count == 0)
        {
            return null;
        }

        (TimelineNode? root, string? projectionStatus) = TrySessionTimelineTree(sessionId);
        IEnumerable<TimelineNode> level = [];
        if (root is not null)
        {
            if (string.IsNullOrEmpty(parentNodeId))
            {
                level = root.Children;
            }
            else
            {
                TimelineNode? parent = FindTimelineNode(root, parentNodeId);
                if (parent is null)
                {
                    // A parent no node names is absent, not empty: an empty level would read as a
                    // real node with no children, and the caller could not tell the difference.
                    return null;
                }

                level = parent.Children;
            }
        }

        List<Dictionary<string, object?>> rows = [.. level.Select(TimelineRow)];
        (long startMs, long endMs) = root is null ? (0, 0) : TimelineTreeBounds(root);
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["sessionId"] = sessionId,
            ["parent"] = string.IsNullOrEmpty(parentNodeId) ? null : parentNodeId,
            ["nodes"] = PageOf(rows, page, limit),
            ["total"] = rows.Count,
            ["limit"] = limit,
            ["offset"] = PageOffset(page, limit),
            ["page"] = page,
            ["bounds"] = new { startMs, endMs },
        };

        if (projectionStatus is not null)
        {
            payload["projection_status"] = projectionStatus;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>Slices one page out of rows the section read already carries in memory.</summary>
    private static List<Dictionary<string, object?>> PageOf(IReadOnlyList<Dictionary<string, object?>> rows, int page, int limit)
    {
        int offset = PageOffset(page, limit);
        if (offset >= rows.Count)
        {
            return [];
        }

        List<Dictionary<string, object?>> pageRows = new(Math.Min(limit, rows.Count - offset));
        for (int index = offset; index < Math.Min(offset + limit, rows.Count); index++)
        {
            pageRows.Add(rows[index]);
        }

        return pageRows;
    }

    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="turnIndex">Zero-based turn index.</param>
    /// <returns>Turn figures JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetTurnFigures(string sessionId, int turnIndex)
    {
        EnsureProjected();
        // The section 404 check reads the projection header row, not the whole projection the
        // stored context turn below scopes to.
        if (projections.SessionHeadersByKeys([sessionId]).Count == 0)
        {
            return null;
        }

        var (turn, projectionStatus) = TryTurnFigures(sessionId, turnIndex);
        if (turn is null)
        {
            // A faulted read answers the section routes' PROJECTION_FAILED convention — an answer,
            // not the 404 that would misread a broken projection as a missing turn.
            return projectionStatus is not null
                ? ProjectionFailedTurnPayload(sessionId, turnIndex)
                : null;
        }

        StoredSessionContextTurn? storedContextTurn = projections
            .GetSessionProjection(sessionId)?
            .Context?
            .Turns
            .FirstOrDefault(storedTurn => storedTurn.Position == turnIndex);

        return JsonSerializer.SerializeToElement(new
        {
            sessionId,
            turnIndex,
            turn = ToTurnDto(turn),
            context = storedContextTurn is null ? null : BuildStoredContextTurn(storedContextTurn),
        }, CamelCaseJsonOptions);
    }

    /// <summary>
    /// Returns bounded or full text/parts content for a single turn. The payload names the turn's
    /// span (<c>spanId</c>) and model (omitted when the turn recorded none), links the span's raw
    /// packet through <c>rawPacket.spanAttributesPath</c>, and flags <c>truncated</c> by counting
    /// scoped parts: true only when the budgeted parts read left at least one part behind, and
    /// never when <paramref name="full"/> is true. Blocks, part labels, and assembled text are
    /// built by the web parser from the served parts, so no text is duplicated on the wire.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="turnIndex">Zero-based turn index.</param>
    /// <param name="budget">Byte budget applied when <paramref name="full"/> is false.</param>
    /// <param name="full">Whether to return the full payload untruncated.</param>
    /// <returns>Turn content JSON element, or <see langword="null"/>.</returns>
    public JsonElement? GetTurnContent(string sessionId, int turnIndex, int budget = ContentResponseBudget, bool full = false)
    {
        EnsureProjected();
        (ReadTimeTurnSummary? figures, string? projectionStatus) = TryTurnFigures(sessionId, turnIndex);
        if (figures is null)
        {
            // Same convention as the figures route: a faulted read answers PROJECTION_FAILED; only
            // an index outside the session answers null.
            return projectionStatus is not null
                ? ProjectionFailedTurnPayload(sessionId, turnIndex)
                : null;
        }

        int effectiveBudget = budget > 0 ? budget : ContentResponseBudget;
        List<Dictionary<string, object?>> parts = [.. reader
            .TurnContentParts(sessionId, turnIndex: turnIndex, budget: effectiveBudget, full: full)
            .Select(ToTurnContentEntry)];
        Dictionary<string, object?> turn = ToTurnDto(figures);
        string serialized = JsonSerializer.Serialize(turn, CamelCaseJsonOptions);
        int totalBytes = Encoding.UTF8.GetByteCount(serialized);
        bool contentSliced = !full && totalBytes > effectiveBudget;
        string slice = contentSliced ? TruncateUtf8(serialized, effectiveBudget) : serialized;
        // The web's `truncated` counts scoped parts, not bytes: the budgeted read left content
        // behind exactly when the scope holds more parts than it returned. The lossless full
        // read answers every part, so it never needs the count.
        bool truncated = !full && parts.Count < reader.TurnContentPartCount(sessionId, turnIndex: turnIndex);

        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["sessionId"] = sessionId,
            ["turnIndex"] = turnIndex,
            ["spanId"] = figures.SpanId,
            ["rawPacket"] = new { spanAttributesPath = $"/api/kyber/span/{figures.SpanId}/attributes" },
            ["content"] = contentSliced ? slice : null,
            ["turn"] = contentSliced ? null : turn,
            ["truncated"] = truncated,
            ["budget"] = effectiveBudget,
            ["totalBytes"] = totalBytes,
            ["parts"] = parts,
        };
        if (figures.Model is string model)
        {
            payload["model"] = model;
        }

        return JsonSerializer.SerializeToElement(payload, CamelCaseJsonOptions);
    }

    /// <summary>Returns bounded or full session content optionally filtered by turn, span, or canonical part key.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="turnIndex">Optional zero-based turn filter.</param>
    /// <param name="spanId">Optional span identifier filter.</param>
    /// <param name="part">Optional canonical content part key.</param>
    /// <param name="full">Whether to omit truncation preview slicing.</param>
    /// <returns>Session content JSON element, or <see langword="null"/> when the session is absent.</returns>
    /// <remarks>
    /// The one request path that materializes conversation text, and it pays only for the turn or
    /// request it was asked for: the parts come from the scoped content read, while the span
    /// order, the emitted index, and the advertised tool names come from the session's
    /// content-free record metadata.
    /// </remarks>
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

        List<CanonicalRecord> metadata = [.. reader.SessionRecordsContentFree(sessionId).Select(CanonRecordHydration.Hydrate)];
        metadata.Sort(CompareRecords);

        // A span filter narrows the record sequence first, so the turn filter then counts inside
        // it — the shape the content reader has always used. Without a span filter the turn index
        // resolves against the session's turn figures, the same turn the summary priced.
        string? scopedSpan = spanId;
        if (string.IsNullOrEmpty(scopedSpan) && turnIndex.HasValue)
        {
            (ReadTimeTurnSummary? turn, string? projectionStatus) = TryTurnFigures(sessionId, turnIndex.Value);
            scopedSpan = turn?.SpanId;
            if (turn is null)
            {
                // The turn index resolves through the figures, so with no figures there is no turn
                // to read: an out-of-range index answers 404, and a faulted read answers the
                // section routes' PROJECTION_FAILED convention. Either way the unscoped
                // whole-session content pass must not run — its loop would pick the Nth record by
                // index, not turn N.
                return projectionStatus is not null
                    ? ProjectionFailedContentPayload(sessionId)
                    : null;
            }
        }

        List<CanonicalRecord> selected = string.IsNullOrEmpty(scopedSpan)
            ? metadata
            : [.. metadata.Where(record => string.Equals(record.SpanId, scopedSpan, StringComparison.Ordinal))];
        bool turnFilterApplies = turnIndex.HasValue && (!string.IsNullOrEmpty(spanId) || string.IsNullOrEmpty(scopedSpan));

        IReadOnlyList<StoredContentPart> scopedParts = selected.Count == 0
            ? []
            : reader.TurnContentParts(
                sessionId,
                spanId: string.IsNullOrEmpty(scopedSpan) ? null : scopedSpan,
                part: string.IsNullOrEmpty(part) ? null : part,
                budget: ContentResponseBudget,
                full: full);

        Dictionary<string, List<CollectedContentPart>> partsBySpan = new(StringComparer.Ordinal);
        foreach (StoredContentPart scopedPart in scopedParts)
        {
            if (!partsBySpan.TryGetValue(scopedPart.SpanId, out List<CollectedContentPart>? spanParts))
            {
                spanParts = [];
                partsBySpan[scopedPart.SpanId] = spanParts;
            }

            spanParts.Add(scopedPart.Part);
        }

        List<object> chunks = [];
        int index = 0;
        foreach (CanonicalRecord record in selected)
        {
            if (turnFilterApplies && index != turnIndex!.Value)
            {
                index++;
                continue;
            }

            partsBySpan.TryGetValue(record.SpanId, out List<CollectedContentPart>? captured);
            if (!string.IsNullOrEmpty(part))
            {
                // A part the capture did not record still answers an empty entry, and a part the
                // content-free metadata carries — the advertised tool names — answers from it.
                string partVal = captured is not null
                    ? string.Join("\n", captured.Select(static p => p.PlainText))
                    : string.Join("\n", (record.Parts ?? []).Where(p => string.Equals(p.Part, part, StringComparison.Ordinal)).Select(p => p.Text));
                chunks.Add(new { spanId = record.SpanId, index, part, text = partVal });
            }
            else
            {
                // One flat entry per captured part, the shape the part-filtered branch and the
                // dashboard's content reader both use. A span that captured nothing contributes
                // no entry, rather than a part-less wrapper the reader cannot render.
                foreach (CollectedContentPart source in captured ?? [])
                {
                    chunks.Add(ToContentEntry(record.SpanId, index, source));
                }

                // Hydration of a content-free record leaves its advertised tool names as parts,
                // ordered after the captured parts, so the payload keeps the entries it had.
                foreach (ContentPart advertised in record.Parts ?? [])
                {
                    Dictionary<string, object> entry = new(StringComparer.Ordinal)
                    {
                        ["spanId"] = record.SpanId,
                        ["index"] = index,
                        ["part"] = advertised.Part,
                        ["text"] = advertised.Text,
                    };
                    if (advertised.Tokens is long schemaTokens) entry["tokens"] = schemaTokens;
                    if (advertised.Server is string server) entry["server"] = server;
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
    /// attributes a hydrated record still names (declared context window, model, agent). A
    /// consent-allowed Droid prompt is conversation text, so the inspector reads that one
    /// span's prompt part through a span-scoped read rather than the session's records.
    /// </remarks>
    public JsonElement? GetSpanAttributes(string spanId)
    {
        CollectedRecord? record = reader.RecordContentFree(spanId);
        if (record is null)
        {
            return null;
        }

        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(record);
        if (string.Equals(record.HarnessId, "droid", StringComparison.OrdinalIgnoreCase)
            && hydrated.Raw is Dictionary<string, JsonElement> raw
            && reader.SpanPromptText(spanId) is string prompt)
        {
            raw[FactoryKeys.PromptText] = JsonSerializer.SerializeToElement(prompt);
        }

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

        Dictionary<string, object?> envelope = PagingEnvelope("findings", pageItems, total, safeLimit, safeOffset, page);
        envelope["detectorCounts"] = detectorCounts;
        envelope["unknownWindowSessions"] = 0;
        return JsonSerializer.SerializeToElement(envelope, CamelCaseJsonOptions);
    }

    /// <summary>
    /// The envelope every paged read answers: the page's rows under their own key and again
    /// under <c>data</c>, then the number of rows the filters match before paging and the window
    /// that was read.
    /// </summary>
    /// <param name="itemsKey">Name of the member the rows are published under.</param>
    /// <param name="items">The page's rows, in page order.</param>
    /// <param name="total">Number of rows the filters match, before paging.</param>
    /// <param name="limit">Rows per page.</param>
    /// <param name="offset">Zero-based offset of the page's first row.</param>
    /// <param name="page">One-based page number.</param>
    /// <returns>The envelope's members, in serialization order.</returns>
    private static Dictionary<string, object?> PagingEnvelope(string itemsKey, IReadOnlyList<object> items, int total, int limit, int offset, int page)
    {
        ArgumentNullException.ThrowIfNull(itemsKey);
        ArgumentNullException.ThrowIfNull(items);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [itemsKey] = items,
            ["data"] = items,
            ["total"] = total,
            ["limit"] = limit,
            ["offset"] = offset,
            ["page"] = page,
        };
    }

    /// <summary>
    /// The zero-based offset of a page's first row. The product is taken in 64 bits: page ×
    /// limit overflows a 32-bit integer for an absurd page number, which then reads as a page
    /// past the end instead of wrapping onto a different page.
    /// </summary>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows per page.</param>
    /// <returns>The offset, clamped to the largest 32-bit integer.</returns>
    private static int PageOffset(int page, int limit) => (int)Math.Clamp(((long)page - 1) * limit, 0L, int.MaxValue);

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
        Dictionary<string, SessionListFigure> figuresById = BuildFigureIndex(lookup);

        List<object> mappedRuns = new(runRows.Count);
        foreach (StoredRun run in runRows)
        {
            IReadOnlyList<StoredExecution> execs = projections.ListExecutionsForRun(run.RunId);
            RunSessionFigures figures = SummarizeRunSessions(execs, figuresById);
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
        Dictionary<string, SessionListFigure> figuresById = BuildFigureIndex(lookup);

        RunSessionFigures figures = SummarizeRunSessions(executions, figuresById);
        List<object> enrichedExecutions = new(executions.Count);
        foreach (StoredExecution exec in executions)
        {
            (int? TurnCount, double? CostUsd) sessionFigures = SessionFiguresOf(exec, figuresById);
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

        List<object> executionTree = BuildExecutionTree(executions, figuresById);
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
    /// <returns>The phase-aligned comparison payload, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The payload is <see cref="CanonRunComparison"/> over each run's resolved turns, so the
    /// token totals, phase alignment, deltas, and verdict a surface renders are engine decisions.
    /// </remarks>
    public JsonElement? GetCompareRuns(string runA, string runB)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runA);
        ArgumentException.ThrowIfNullOrWhiteSpace(runB);

        EnsureProjected();
        StoredRun? runARow = projections.GetRun(runA);
        StoredRun? runBRow = projections.GetRun(runB);
        if (runARow is null || runBRow is null)
        {
            return null;
        }

        IModelPricingLookup lookup = pricing.LoadPricingTables();
        RunComparisonRun sideA = ResolveComparisonRun(runARow, lookup);
        RunComparisonRun sideB = ResolveComparisonRun(runBRow, lookup);

        return CanonRunComparison.Compare(sideA, sideB);
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

        return JsonSerializer.SerializeToElement(PagingEnvelope("entries", entries, total, limit, offset, page), CamelCaseJsonOptions);
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

        return JsonSerializer.SerializeToElement(PagingEnvelope("problems", problems, total, limit, offset, page), CamelCaseJsonOptions);
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
        SettingsView settingsSnapshot = operationalStore.ReadSettings();
        bool receiverEnabled = settingsSnapshot.ReceiverHosted;
        bool jobsPaused = settingsSnapshot.JobsPaused;
        bool collectorPaused = collector?.IsPaused == true;
        bool isPaused = jobsPaused || collectorPaused;
        bool? listening = listener is null ? null : listener.IsListening;
        bool? hostedByUs = listener is null ? null : listener.IsListening;
        int? boundOrConfiguredPort = listener?.BoundPort
            ?? (receiverEnabled ? dashOptions?.OtlpPort : null);
        string? endpoint = boundOrConfiguredPort is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{boundOrConfiguredPort.Value}")
            : null;
        int? failures5m = collector?.CountFailuresSince(clock().Subtract(TimeSpan.FromMinutes(5)));
        string? checkedAt = listening == true
            ? clock().UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)
            : null;
        bool healthy = listening == true && !isPaused && failures5m == 0;
        string status = listening == true
            ? isPaused ? "paused" : "listening"
            : receiverEnabled ? "unknown" : "disabled";
        object receiver = new
        {
            enabled = receiverEnabled,
            endpoint,
            status,
            listening,
            hostedByUs,
            paused = isPaused,
            checkedAt,
            lastAcceptedAt = collector?.LastAcceptedAt,
            failures5m,
            lastError = collector?.LastError,
            lastCode = collector?.LastErrorCode,
            lastErrorAt = collector?.LastErrorAt,
            healthy,
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

    /// <summary>Builds a bounded diagnostic report for <c>/api/kyber/report</c> using the default 7-day window.</summary>
    /// <param name="harness">Optional harness filter.</param>
    /// <param name="sessionId">Optional session filter.</param>
    /// <param name="runId">Optional run filter.</param>
    /// <param name="limit">Maximum findings included (clamped to 1..200).</param>
    /// <returns>Diagnostic report JSON element.</returns>
    public JsonElement GetReport(string? harness = null, string? sessionId = null, string? runId = null, int limit = 50) =>
        GetReport(harness, sessionId, runId, limit, days: 7);

    /// <summary>Builds a bounded <c>ContextReport</c> diagnostic payload scoped to <paramref name="days"/>.</summary>
    /// <param name="harness">Optional harness filter.</param>
    /// <param name="sessionId">Optional session filter.</param>
    /// <param name="runId">Optional run filter.</param>
    /// <param name="limit">Maximum findings included (clamped to 1..200).</param>
    /// <param name="days">Positive lookback window in whole days.</param>
    /// <returns>Diagnostic report JSON element.</returns>
    public JsonElement GetReport(string? harness, string? sessionId, string? runId, int limit, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);
        EnsureProjected();

        int boundedLimit = Math.Clamp(limit, 1, 200);
        string? normalizedHarness = NormalizeHarnessFilter(harness);
        string? normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        string? normalizedRunId = string.IsNullOrWhiteSpace(runId) ? null : runId.Trim();

        DateTimeOffset now = clock();
        string generatedAt = now.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        DateTimeOffset cutoff = days >= (now - DateTimeOffset.MinValue).TotalDays
            ? DateTimeOffset.MinValue
            : now - TimeSpan.FromDays(days);

        IReadOnlyList<CollectedSession> storeSessions = reader.Sessions();
        IReadOnlyList<StoredSessionProjection> allProjections = projections.ListSessionProjections();
        IReadOnlyList<StoredRun> allRuns = projections.ListRuns();
        IReadOnlyList<StoredHarnessRollup> storedRollups = projections.ListHarnessRollups();

        Dictionary<string, StoredSessionProjection> projectionById = new(allProjections.Count, StringComparer.Ordinal);
        foreach (StoredSessionProjection projection in allProjections)
        {
            projectionById[projection.SessionId] = projection;
        }

        Dictionary<string, string> sessionHarnessById = new(storeSessions.Count, StringComparer.Ordinal);
        Dictionary<string, int> totalHarnessCounts = new(StringComparer.Ordinal);
        List<(CollectedSession Session, StoredSessionProjection Projection, string Harness)> projectedSessions =
            new(storeSessions.Count);
        foreach (CollectedSession session in storeSessions)
        {
            string? canonicalHarness = CanonPricing.CanonicalHarnessId(session.HarnessId);
            if (canonicalHarness is null)
            {
                continue;
            }

            if (!projectionById.TryGetValue(session.SessionKey, out StoredSessionProjection? projection))
            {
                continue;
            }

            sessionHarnessById[session.SessionKey] = canonicalHarness;
            totalHarnessCounts[canonicalHarness] = totalHarnessCounts.GetValueOrDefault(canonicalHarness) + 1;
            projectedSessions.Add((session, projection, canonicalHarness));
        }

        Dictionary<string, string> runHarnessById = new(allRuns.Count, StringComparer.Ordinal);
        HashSet<string> inWindowRunIds = new(StringComparer.Ordinal);
        foreach (StoredRun run in allRuns)
        {
            string? canonicalRunHarness = CanonPricing.CanonicalHarnessId(run.HarnessId);
            if (canonicalRunHarness is null)
            {
                continue;
            }

            runHarnessById[run.RunId] = canonicalRunHarness;
            if (IsRunInWindow(run, cutoff))
            {
                _ = inWindowRunIds.Add(run.RunId);
            }
        }

        HashSet<string>? runSessionIds = null;
        if (normalizedRunId is not null)
        {
            runSessionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (StoredExecution execution in projections.ListExecutionsForRun(normalizedRunId))
            {
                if (!string.IsNullOrEmpty(execution.SessionId))
                {
                    _ = runSessionIds.Add(execution.SessionId);
                }
            }
        }

        List<ReportSessionEntry> scopedSessions = [];
        Dictionary<string, int> inWindowHarnessCounts = new(StringComparer.Ordinal);
        HashSet<string> inWindowSessionIds = new(StringComparer.Ordinal);

        foreach ((CollectedSession session, StoredSessionProjection projection, string sessionHarness) in projectedSessions)
        {
            if (!IsSessionInWindow(projection, cutoff, out DateTimeOffset? activityTime))
            {
                continue;
            }

            ReportSessionEntry entry = new(session, projection, sessionHarness, activityTime);
            _ = inWindowSessionIds.Add(session.SessionKey);
            inWindowHarnessCounts[sessionHarness] = inWindowHarnessCounts.GetValueOrDefault(sessionHarness) + 1;

            if (normalizedHarness is not null
                && !string.Equals(sessionHarness, normalizedHarness, StringComparison.Ordinal))
            {
                continue;
            }

            if (normalizedSessionId is not null
                && !string.Equals(session.SessionKey, normalizedSessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (runSessionIds is not null && !runSessionIds.Contains(session.SessionKey))
            {
                continue;
            }

            scopedSessions.Add(entry);
        }

        IModelPricingLookup pricingLookup = pricing.LoadPricingTables();
        // The report prices its whole scoped window with one keyed batched read over the
        // content-free report figures, so a report never materializes conversation text and its
        // statement count never scales with the window (decision D13).
        Dictionary<string, SessionReportFigure> aggregatesById = new(StringComparer.Ordinal);
        try
        {
            foreach (SessionReportFigure figure in reader.SessionReportFigures(
                [.. scopedSessions.Select(static entry => entry.Session.SessionKey)],
                pricingLookup))
            {
                aggregatesById[figure.SessionKey] = figure;
            }
        }
        catch (Exception ex)
        {
            // The window's figures failed as one batch, so every scoped session carries the
            // isolation marker, exactly as each session did when its own read failed.
            foreach (ReportSessionEntry entry in scopedSessions)
            {
                RecordProjectionFailure(entry.Session.SessionKey, ex.Message);
            }
        }

        Dictionary<string, StoredHarnessRollup> storedRollupsByHarness = new(StringComparer.Ordinal);
        foreach (StoredHarnessRollup rollup in storedRollups)
        {
            string? canonicalHarness = CanonPricing.CanonicalHarnessId(rollup.HarnessId);
            if (canonicalHarness is null || !totalHarnessCounts.ContainsKey(canonicalHarness))
            {
                continue;
            }

            storedRollupsByHarness[canonicalHarness] = rollup;
        }

        List<StoredFinding> candidateFindings =
            [.. projections.ListFindings(sessionId: normalizedSessionId, runId: normalizedRunId)];
        List<(StoredFinding Finding, string? Harness)> scopedFindings = [];
        foreach (StoredFinding finding in candidateFindings)
        {
            string? resolvedHarness = ResolveFindingHarness(finding, sessionHarnessById, runHarnessById);
            if (normalizedHarness is not null
                && !string.Equals(resolvedHarness, normalizedHarness, StringComparison.Ordinal))
            {
                continue;
            }

            bool findingInWindow = finding.SessionId is not null
                ? inWindowSessionIds.Contains(finding.SessionId)
                : finding.RunId is null || inWindowRunIds.Contains(finding.RunId);
            if (!findingInWindow)
            {
                continue;
            }

            scopedFindings.Add((finding, resolvedHarness));
        }

        scopedFindings.Sort(static (left, right) =>
        {
            int rankComparison = right.Finding.RankScore.CompareTo(left.Finding.RankScore);
            return rankComparison != 0
                ? rankComparison
                : string.Compare(left.Finding.FindingId, right.Finding.FindingId, StringComparison.Ordinal);
        });

        List<object> projectedFindings = new(Math.Min(boundedLimit, scopedFindings.Count));
        foreach ((StoredFinding finding, string? resolvedHarness) in scopedFindings.Take(boundedLimit))
        {
            projectedFindings.Add(BuildReportFindingDto(finding, resolvedHarness));
        }

        List<object> reportHarnesses = BuildReportHarnesses(
            scopedSessions,
            totalHarnessCounts,
            inWindowHarnessCounts,
            storedRollupsByHarness,
            aggregatesById,
            allRuns,
            cutoff,
            normalizedSessionId is null && normalizedRunId is null);

        object? latestSession = BuildReportLatestSession(scopedSessions, generatedAt, pricingLookup);
        List<object> reportCost = BuildReportCost(scopedSessions, aggregatesById);

        RefreshCoverageState refreshState = BuildRefreshCoverageState();
        List<object> coverageHarnesses = BuildReportCoverageHarnesses(
            storedRollupsByHarness,
            totalHarnessCounts,
            inWindowHarnessCounts);
        List<string> coverageHints = BuildReportCoverageHints(scopedSessions.Count, refreshState);

        Dictionary<string, object?> scopePayload = new(StringComparer.Ordinal)
        {
            ["days"] = days,
        };
        if (normalizedHarness is not null)
        {
            scopePayload["harness"] = normalizedHarness;
        }

        if (normalizedSessionId is not null)
        {
            scopePayload["sessionId"] = normalizedSessionId;
        }

        if (normalizedRunId is not null)
        {
            scopePayload["runId"] = normalizedRunId;
        }

        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = ApiContractVersion,
            generatedAt,
            kyberdashVersion = version,
            storeGeneration = operationalStore.GetWriteMarker(),
            sessionCount = scopedSessions.Count,
            findingCount = scopedFindings.Count,
            harnessCount = reportHarnesses.Count,
            scope = scopePayload,
            coverage = new
            {
                storePath,
                refresh = refreshState.Payload,
                harnesses = coverageHarnesses,
                quarantineCount = operationalStore.CountQuarantinedSpans(normalizedHarness),
                problemCount = operationalStore.CountIngestProblems(normalizedHarness),
                hints = coverageHints,
            },
            findings = projectedFindings,
            harnesses = reportHarnesses,
            latestSession,
            cost = reportCost,
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
            List<CanonicalRecord> records = [.. reader.SessionRecordsContentFree(session.SessionKey).Select(CanonRecordHydration.Hydrate)];
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

    /// <summary>
    /// Returns the content-free stored-fields inventory for a session, or <see langword="null"/>
    /// if missing.
    /// </summary>
    /// <param name="sessionKey">Canonical session identifier.</param>
    /// <returns>Stored-fields inventory JSON element, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The stored-fields section loads this inventory at mount, and it carries no row value: rows
    /// hold stored column values, including content text, so they load per table through
    /// <see cref="GetStoredFieldRows"/> only when the viewer opens one (decision D15).
    /// </remarks>
    public JsonElement? GetStoredFieldsInventory(string sessionKey)
    {
        EnsureProjected();
        if (reader.GetSession(sessionKey) is null)
        {
            return null;
        }

        StoredFieldsInventoryView view = reader.StoredFieldsInventory(sessionKey);
        return JsonSerializer.SerializeToElement(view, CamelCaseJsonOptions);
    }

    /// <summary>
    /// Returns one page of a single catalog table's stored rows for a session, or
    /// <see langword="null"/> when the session or the table is missing.
    /// </summary>
    /// <param name="sessionKey">Canonical session identifier.</param>
    /// <param name="table">Exact catalog table name; the allowlist is the catalog.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    public JsonElement? GetStoredFieldRows(string sessionKey, string table, int page = 1, int limit = DefaultSectionPageLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        EnsureProjected();
        if (reader.GetSession(sessionKey) is null)
        {
            return null;
        }

        StoredTableRows? rows = reader.StoredFieldsRows(sessionKey, table, page, limit);
        return rows is null ? null : JsonSerializer.SerializeToElement(rows, CamelCaseJsonOptions);
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

        // The grouped analytics answer names session keys and harnesses only, so it reads the
        // content-free session rows instead of pricing any session's figures: one row per
        // session key under the session's own harness, so a session whose records span two
        // harness ids (a harness renamed mid-life) still counts once, matching the
        // pre-migration per-session aggregate rows.
        List<CollectedSession> analyticsSessions = [.. reader.Sessions(normalizedFilter)];

        if (string.Equals(normalizedOp, "outcomes", StringComparison.Ordinal))
        {
            // The relational schema keeps no outcome rows, so an outcome stays not-measurable
            // rather than being guessed from unrelated telemetry.
            List<object> outcomes = [.. analyticsSessions.Select(session => (object)new
            {
                sessionId = session.SessionKey,
                harness = session.HarnessId,
                outcome = "not_measurable",
            })];
            return (200, BuildAnalyticsEnvelope(defaultScope, new { items = outcomes }, new { }, freshness, new { returnedCount = outcomes.Count, nextCursor = (string?)null }, null, null, null));
        }

        // operation == "query"
        List<object> groups = [.. analyticsSessions
            .GroupBy(session => session.HarnessId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (object)new
            {
                key = new { harness = g.Key },
                sessions = g.Select(session => session.SessionKey).Distinct(StringComparer.Ordinal).Count(),
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

    /// <summary>Reads a session's bounded summary figures, isolating any fault to that session.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="lookup">Read-time pricing tables.</param>
    /// <returns>The figures when readable, and the isolation marker when the read failed.</returns>
    private (SessionSummaryFigure? Figures, string? ProjectionStatus) ReadSessionSummaryFigures(
        string sessionKey,
        IModelPricingLookup lookup)
    {
        try
        {
            return (reader.SessionSummaryFigures(sessionKey, lookup), null);
        }
        catch (Exception ex) when (IsSessionReadFault(ex))
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            return (null, ProjectionFailedStatus);
        }
    }

    /// <summary>
    /// Whether an exception from one session's section read is that session's fault, to isolate
    /// behind <see cref="ProjectionFailedStatus"/>, rather than the host's: out-of-memory, stack
    /// exhaustion and a cancelled request propagate, so an unhealthy process or an aborted request
    /// is never reported as one broken projection.
    /// </summary>
    /// <param name="ex">The exception the read threw.</param>
    /// <returns><see langword="true"/> when the fault belongs to the session's projection.</returns>
    private static bool IsSessionReadFault(Exception ex) =>
        ex is not (OutOfMemoryException or InsufficientExecutionStackException or OperationCanceledException);

    private (ReadTimeTurnSummary? Figures, string? ProjectionStatus) TryTurnFigures(string sessionKey, int turnIndex)
    {
        try
        {
            return (reader.TurnFigures(sessionKey, turnIndex, pricing.LoadPricingTables()), null);
        }
        catch (Exception ex) when (IsSessionReadFault(ex))
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            return (null, ProjectionFailedStatus);
        }
    }

    /// <summary>
    /// The payload the content route answers when the requested turn's figures read faulted: the
    /// session identity plus the section routes' <see cref="ProjectionFailedStatus"/> marker, with
    /// no content to serve.
    /// </summary>
    private static JsonElement ProjectionFailedContentPayload(string sessionId) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sessionId"] = sessionId,
            ["projection_status"] = ProjectionFailedStatus,
            ["parts"] = Array.Empty<object>(),
            ["items"] = Array.Empty<object>(),
        }, CamelCaseJsonOptions);

    /// <summary>
    /// The payload the turn routes answer when this turn's figures read faulted: identity plus the
    /// section routes' <see cref="ProjectionFailedStatus"/> marker, with no figures to render.
    /// </summary>
    private static JsonElement ProjectionFailedTurnPayload(string sessionId, int turnIndex) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sessionId"] = sessionId,
            ["turnIndex"] = turnIndex,
            ["projection_status"] = ProjectionFailedStatus,
        }, CamelCaseJsonOptions);

    /// <summary>Reads a session's per-turn figures with its context edges, isolating any fault to that session.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="lookup">Read-time pricing tables.</param>
    /// <returns>The turn figure set when readable, and the isolation marker when the read failed.</returns>
    private (SessionTurnFigureSet? Figures, string? ProjectionStatus) TrySessionTurnFigures(
        string sessionKey,
        IModelPricingLookup lookup)
    {
        try
        {
            return (reader.SessionTurnFigures(sessionKey, lookup), null);
        }
        catch (Exception ex) when (IsSessionReadFault(ex))
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            return (null, ProjectionFailedStatus);
        }
    }

    /// <summary>Reads a page of a session's request rows, isolating any fault to that session.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>The request page when readable, and the isolation marker when the read failed.</returns>
    private (SessionRequestFigurePageResult? Page, string? ProjectionStatus) TrySessionRequestFigures(
        string sessionKey,
        int page,
        int limit)
    {
        try
        {
            return (reader.SessionRequestFigures(sessionKey, page, limit), null);
        }
        catch (Exception ex) when (IsSessionReadFault(ex))
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            return (null, ProjectionFailedStatus);
        }
    }

    /// <summary>Reads a page of a session's reconciliation rows, isolating any fault to that session.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>The reconciliation page when readable, and the isolation marker when the read failed.</returns>
    private (SessionReconciliationFigurePageResult? Page, string? ProjectionStatus) TrySessionReconciliationFigures(
        string sessionKey,
        int page,
        int limit)
    {
        try
        {
            return (reader.SessionReconciliationFigures(sessionKey, page, limit), null);
        }
        catch (Exception ex) when (IsSessionReadFault(ex))
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            return (null, ProjectionFailedStatus);
        }
    }

    /// <summary>Reads a page of a session's stored ingest problems, isolating any fault to that session.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>The problem page when readable, and the isolation marker when the read failed.</returns>
    private (SessionProblemPageResult? Page, string? ProjectionStatus) TrySessionProblems(
        string sessionKey,
        int page,
        int limit)
    {
        try
        {
            return (reader.SessionProblems(sessionKey, page, limit), null);
        }
        catch (Exception ex) when (IsSessionReadFault(ex))
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            return (null, ProjectionFailedStatus);
        }
    }

    /// <summary>Reads a session's tool and server figures, isolating any fault to that session.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="lookup">Read-time pricing tables.</param>
    /// <returns>The figures when readable, and the isolation marker when the read failed.</returns>
    private (SessionToolFigureSet? Figures, string? ProjectionStatus) TrySessionToolFigures(
        string sessionKey,
        IModelPricingLookup lookup)
    {
        try
        {
            return (reader.SessionToolFigures(sessionKey, lookup), null);
        }
        catch (Exception ex) when (IsSessionReadFault(ex))
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            return (null, ProjectionFailedStatus);
        }
    }

    /// <summary>Reads a session's stored skill invocations, isolating any fault to that session.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <returns>The skill rows when readable, empty with the isolation marker when the read failed.</returns>
    private (IReadOnlyList<StoredSkillInvocation> Rows, string? ProjectionStatus) TrySessionSkillFigures(string sessionKey)
    {
        try
        {
            return (reader.SessionSkillFigures(sessionKey), null);
        }
        catch (Exception ex) when (IsSessionReadFault(ex))
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            return ([], ProjectionFailedStatus);
        }
    }

    /// <summary>
    /// Builds the session's timeline tree from the content-free metadata read, isolating any
    /// fault to that session.
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <returns>The tree when readable, and the isolation marker when the read failed.</returns>
    private (TimelineNode? Root, string? ProjectionStatus) TrySessionTimelineTree(string sessionKey)
    {
        try
        {
            List<CanonicalRecord> records =
                [.. reader.SessionTimelineRecords(sessionKey).Select(CanonRecordHydration.Hydrate)];
            records.Sort(CompareRecords);
            return (CanonContextAnalysis.BuildTimeline(records), null);
        }
        catch (Exception ex) when (IsSessionReadFault(ex))
        {
            RecordProjectionFailure(sessionKey, ex.Message);
            return (null, ProjectionFailedStatus);
        }
    }

    /// <summary>Finds the tree node whose span id <paramref name="spanId"/> names, at any depth.</summary>
    /// <param name="root">The tree to search.</param>
    /// <param name="spanId">The exact span id, synthetic container ids included.</param>
    /// <returns>The node, or <see langword="null"/> when no node carries the id.</returns>
    private static TimelineNode? FindTimelineNode(TimelineNode root, string spanId)
    {
        if (string.Equals(root.SpanId, spanId, StringComparison.Ordinal))
        {
            return root;
        }

        foreach (TimelineNode child in root.Children)
        {
            if (FindTimelineNode(child, spanId) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>The whole tree's extent: the earliest node start and the latest node end.</summary>
    /// <param name="root">The tree to measure.</param>
    /// <returns>The earliest <c>startMs</c> and the latest <c>startMs + durationMs</c> over every node.</returns>
    private static (long StartMs, long EndMs) TimelineTreeBounds(TimelineNode root)
    {
        long start = long.MaxValue;
        long end = long.MinValue;
        void Walk(TimelineNode node)
        {
            start = Math.Min(start, node.StartMs);
            end = Math.Max(end, node.StartMs + node.DurationMs);
            foreach (TimelineNode child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(root);
        return (start == long.MaxValue ? 0 : start, end == long.MinValue ? 0 : end);
    }

    /// <summary>
    /// Maps one timeline node to the section row: today's node fields minus <c>children</c>, plus
    /// the count of children the level below it holds.
    /// </summary>
    /// <param name="node">The node to map.</param>
    /// <returns>The section row's fields.</returns>
    private static Dictionary<string, object?> TimelineRow(TimelineNode node) => new(StringComparer.Ordinal)
    {
        ["spanId"] = node.SpanId,
        ["parentId"] = node.ParentId,
        ["startMs"] = node.StartMs,
        ["durationMs"] = node.DurationMs,
        ["kind"] = node.Kind,
        ["name"] = node.Name,
        ["status"] = node.Status,
        ["attributes"] = node.Attributes,
        ["isSubagent"] = node.IsSubagent,
        ["isAuxiliary"] = node.IsAuxiliary,
        ["cost"] = node.Cost,
        ["childCount"] = node.Children.Count,
    };

    private void RecordProjectionFailure(string sessionKey, string message)
    {
        lock (projectionFailureLock)
        {
            projectionFailures[sessionKey] = message;
        }
    }

    /// <summary>
    /// Records a faulted batched figures read under the coverage payload's batch slot. Batch
    /// faults carry no session, so they never enter the per-session failure map: an empty-key
    /// entry would surface as a failed session no operator can resolve.
    /// </summary>
    /// <param name="message">Fault message from the batched read.</param>
    private void RecordBatchedReadFault(string message)
    {
        lock (projectionFailureLock)
        {
            lastBatchedReadFault = message;
        }
    }

    /// <summary>Clears the batched-read fault once a batched read succeeds again.</summary>
    private void ClearBatchedReadFault()
    {
        lock (projectionFailureLock)
        {
            lastBatchedReadFault = null;
        }
    }

    private object BuildProjectionHealth()
    {
        string? lastError;
        string? lastBatchReadFault;
        List<string> failedSessions;
        lock (projectionFailureLock)
        {
            lastError = lastProjectionPassFault ?? (projectionFailures.Count > 0 ? projectionFailures.Values.First() : null);
            lastBatchReadFault = lastBatchedReadFault;
            failedSessions = [.. projectionFailures.Keys.Order(StringComparer.Ordinal)];
        }

        // A read-path pass writes deferred keys back as still dirty, so the dirty state names
        // exactly the sessions whose projections lag the store; a wildcard dirty state carries
        // no keys and is reported by the marker instead of this list.
        DirtyProjectionState? dirtyState = operationalStore.GetProjectionDirtyState();
        List<string> deferredSessions = dirtyState is not null && !dirtyState.IsWildcard && dirtyState.Keys is not null
            ? [.. dirtyState.Keys.Order(StringComparer.Ordinal)]
            : [];

        return new
        {
            status = lastError is null ? "ok" : ProjectionFailedStatus,
            lastError,
            failedSessions,
            deferredSessions,
            lastBatchReadFault,
        };
    }

    private static bool IsTransientStoreFault(Exception ex) =>
        ex is SqliteExecutionException or CircuitBreakerOpenException or IOException or TimeoutException;

    private static string? NormalizeHarnessFilter(string? harness) =>
        string.IsNullOrWhiteSpace(harness) ? null : CanonPricing.NormalizeHarnessName(harness.Trim());

    /// <summary>Trims a sessions search to the text to match, or answers <see langword="null"/> when none was given.</summary>
    /// <param name="search">Search text as typed.</param>
    /// <returns>The trimmed search text, or <see langword="null"/>.</returns>
    private static string? NormalizeSessionSearch(string? search) =>
        string.IsNullOrWhiteSpace(search) ? null : search.Trim();

    private static string? ResolveContextLimitSource(StoredSessionProjection projection) =>
        projection.Context?.ContextLimitSource ?? projection.ContextUnmeasurableReason;

    private static List<Dictionary<string, object?>> RequestRows(IReadOnlyList<ReadTimeSessionRequestSummary> requests)
    {
        List<Dictionary<string, object?>> rows = new(requests.Count);
        foreach (ReadTimeSessionRequestSummary request in requests)
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

    private static List<Dictionary<string, object?>> ToolRows(
        IReadOnlyList<ReadTimeSessionToolSummary> tools,
        IReadOnlyList<string> toolsOffered)
    {
        HashSet<string> offeredTools = new(toolsOffered, StringComparer.Ordinal);
        List<Dictionary<string, object?>> rows = new(tools.Count);
        foreach (ReadTimeSessionToolSummary tool in tools)
        {
            bool inDefinitions = offeredTools.Contains(tool.Name)
                || tool.TurnsResident > 0
                || tool.SchemaTokens > 0
                || tool.TotalSchemaCost > 0;
            bool isMcp = !string.IsNullOrWhiteSpace(tool.Server);

            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = tool.Name,
                ["server"] = tool.Server,
                ["invocations"] = tool.Invocations,
                ["schema_tokens"] = inDefinitions ? tool.SchemaTokens : null,
                ["turns_resident"] = inDefinitions ? tool.TurnsResident : null,
                ["total_schema_cost"] = inDefinitions ? tool.TotalSchemaCost : null,
                ["invoked"] = tool.Invoked,
                ["in_definitions"] = inDefinitions,
                ["is_mcp"] = isMcp,
            });
        }

        return rows;
    }

    private static List<Dictionary<string, object?>> ServerRows(
        IReadOnlyList<ReadTimeSessionToolSummary> tools,
        IReadOnlyList<ReadTimeSessionServerSummary> servers)
    {
        Dictionary<string, long> schemaCostByServer = new(StringComparer.Ordinal);
        foreach (ReadTimeSessionToolSummary tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Server))
            {
                continue;
            }

            schemaCostByServer[tool.Server] = schemaCostByServer.GetValueOrDefault(tool.Server) + tool.TotalSchemaCost;
        }

        List<Dictionary<string, object?>> rows = new(servers.Count);
        foreach (ReadTimeSessionServerSummary server in servers)
        {
            long totalSchemaCost = schemaCostByServer.TryGetValue(server.Server, out long sum)
                ? sum
                : server.UnusedCost;

            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["server"] = server.Server,
                ["is_mcp"] = server.IsMcp,
                ["tools"] = server.Tools,
                ["schema_tokens"] = server.SchemaTokens,
                ["total_schema_cost"] = totalSchemaCost,
                ["invocations"] = server.Invocations,
                ["unused_tools"] = server.UnusedTools,
                ["unused_cost"] = server.UnusedCost,
            });
        }

        return rows;
    }

    private static List<Dictionary<string, object?>> ReconciliationRows(IReadOnlyList<ReadTimeTurnReconciliation> reconciliation)
    {
        List<Dictionary<string, object?>> rows = [];
        foreach (ReadTimeTurnReconciliation row in reconciliation)
        {
            if (!row.InputMatch.HasValue || !row.OutputMatch.HasValue)
            {
                continue;
            }

            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = row.RequestSpanId,
                ["root_input"] = row.RootInput,
                ["sum_chat_input"] = row.SumChatInput,
                ["input_match"] = row.InputMatch.Value,
                ["root_output"] = row.RootOutput,
                ["sum_chat_output"] = row.SumChatOutput,
                ["output_match"] = row.OutputMatch.Value,
            });
        }

        return rows;
    }

    private static Dictionary<string, object?>? BuildCostBasisMismatchObject(ReadTimeCostBasisMismatch? mismatch)
    {
        if (mismatch is null)
        {
            return null;
        }

        Dictionary<string, double?> totalsByBasis = new(StringComparer.Ordinal);
        foreach (ReadTimeBasisTotal entry in mismatch.TotalsByBasis)
        {
            totalsByBasis[entry.Basis] = entry.Total;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = mismatch.Code,
            ["message"] = mismatch.Message,
            ["bases"] = mismatch.Bases,
            ["totalsByBasis"] = totalsByBasis,
        };
    }

    private static (IReadOnlyList<string> DistinctSkills, IReadOnlyDictionary<string, int> Counts) CollectSkillCounts(
        IReadOnlyList<StoredSkillInvocation> rows)
    {
        List<string> distinct = [];
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (StoredSkillInvocation row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SkillName))
            {
                continue;
            }

            if (counts.TryGetValue(row.SkillName, out int existing))
            {
                counts[row.SkillName] = existing + 1;
            }
            else
            {
                counts[row.SkillName] = 1;
                distinct.Add(row.SkillName);
            }
        }

        return (distinct, counts);
    }

    private static Dictionary<string, object?> BuildSkillsPayload(
        IReadOnlyList<string> distinctSkills,
        IReadOnlyDictionary<string, int> skillCounts,
        string harness)
    {
        if (distinctSkills.Count == 0)
        {
            return NotMeasurableBlock($"{harness} did not record skill invocations or listed skills for this session.");
        }

        List<Dictionary<string, object?>> rows = new(distinctSkills.Count);
        foreach (string skillName in distinctSkills)
        {
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = skillName,
                ["listed"] = NotMeasurableBlock($"{harness} did not export the session's listed skills."),
                ["invocations"] = skillCounts[skillName],
                ["turns_listed"] = Array.Empty<int>(),
            });
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["measurable"] = true,
            ["rows"] = rows,
        };
    }

    private static Dictionary<string, object?> BuildContextPayload(
        StoredSessionProjection projection,
        ReadTimeContextBucketSummary? firstBucket,
        ReadTimeContextBucketSummary? lastBucket,
        IReadOnlyList<ReadTimeTurnSummary> turns)
    {
        int unmeasuredTurns = turns.Count(static t => !t.InputTokens.HasValue);

        if (projection.Context is not StoredSessionContext ctx)
        {
            string reason = !string.IsNullOrWhiteSpace(projection.ContextUnmeasurableReason)
                ? projection.ContextUnmeasurableReason
                : "Session turns did not carry message parts or context token measurements.";

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["measurable"] = false,
                ["reason"] = reason,
                ["turns"] = turns.Count,
                ["contextLimit"] = 0L,
                ["first"] = firstBucket is ReadTimeContextBucketSummary firstOnly
                    ? BuildContextEdge(firstOnly, storedTurn: null)
                    : null,
                ["last"] = lastBucket is ReadTimeContextBucketSummary lastOnly
                    ? BuildContextEdge(lastOnly, storedTurn: null)
                    : null,
                ["unmeasuredTurns"] = unmeasuredTurns,
            };
        }

        Dictionary<string, long> derivedCounts = new(StringComparer.Ordinal);
        foreach (StoredSessionContextDerivedCount counter in ctx.DerivedCounts)
        {
            derivedCounts[counter.Counter] = counter.Count;
        }

        List<int> flaggedTurns = new(ctx.FlaggedTurnIndexes.Count);
        foreach (int zeroBasedIdx in ctx.FlaggedTurnIndexes)
        {
            flaggedTurns.Add(zeroBasedIdx + 1);
        }

        List<Dictionary<string, object?>> contextTurns = new(ctx.Turns.Count);
        foreach (StoredSessionContextTurn turn in ctx.Turns)
        {
            contextTurns.Add(BuildStoredContextTurn(turn));
        }

        StoredSessionContextTurn? firstStored = ctx.Turns.Count > 0 ? ctx.Turns[0] : null;
        StoredSessionContextTurn? lastStored = ctx.Turns.Count > 0 ? ctx.Turns[^1] : null;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["measurable"] = true,
            ["contextLimit"] = ctx.ContextLimit,
            ["contextLimitSource"] = ctx.ContextLimitSource,
            ["residualTotal"] = ctx.ResidualTotal,
            ["freshJumpFactor"] = ctx.FreshJumpFactor,
            ["accumulationRate"] = ctx.AccumulationRate,
            ["observedBuckets"] = ctx.ObservedBuckets,
            ["derivedCounts"] = derivedCounts,
            ["flaggedTurns"] = flaggedTurns,
            ["unmeasuredTurns"] = unmeasuredTurns,
            ["turns"] = contextTurns,
            ["first"] = BuildContextEdge(firstBucket, firstStored),
            ["last"] = BuildContextEdge(lastBucket, lastStored),
        };
    }

    /// <summary>
    /// Projects one stored context turn onto the per-turn entry the charts context object and the
    /// widened turn section both render, so the two routes answer the same buckets.
    /// </summary>
    private static Dictionary<string, object?> BuildStoredContextTurn(StoredSessionContextTurn turn)
    {
        Dictionary<string, long> buckets = new(StringComparer.Ordinal);
        foreach (StoredTurnBucketTokens bucket in turn.Buckets)
        {
            buckets[bucket.Bucket] = bucket.Tokens;
        }

        Dictionary<string, long> toolDefsByServer = new(StringComparer.Ordinal);
        foreach (StoredTurnServerDefinitionTokens serverDef in turn.ServerDefinitions)
        {
            toolDefsByServer[serverDef.Server] = serverDef.Tokens;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["index"] = turn.Position + 1,
            ["inputTokens"] = turn.InputTokens,
            ["reported_input"] = turn.InputTokens,
            ["buckets"] = buckets,
            ["toolDefinitionsByServer"] = toolDefsByServer,
            ["builtinToolDefinitionTokens"] = turn.BuiltinToolDefinitionTokens,
            ["strippedInstructionBlocks"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["count"] = turn.StrippedBlockCount ?? 0L,
                ["tokens"] = turn.StrippedTokens ?? 0L,
            },
            ["bucketedTokens"] = turn.BucketedTokens,
            ["residual"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tokens"] = turn.ResidualTokens,
                ["attribution"] = turn.ResidualAttribution,
            },
            ["headroom"] = turn.Headroom,
            ["pressure"] = turn.Pressure,
            ["accumulationRate"] = turn.AccumulationRate,
            ["freshInput"] = turn.FreshInput,
            ["previousFreshInput"] = turn.PreviousFreshInput,
            ["freshJumpFactor"] = turn.FreshJumpFactor,
            ["nonResidentTokens"] = turn.NonResidentTokens,
        };
    }

    private static Dictionary<string, object?>? BuildContextEdge(
        ReadTimeContextBucketSummary? edge,
        StoredSessionContextTurn? storedTurn)
    {
        if (edge is not null)
        {
            Dictionary<string, long> buckets = new(StringComparer.Ordinal);
            foreach (ReadTimeContextBucketTokens bucket in edge.Buckets)
            {
                buckets[bucket.Bucket] = bucket.Tokens;
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["turn"] = storedTurn is not null ? storedTurn.Position + 1 : 1,
                ["buckets"] = buckets,
                ["reported_input"] = edge.ReportedInput,
            };
        }

        if (storedTurn is not null)
        {
            Dictionary<string, long> buckets = new(StringComparer.Ordinal);
            foreach (StoredTurnBucketTokens bucket in storedTurn.Buckets)
            {
                buckets[bucket.Bucket] = bucket.Tokens;
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["turn"] = storedTurn.Position + 1,
                ["buckets"] = buckets,
                ["reported_input"] = storedTurn.InputTokens,
            };
        }

        return null;
    }

    private static List<string> BuildNotes(
        StoredSessionProjection projection,
        SessionSummaryFigure? figures,
        IReadOnlyList<string> distinctSkills)
    {
        List<string> notes = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        void AddNote(string? note)
        {
            if (!string.IsNullOrWhiteSpace(note) && seen.Add(note))
            {
                notes.Add(note);
            }
        }

        if (projection.Context is null)
        {
            AddNote(!string.IsNullOrWhiteSpace(projection.ContextUnmeasurableReason)
                ? projection.ContextUnmeasurableReason
                : "Context breakdown is not measurable because turns did not export message parts.");
        }

        if (projection.Schema is null)
        {
            AddNote(!string.IsNullOrWhiteSpace(projection.SchemaUnmeasurableReason)
                ? projection.SchemaUnmeasurableReason
                : "Schema residency is not measurable because tool definitions were not exported.");
        }

        if (figures is not null)
        {
            if (!figures.HasToolDefinitions && figures.ToolRowsCount > 0)
            {
                AddNote("Tool definitions were not exported; tool residency and schema costs are marked unmeasured.");
            }

            if (figures.ReportedCost is null && figures.TurnCount > 0)
            {
                AddNote("Harness-reported cost was not exported; published pricing estimates are shown separately.");
            }

            if (figures.CostBasisMismatch is not null)
            {
                AddNote(figures.CostBasisMismatch.Message);
            }

            if (!figures.TotalCacheCreation.HasValue && figures.TurnCount > 0)
            {
                AddNote("Cache creation tokens were not reported for this session.");
            }

            if (figures.UnmeasuredInputTurns > 0)
            {
                AddNote("One or more turns lack input token counts.");
            }
        }

        AddNote(distinctSkills.Count > 0
            ? "Skill listings were not exported; only invoked skills are shown."
            : "No skill listings or skill invocations were recorded for this session.");

        return notes;
    }

    private static Dictionary<string, object?> NotMeasurableBlock(string reason) => new(StringComparer.Ordinal)
    {
        ["availability"] = "not_measurable",
        ["reason"] = reason,
    };

    /// <summary>
    /// Resolves the advertised tool names the summary's <c>tools_offered</c> shows: the projection
    /// schema's ranked tools when the figures carried none, else the figures' own list.
    /// </summary>
    private static IReadOnlyList<string> OfferedToolNames(SessionSummaryFigure figures, StoredSessionProjection projection)
    {
        if (figures.HasToolDefinitions)
        {
            return figures.ToolsOffered;
        }

        return projection.Schema is StoredSessionSchema schema
            ? [.. schema.RankedTools.OrderBy(static t => t.Position).Select(static t => t.ToolName)]
            : [];
    }

    private static Dictionary<string, object?> BuildSessionSummary(
        SessionSummaryFigure figures,
        StoredSessionProjection projection,
        string harness,
        IReadOnlyList<string> distinctSkills)
    {
        bool hasToolDefinitions = figures.HasToolDefinitions || projection.Schema is not null;
        object toolsOffered = hasToolDefinitions
            ? OfferedToolNames(figures, projection)
            : NotMeasurableBlock($"{harness} did not export tool definitions for this session.");

        Dictionary<string, object?> summary = new(StringComparer.Ordinal)
        {
            ["turn_count"] = figures.TurnCount,
            ["request_count"] = figures.RequestCount,
            ["span_count"] = projection.SpanCount,
            ["total_input"] = figures.TotalInput,
            ["total_output"] = figures.TotalOutput,
            ["total_cache_read"] = figures.TotalCacheRead,
            ["total_cache_creation"] = figures.TotalCacheCreation,
            ["duration_ms"] = figures.DurationMs,
            ["models"] = figures.Models,
            ["cache_hit_ratio"] = figures.CacheHitRatio,
            ["cache_creation_coverage"] = figures.CacheCreationCoverage,
            ["tool_calls"] = figures.ToolCalls,
            ["tools_invoked"] = figures.ToolsInvoked,
            ["tools_offered"] = toolsOffered,
            ["has_tool_definitions"] = hasToolDefinitions,
            ["cost"] = figures.PublishedCost,
            ["cost_reported"] = figures.ReportedCost,
            ["cost_basis_mismatch"] = figures.CostBasisMismatch,
            ["coverage"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schema_measurable"] = figures.Coverage.SchemaMeasurable,
                ["context_measurable"] = figures.Coverage.ContextMeasurable,
            },
            ["problems"] = figures.Problems,
        };

        if (distinctSkills.Count > 0)
        {
            summary["skills_invoked"] = distinctSkills;
        }

        return summary;
    }

    /// <summary>Projects one scoped content part onto the flat entry the content view renders.</summary>
    /// <param name="spanId">Span the part belongs to.</param>
    /// <param name="index">Position of that span in the answered sequence.</param>
    /// <param name="source">Content part read through the scoped content read.</param>
    private static Dictionary<string, object?> ToContentEntry(string spanId, int index, CollectedContentPart source)
    {
        Dictionary<string, object?> entry = new(StringComparer.Ordinal)
        {
            ["spanId"] = spanId,
            ["index"] = index,
            ["part"] = CanonRecordHydration.PartKeyOf(source.Bucket),
            ["text"] = source.PlainText ?? string.Empty,
        };
        AddPartDetail(entry, source);
        return entry;
    }

    /// <summary>Projects one scoped content part onto the turn content payload's part entry.</summary>
    /// <param name="scoped">Content part read through the scoped content read.</param>
    private static Dictionary<string, object?> ToTurnContentEntry(StoredContentPart scoped)
    {
        Dictionary<string, object?> entry = new(StringComparer.Ordinal)
        {
            ["spanId"] = scoped.SpanId,
            ["part"] = CanonRecordHydration.PartKeyOf(scoped.Part.Bucket),
            ["text"] = scoped.Part.PlainText ?? string.Empty,
        };
        AddPartDetail(entry, scoped.Part);
        return entry;
    }

    /// <summary>Adds the measured detail a part carries — tokens, server, residency, truncation — when present.</summary>
    /// <param name="entry">Entry being built.</param>
    /// <param name="part">Content part the entry describes.</param>
    private static void AddPartDetail(Dictionary<string, object?> entry, CollectedContentPart part)
    {
        if ((part.ReportedTokens ?? part.Withheld?.MeasuredTokens) is long tokens) entry["tokens"] = tokens;
        if (part.Server is string server) entry["server"] = server;
        if (part.Resident is bool resident) entry["resident"] = resident;
        if (part.OriginalUtf8BytesWhenTruncated.HasValue) entry["truncated"] = true;
    }

    private static Dictionary<string, object?> ToTurnDto(ReadTimeTurnSummary turn) => new(StringComparer.Ordinal)
    {
        ["turnIndex"] = turn.Position,
        ["index"] = turn.Position,
        ["spanId"] = turn.SpanId,
        ["timestamp"] = turn.TimestampUtc,
        ["model"] = turn.Model,
        ["inputTokens"] = turn.InputTokens,
        ["input"] = turn.InputTokens,
        ["outputTokens"] = turn.OutputTokens,
        ["output"] = turn.OutputTokens,
        ["tokens"] = turn.InputTokens.HasValue && turn.OutputTokens.HasValue
            ? turn.InputTokens.Value + turn.OutputTokens.Value
            : null,
        ["freshInputTokens"] = turn.FreshInputTokens,
        ["fresh"] = turn.FreshInputTokens,
        ["fresh_input"] = turn.FreshInputTokens,
        ["cacheReadTokens"] = turn.CacheReadTokens,
        ["cache_read"] = turn.CacheReadTokens,
        ["cacheCreationTokens"] = turn.CacheCreationTokens,
        ["cache_creation"] = turn.CacheCreationTokens,
        ["reasoningTokens"] = turn.ReasoningTokens,
        ["reasoning"] = turn.ReasoningTokens,
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

    /// <summary>
    /// Reads the content-free session figures the run and comparison pages price, in one batched
    /// pass. A faulted batch leaves the figures absent rather than materializing every session's
    /// records to recover them, which is the stall this read model exists to remove.
    /// </summary>
    /// <param name="lookup">Read-time pricing tables.</param>
    private Dictionary<string, SessionListFigure> BuildFigureIndex(IModelPricingLookup lookup)
    {
        Dictionary<string, SessionListFigure> figures = new(StringComparer.Ordinal);
        try
        {
            foreach (SessionListFigure figure in reader.SessionListFigures((string?)null, lookup))
            {
                figures[figure.SessionKey] = figure;
            }

            ClearBatchedReadFault();
        }
        catch (Exception ex)
        {
            // The fault carries no session, so it is recorded in the coverage payload's batch
            // slot rather than the per-session failure map, where it would surface as a
            // failed session no operator can resolve.
            RecordBatchedReadFault(ex.Message);
            return [];
        }

        return figures;
    }

    private static (int? TurnCount, double? CostUsd) SessionFiguresOf(
        StoredExecution execution,
        Dictionary<string, SessionListFigure> figuresById)
    {
        if (string.IsNullOrEmpty(execution.SessionId)
            || !figuresById.TryGetValue(execution.SessionId, out SessionListFigure? figure))
        {
            return (null, null);
        }

        return (figure.TurnCount, ExtractUsdCost(figure.PublishedCost));
    }

    private static RunSessionFigures SummarizeRunSessions(
        IReadOnlyList<StoredExecution> executions,
        Dictionary<string, SessionListFigure> figuresById)
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

            if (!figuresById.TryGetValue(exec.SessionId, out SessionListFigure? figure))
            {
                continue;
            }

            turnCount += figure.TurnCount;
            hasTurns = true;

            if (figure.TotalInput.HasValue)
            {
                totalInput += figure.TotalInput.Value;
                hasInput = true;
            }

            if (figure.TotalOutput.HasValue)
            {
                totalOutput += figure.TotalOutput.Value;
                hasOutput = true;
            }

            if (ExtractUsdCost(figure.PublishedCost) is double sessionCost)
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
        Dictionary<string, SessionListFigure> figuresById)
    {
        Dictionary<string, ExecutionTreeNode> nodesById = new(StringComparer.Ordinal);
        List<ExecutionTreeNode> ordered = new(executions.Count);
        foreach (StoredExecution exec in executions)
        {
            (int? TurnCount, double? CostUsd) sessionFigures = SessionFiguresOf(exec, figuresById);
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
                // The run page lists every turn of the run, so it pays one content-free turn
                // figures read per session rather than the detail figure set's tool and request
                // rows the read model exists to avoid.
                sessionTurns = reader.SessionTurnFigures(exec.SessionId, lookup)?.Turns ?? [];
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

    /// <summary>
    /// Resolves one run's turns for comparison: the canonical content-free turn figures, joined to
    /// the tool names and harness-reported token totals the content-free record read carries. A
    /// faulted session is isolated the way <see cref="ExtractRunTurns"/> isolates it — recorded,
    /// skipped, and the remaining sessions answer.
    /// </summary>
    /// <param name="run">The run being resolved.</param>
    /// <param name="lookup">Read-time pricing tables.</param>
    /// <returns>The run's comparison turns, in execution order.</returns>
    private RunComparisonRun ResolveComparisonRun(StoredRun run, IModelPricingLookup lookup)
    {
        IReadOnlyList<StoredExecution> executions = projections.ListExecutionsForRun(run.RunId);
        List<RunComparisonTurn> turns = [];
        HashSet<string> seenSessions = new(StringComparer.Ordinal);
        foreach (StoredExecution exec in executions)
        {
            if (string.IsNullOrEmpty(exec.SessionId) || !seenSessions.Add(exec.SessionId))
            {
                continue;
            }

            IReadOnlyList<ReadTimeTurnSummary> sessionTurns;
            IReadOnlyList<CollectedRecord> sessionRecords;
            try
            {
                sessionTurns = reader.SessionTurnFigures(exec.SessionId, lookup)?.Turns ?? [];
                sessionRecords = reader.SessionRecordsContentFree(exec.SessionId);
            }
            catch (Exception ex)
            {
                RecordProjectionFailure(exec.SessionId, ex.Message);
                continue;
            }

            Dictionary<string, CollectedRecord> recordsBySpan = new(sessionRecords.Count, StringComparer.Ordinal);
            foreach (CollectedRecord record in sessionRecords)
            {
                recordsBySpan[record.RecordId] = record;
            }

            Dictionary<string, List<string>> toolsBySpan = CollectTurnTools(sessionRecords);
            foreach (ReadTimeTurnSummary turn in sessionTurns)
            {
                // The canonical per-turn total follows the read model's own rule: measured input
                // plus measured output, withheld with a reason when either side went unmeasured.
                CollectedRecord? record = recordsBySpan.GetValueOrDefault(turn.SpanId);
                long? all = turn.InputTokens.HasValue && turn.OutputTokens.HasValue
                    ? turn.InputTokens.Value + turn.OutputTokens.Value
                    : null;
                turns.Add(new RunComparisonTurn(
                    TurnIndex: turns.Count,
                    SpanId: turn.SpanId,
                    FreshInput: turn.FreshInputTokens,
                    CacheRead: turn.CacheReadTokens,
                    CacheCreation: turn.CacheCreationTokens,
                    Output: turn.OutputTokens,
                    ReportedInput: record?.ReportedInputTokens,
                    ReportedOutput: record?.ReportedOutputTokens,
                    All: all,
                    AllReason: all.HasValue ? null : CanonRunComparison.TurnTokensNotMeasurableReason,
                    Cost: turn.PublishedCost,
                    Tools: toolsBySpan.GetValueOrDefault(turn.SpanId) ?? []));
            }
        }

        return new RunComparisonRun(run.RunId, run.HarnessId, run.Label, turns);
    }

    /// <summary>
    /// Attributes each tool invocation to the model turn that issued it: walking a session's
    /// records in start order, a <c>tool.invoke</c> record names the turn whose <c>llm.invoke</c>
    /// record precedes it.
    /// </summary>
    /// <param name="records">The session's content-free records.</param>
    /// <returns>Tool names keyed by the issuing turn's span identifier.</returns>
    private static Dictionary<string, List<string>> CollectTurnTools(IReadOnlyList<CollectedRecord> records)
    {
        Dictionary<string, List<string>> toolsBySpan = new(StringComparer.Ordinal);
        string? currentTurnSpan = null;
        IOrderedEnumerable<CollectedRecord> ordered = records
            .OrderBy(static record => record.StartedAt)
            .ThenBy(static record => record.RecordId, StringComparer.Ordinal);
        foreach (CollectedRecord record in ordered)
        {
            if (record.Op == RecordOp.LlmInvoke)
            {
                currentTurnSpan = record.RecordId;
            }
            else if (record.Op == RecordOp.ToolInvoke
                && currentTurnSpan is not null
                && record.Tool is CollectedToolCall tool
                && !string.IsNullOrWhiteSpace(tool.ToolName))
            {
                if (!toolsBySpan.TryGetValue(currentTurnSpan, out List<string>? names))
                {
                    names = [];
                    toolsBySpan[currentTurnSpan] = names;
                }

                names.Add(tool.ToolName);
            }
        }

        return toolsBySpan;
    }

    private object BuildRefreshCoverage() => BuildRefreshCoverageState().Payload;

    private RefreshCoverageState BuildRefreshCoverageState()
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

        string? lastSuccessAt = success is null ? null : (success.CompletedAt ?? success.StartedAt);
        string? lastFailureAt = failure is null ? null : (failure.CompletedAt ?? failure.StartedAt);
        object payload = new
        {
            lastSuccessAt,
            lastFailure = failure is null
                ? null
                : new { at = lastFailureAt, summary = failure.Summary ?? "refresh failed" },
            inProgress = running is null
                ? null
                : new { pid = running.Pid, since = running.StartedAt },
            historyWeeks,
            coveredFrom,
            coveredThrough,
        };

        return new RefreshCoverageState(payload, lastSuccessAt, lastFailureAt);
    }

    private object BuildIngestCoverage()
    {
        Dictionary<string, int> recordCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> countsBySource = new(StringComparer.Ordinal);
        foreach (StoredSourceCount count in reader.SourceRecordCounts())
        {
            countsBySource[count.SourceId] = count.Count;
        }

        foreach (IGrouping<string, StoredSource> group in reader.Sources()
                     .GroupBy(static s => s.Source.Locator, StringComparer.Ordinal))
        {
            int count = 0;
            foreach (StoredSource source in group)
            {
                count += countsBySource.GetValueOrDefault(source.SourceId);
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

    private static bool TryParseReportTimestamp(string? raw, out DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            timestamp = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static bool TryGetProjectionActivityTime(StoredSessionProjection? projection, out DateTimeOffset timestamp)
    {
        if (projection is null)
        {
            timestamp = default;
            return false;
        }

        return TryParseReportTimestamp(projection.EndedAtUtc, out timestamp)
            || TryParseReportTimestamp(projection.StartedAtUtc, out timestamp);
    }

    private static bool IsSessionInWindow(
        StoredSessionProjection? projection,
        DateTimeOffset cutoff,
        out DateTimeOffset? activityTime)
    {
        if (TryGetProjectionActivityTime(projection, out DateTimeOffset parsed))
        {
            activityTime = parsed;
            return parsed >= cutoff;
        }

        activityTime = null;
        return false;
    }

    private static bool IsRunInWindow(StoredRun run, DateTimeOffset cutoff)
    {
        if (TryParseReportTimestamp(run.EndedAtUtc, out DateTimeOffset ended)
            || TryParseReportTimestamp(run.StartedAtUtc, out ended))
        {
            return ended >= cutoff;
        }

        return cutoff == DateTimeOffset.MinValue;
    }

    private static string? ResolveFindingHarness(
        StoredFinding finding,
        IReadOnlyDictionary<string, string> sessionHarnessById,
        IReadOnlyDictionary<string, string> runHarnessById)
    {
        if (finding.SessionId is not null && sessionHarnessById.TryGetValue(finding.SessionId, out string? sessionHarness))
        {
            return sessionHarness;
        }

        if (finding.RunId is not null && runHarnessById.TryGetValue(finding.RunId, out string? runHarness))
        {
            return runHarness;
        }

        return null;
    }

    private static object BuildReportFindingDto(StoredFinding finding, string? harness)
    {
        string measurementClass = string.IsNullOrWhiteSpace(finding.MeasurementClass)
            ? "inferred"
            : finding.MeasurementClass;
        object recoverableTokens = finding.EstimatedWasteTokens is long waste
            ? new { value = waste, unit = "tokens" }
            : new { value = (long?)null, reason = "Estimated recoverable tokens not measured for this finding." };
        object? errorBar = finding.ErrorBar is FindingErrorBar eb
            ? new { low = eb.Lower, high = eb.Upper }
            : null;
        List<string> evidenceIds = [.. finding.EvidenceLinks.Select(static link => link.SpanId)];

        return new
        {
            id = finding.FindingId,
            title = finding.Title,
            measurementClass,
            confidence = finding.Confidence,
            confidenceBasis = finding.ConfidenceBasis,
            mechanism = finding.Mechanism,
            evidenceIds,
            recommendation = finding.Recommendation,
            recoverableTokens,
            errorBar,
            outcomeRiskCaveat = finding.OutcomeRiskCaveat,
            rankScore = finding.RankScore,
            sessionId = finding.SessionId,
            runId = finding.RunId,
            harness,
            view = string.Create(CultureInfo.InvariantCulture, $"finding/{finding.FindingId}"),
        };
    }

    private static List<object> BuildReportCoverageHarnesses(
        IReadOnlyDictionary<string, StoredHarnessRollup> storedRollupsByHarness,
        IReadOnlyDictionary<string, int> totalHarnessCounts,
        IReadOnlyDictionary<string, int> inWindowHarnessCounts)
    {
        List<string> allHarnesses = [.. totalHarnessCounts.Keys.OrderBy(static harnessId => harnessId, StringComparer.Ordinal)];
        List<object> entries = new(allHarnesses.Count);
        foreach (string harnessId in allHarnesses)
        {
            Dictionary<string, object> measurability = storedRollupsByHarness.TryGetValue(harnessId, out StoredHarnessRollup? rollup)
                ? ConvertCoverageMeasurability(ToHarnessRollupRow(rollup).Measurability)
                : new Dictionary<string, object>(StringComparer.Ordinal);
            entries.Add(new
            {
                harness = harnessId,
                name = harnessId,
                sessionsInWindow = inWindowHarnessCounts.GetValueOrDefault(harnessId, 0),
                measurability,
            });
        }

        return entries;
    }

    private static Dictionary<string, object> ConvertCoverageMeasurability(IReadOnlyDictionary<string, JsonElement> source)
    {
        Dictionary<string, object> result = new(source.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> pair in source)
        {
            if (pair.Value.ValueKind == JsonValueKind.String)
            {
                result[pair.Key] = "measurable";
            }
            else if (pair.Value.ValueKind == JsonValueKind.Object
                && pair.Value.TryGetProperty("reason", out JsonElement reasonElement)
                && reasonElement.ValueKind == JsonValueKind.String)
            {
                result[pair.Key] = new { reason = reasonElement.GetString() ?? "Not measurable." };
            }
        }

        return result;
    }

    private static List<string> BuildReportCoverageHints(int sessionsInScope, RefreshCoverageState refreshState)
    {
        List<string> hints = [];
        if (sessionsInScope == 0)
        {
            hints.Add("No session in the window. Run `kyberdash dash refresh --history-weeks <n>` to widen the coverage window.");
        }

        if (string.IsNullOrWhiteSpace(refreshState.LastSuccessAt))
        {
            hints.Add("No successful refresh recorded. Run `kyberdash dash refresh`.");
        }
        else if (TryParseReportTimestamp(refreshState.LastFailureAt, out DateTimeOffset failureTime)
            && TryParseReportTimestamp(refreshState.LastSuccessAt, out DateTimeOffset successTime)
            && failureTime > successTime)
        {
            hints.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Last successful refresh was {refreshState.LastSuccessAt}. Run `kyberdash dash refresh`."));
        }

        return hints;
    }

    private List<object> BuildReportHarnesses(
        IReadOnlyList<ReportSessionEntry> scopedSessions,
        IReadOnlyDictionary<string, int> totalHarnessCounts,
        IReadOnlyDictionary<string, int> inWindowHarnessCounts,
        IReadOnlyDictionary<string, StoredHarnessRollup> storedRollupsByHarness,
        IReadOnlyDictionary<string, SessionReportFigure> aggregatesById,
        IReadOnlyList<StoredRun> allRuns,
        DateTimeOffset cutoff,
        bool unfilteredSessionsAndRuns)
    {
        Dictionary<string, List<ReportSessionEntry>> sessionsByHarness = new(StringComparer.Ordinal);
        foreach (ReportSessionEntry entry in scopedSessions)
        {
            if (!sessionsByHarness.TryGetValue(entry.Harness, out List<ReportSessionEntry>? bucket))
            {
                bucket = [];
                sessionsByHarness[entry.Harness] = bucket;
            }

            bucket.Add(entry);
        }

        List<string> orderedHarnesses = [.. sessionsByHarness.Keys.OrderBy(static k => k, StringComparer.Ordinal)];
        List<object> result = new(orderedHarnesses.Count);
        foreach (string harnessId in orderedHarnesses)
        {
            List<ReportSessionEntry> harnessSessions = sessionsByHarness[harnessId];
            StoredHarnessRollup? fullRollup = null;
            bool matchesStoredAllTime = unfilteredSessionsAndRuns
                && storedRollupsByHarness.TryGetValue(harnessId, out fullRollup)
                && fullRollup.SessionCount == harnessSessions.Count
                && inWindowHarnessCounts.GetValueOrDefault(harnessId, 0) == totalHarnessCounts.GetValueOrDefault(harnessId, 0)
                && harnessSessions.Count == totalHarnessCounts.GetValueOrDefault(harnessId, 0);

            HarnessRollupRow row = matchesStoredAllTime && fullRollup is not null
                ? ToHarnessRollupRow(fullRollup)
                : BuildWindowHarnessRollupRow(harnessId, harnessSessions, aggregatesById, allRuns, cutoff, unfilteredSessionsAndRuns);

            result.Add(new
            {
                harness = harnessId,
                name = harnessId,
                dimensions = CanonRollupsEngine.BuildHarnessScorecard(row),
            });
        }

        return result;
    }

    private HarnessRollupRow BuildWindowHarnessRollupRow(
        string harnessId,
        IReadOnlyList<ReportSessionEntry> harnessSessions,
        IReadOnlyDictionary<string, SessionReportFigure> aggregatesById,
        IReadOnlyList<StoredRun> allRuns,
        DateTimeOffset cutoff,
        bool allowExecutionFallback)
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

        foreach (ReportSessionEntry entry in harnessSessions)
        {
            if (entry.Projection is not null && entry.Projection.IsSubagent)
            {
                subagentSessions++;
            }

            if (entry.Projection?.Context is StoredSessionContext context
                && !IsUnnamedReportWindowSource(context.ContextLimitSource))
            {
                double peak = 0;
                bool found = false;
                foreach (StoredSessionContextTurn turn in context.Turns)
                {
                    if (turn.Pressure is double pressure
                        && double.IsFinite(pressure)
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

            if (!aggregatesById.TryGetValue(entry.Session.SessionKey, out SessionReportFigure? aggregate))
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
            double medianPressure = Math.Round(CanonRollupsEngine.ComputeMedian(peakPressures), 4);
            double p95Pressure = Math.Round(CanonRollupsEngine.ComputeP95(peakPressures), 4);
            string basis = anyDerivedPressure ? "derived" : "measured";
            metrics.Add(new StoredHarnessRollupMetric("context_pressure_median", medianPressure, basis));
            metrics.Add(new StoredHarnessRollupMetric("context_pressure_p95", p95Pressure, basis));
        }

        if (hasCacheRead && totalInput > 0)
        {
            metrics.Add(new StoredHarnessRollupMetric("cache_hit_rate", Math.Round((double)totalCacheRead / totalInput, 4), "measured"));
        }

        if (totalDefinedTools > 0)
        {
            metrics.Add(new StoredHarnessRollupMetric("tool_yield", Math.Round((double)totalInvokedTools / totalDefinedTools, 4), "measured"));
        }

        int executionCount = 0;
        int childExecutions = 0;
        int runCount = 0;
        foreach (StoredRun run in allRuns)
        {
            if (!string.Equals(CanonPricing.NormalizeHarnessName(run.HarnessId), harnessId, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryParseReportTimestamp(run.EndedAtUtc ?? run.StartedAtUtc, out DateTimeOffset runTime) && runTime < cutoff)
            {
                continue;
            }

            runCount++;
            if (allowExecutionFallback && subagentSessions == 0)
            {
                IReadOnlyList<StoredExecution> execs = projections.ListExecutionsForRun(run.RunId);
                executionCount += execs.Count;
                foreach (StoredExecution exec in execs)
                {
                    if (!exec.IsRoot)
                    {
                        childExecutions++;
                    }
                }
            }
        }

        if (harnessSessions.Count > 0 && subagentSessions > 0)
        {
            metrics.Add(new StoredHarnessRollupMetric(
                "delegation_overhead",
                Math.Round((double)subagentSessions / harnessSessions.Count, 4),
                "measured"));
        }
        else if (executionCount > 1 && childExecutions > 0)
        {
            metrics.Add(new StoredHarnessRollupMetric(
                "delegation_overhead",
                Math.Round((double)childExecutions / executionCount, 4),
                "measured"));
        }

        return ToHarnessRollupRow(new StoredHarnessRollup(
            harnessId,
            harnessSessions.Count,
            runCount,
            executionCount,
            unknownWindowSessions,
            harnessSessions.Count,
            metrics));
    }

    private static bool IsUnnamedReportWindowSource(string? source) =>
        string.IsNullOrEmpty(source) || string.Equals(source, "default", StringComparison.Ordinal);

    private object? BuildReportLatestSession(
        IReadOnlyList<ReportSessionEntry> scopedSessions,
        string generatedAt,
        IModelPricingLookup pricingLookup)
    {
        if (scopedSessions.Count == 0)
        {
            return null;
        }

        ReportSessionEntry newest = scopedSessions[0];
        for (int index = 1; index < scopedSessions.Count; index++)
        {
            ReportSessionEntry candidate = scopedSessions[index];
            DateTimeOffset candidateTime = candidate.ActivityTime ?? DateTimeOffset.MinValue;
            DateTimeOffset newestTime = newest.ActivityTime ?? DateTimeOffset.MinValue;
            int timeCompare = candidateTime.CompareTo(newestTime);
            if (timeCompare > 0
                || (timeCompare == 0 && string.Compare(candidate.Session.SessionKey, newest.Session.SessionKey, StringComparison.Ordinal) > 0))
            {
                newest = candidate;
            }
        }

        (SessionTurnFigureSet? latestTurnSet, _) = TrySessionTurnFigures(newest.Session.SessionKey, pricingLookup);
        IReadOnlyList<ReadTimeTurnSummary> turns = latestTurnSet?.Turns ?? [];

        StoredSessionProjection? projection = newest.Projection;
        string harness = newest.Harness;
        string lastActivityAt = !string.IsNullOrWhiteSpace(projection?.EndedAtUtc)
            ? projection.EndedAtUtc
            : !string.IsNullOrWhiteSpace(projection?.StartedAtUtc)
                ? projection.StartedAtUtc
                : generatedAt;
        int turnCount = latestTurnSet is not null
            ? turns.Count
            : Math.Max(turns.Count, projection?.Context?.Turns.Count ?? 0);

        object latestTurn;
        List<object> toolDefinitionSources = [];
        List<int> cacheInvalidationTurns = [];

        if (projection?.Context is StoredSessionContext context && context.Turns.Count > 0)
        {
            latestTurn = BuildMeasuredLatestTurn(context);
            foreach (int zeroBasedIndex in context.FlaggedTurnIndexes)
            {
                cacheInvalidationTurns.Add(zeroBasedIndex + 1);
            }
        }
        else
        {
            string unmeasurableReason = !string.IsNullOrWhiteSpace(projection?.ContextUnmeasurableReason)
                ? projection.ContextUnmeasurableReason
                : "harness exported no message structure for this session";
            latestTurn = BuildUnmeasurableLatestTurn(unmeasurableReason);
        }

        if (projection?.Schema?.Servers is { Count: > 0 } servers)
        {
            foreach (StoredSchemaServerTokens server in servers)
            {
                toolDefinitionSources.Add(new
                {
                    source = server.ServerName,
                    tokens = new { value = server.Tokens, unit = "tokens" },
                });
            }
        }

        return new
        {
            sessionId = newest.Session.SessionKey,
            harness,
            project = ResolveReportProject(newest.Session),
            lastActivityAt,
            turnCount,
            view = string.Create(CultureInfo.InvariantCulture, $"session/{newest.Session.SessionKey}"),
            latestTurn,
            toolDefinitionSources,
            cacheInvalidationTurns,
        };
    }

    private static object BuildMeasuredLatestTurn(StoredSessionContext context)
    {
        StoredSessionContextTurn lastTurn = context.Turns[^1];
        bool windowKnown = !IsUnnamedReportWindowSource(context.ContextLimitSource) && context.ContextLimit > 0;
        object pressure = windowKnown && lastTurn.Pressure is double pressureValue && double.IsFinite(pressureValue)
            ? new { value = pressureValue }
            : new { value = (double?)null, reason = "harness did not report model context window" };
        object contextWindow = windowKnown
            ? new { value = context.ContextLimit, unit = "tokens" }
            : new { value = (long?)null, reason = "harness did not report model context window" };

        Dictionary<string, long> rawBuckets = new(lastTurn.Buckets.Count, StringComparer.Ordinal);
        foreach (StoredTurnBucketTokens bucket in lastTurn.Buckets)
        {
            rawBuckets[bucket.Bucket] = bucket.Tokens;
        }

        Dictionary<string, object> buckets = new(ReportBucketKeys.Length, StringComparer.Ordinal);
        foreach (string key in ReportBucketKeys)
        {
            buckets[key] = new { value = rawBuckets.GetValueOrDefault(key, 0L), unit = "tokens" };
        }

        return new
        {
            index = lastTurn.Position + 1,
            pressure,
            contextWindow,
            buckets,
            residual = new { value = lastTurn.ResidualTokens, unit = "tokens" },
            cacheInvalidation = context.FlaggedTurnIndexes.Contains(lastTurn.Position),
        };
    }

    private static object BuildUnmeasurableLatestTurn(string reason)
    {
        object absent = new { value = (long?)null, reason };
        Dictionary<string, object> buckets = new(ReportBucketKeys.Length, StringComparer.Ordinal);
        foreach (string key in ReportBucketKeys)
        {
            buckets[key] = absent;
        }

        return new
        {
            index = 0,
            pressure = absent,
            contextWindow = absent,
            buckets,
            residual = absent,
            cacheInvalidation = false,
        };
    }

    private static string? ResolveReportProject(CollectedSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Workspace?.Repository))
        {
            return session.Workspace.Repository;
        }

        if (string.IsNullOrWhiteSpace(session.Workspace?.WorkingDirectory))
        {
            return null;
        }

        string trimmed = session.Workspace.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static List<object> BuildReportCost(
        IReadOnlyList<ReportSessionEntry> scopedSessions,
        IReadOnlyDictionary<string, SessionReportFigure> aggregatesById)
    {
        double harnessUsd = 0;
        bool hasHarness = false;
        double publishedUsd = 0;
        bool hasPublished = false;

        foreach (ReportSessionEntry entry in scopedSessions)
        {
            if (!aggregatesById.TryGetValue(entry.Session.SessionKey, out SessionReportFigure? aggregate))
            {
                continue;
            }

            if (ExtractUsdCost(aggregate.ReportedCost) is double reported)
            {
                harnessUsd += reported;
                hasHarness = true;
            }

            if (ExtractUsdCost(aggregate.PublishedCost) is double published)
            {
                publishedUsd += published;
                hasPublished = true;
            }
        }

        List<object> costRows = [];
        if (hasHarness)
        {
            costRows.Add(new
            {
                basis = "harness",
                amountUsd = new { value = Math.Round(harnessUsd, 6), unit = "USD" },
            });
        }

        if (hasPublished)
        {
            costRows.Add(new
            {
                basis = "published",
                amountUsd = new { value = Math.Round(publishedUsd, 6), unit = "USD" },
            });
        }

        if (costRows.Count == 0)
        {
            costRows.Add(new
            {
                basis = "unknown",
                amountUsd = new { value = (double?)null, reason = "no session in scope carried a cost" },
            });
        }

        return costRows;
    }

    private sealed record ReportSessionEntry(
        CollectedSession Session,
        StoredSessionProjection? Projection,
        string Harness,
        DateTimeOffset? ActivityTime);

    private sealed record RefreshCoverageState(
        object Payload,
        string? LastSuccessAt,
        string? LastFailureAt);

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
