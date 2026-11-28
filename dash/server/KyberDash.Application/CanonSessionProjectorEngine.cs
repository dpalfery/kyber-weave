using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using static KyberDash.Application.CanonSessionProjectorEngine;
using static KyberDash.Application.TwinDeduplicator;
using static KyberDash.Application.SessionProjectionHelpers;
using static KyberDash.Application.StoredProjectionHelpers;

namespace KyberDash.Application;

/// <summary>
/// Incremental session projection, same-turn twin deduplication, turn repricing, and
/// subagent/finding orchestration over the relational store contracts.
/// </summary>
/// <remarks>
/// <para>
/// The primary constructor projects sessions typed: it reads records only through
/// <see cref="IRelationalStoreReader"/>, counts tokens through the local
/// <see cref="ITextTokenizer"/> seam memoized via <see cref="IOperationalStore"/>
/// (decision D16), and writes <see cref="StoredSessionProjection"/> rows, typed context and
/// schema children (with exclusive <c>_unmeasurable</c> rows), and timeline marks through
/// <see cref="IProjectionStore"/> — no JSON payload column anywhere.
/// </para>
/// </remarks>
public partial class CanonSessionProjectorEngine
{
    /// <summary>Maximum timestamp skew (ms) for exact-counter or Cursor overlap twin collapsing.</summary>
    public const long TwinTurnMaxSkewMs = 60_000;

    /// <summary>Default per-session record count a read-path projection pass may rebuild in one pass.</summary>
    /// <remarks>
    /// A dirty session with more records than the budget is deferred: the read path must answer
    /// within a bounded read, so oversized sessions stay dirty for the maintenance pass
    /// (<c>kyber build</c> and the job scheduler) instead of stalling the request.
    /// </remarks>
    public const int DefaultReadPathSessionRecordBudget = 2_000;

    /// <summary>Default total record count one read-path projection pass may rebuild.</summary>
    /// <remarks>
    /// The per-session budget bounds one oversized session; this budget bounds the whole pass,
    /// so a cold read pass over thousands of small sessions rebuilds a bounded prefix of them
    /// inside the request and leaves the rest dirty for the next pass or the maintenance
    /// rebuild instead of rebuilding the whole store at once.
    /// </remarks>
    public const int DefaultReadPathPassRecordBudget = 20_000;

    internal const int PromptPreviewLength = 200;
    internal const string DroidHarness = "droid";
    internal const string DroidPluginSource = "kyberdash-factory-plugin";
    internal const string SessionLabelKey = "kyber.session.label";
    internal const string NoMessageStructureReason = "no_message_structure";
    internal const string NoToolDefinitionSchemaReason = "The source did not provide tool-definition schema data.";
    internal const string DefaultContextLimitSource = "default";
    internal const string SubagentSessionKeyMarker = "#agent-";

    internal static readonly HashSet<string> ClaudeCumulativeBuckets = new(StringComparer.Ordinal)
    {
        "system_prompt",
        "instruction_context",
        "conversation_history",
        "tool_result_content",
    };

    internal static readonly string[] AgentNameKeys = ["gen_ai.agent.name", "agent.name"];
    internal static readonly string[] RepoKeys = ["vcs.repository.name", "repo"];
    internal static readonly string[] BranchKeys = ["vcs.ref.head.name", "branch"];
    internal static readonly string[] ModelKeys = ["gen_ai.response.model", "gen_ai.request.model", "model"];
    internal static readonly string[] ParentSessionKeys = ["parent_session", "parent_session_id", "gen_ai.parent_session_id", "parentSession"];

    /// <summary>
    /// The harness ids a Codex record can carry: the OTLP log path names Codex <c>codex</c>
    /// (CanonAdapters' CodexAdapter), the OTLP span and rollout-file paths <c>codex-cli</c>
    /// (<c>TranscriptReaderRepository.CodexHarnessId</c>).
    /// </summary>
    internal static readonly HashSet<string> CodexHarnessIds = new(StringComparer.Ordinal)
    {
        "codex",
        "codex-cli",
    };

    internal static readonly HashSet<string> SideAgentNames = new(StringComparer.Ordinal)
    {
        "backgroundTodoAgent",
        "progressMessages",
        "title",
        "gitCommitMessageGenerator",
    };

    internal static readonly JsonSerializerOptions ProjectionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IRelationalStoreReader? reader;
    private readonly IProjectionStore? projections;
    private readonly IOperationalStore? operationalStore;
    private readonly IPricingRepository pricing;
    private readonly ITextTokenizer tokenizer;
    private readonly int readPathSessionRecordBudget;
    private readonly int readPathPassRecordBudget;
    private int projectionPassInFlight;

    /// <summary>
    /// Initializes a relational <see cref="CanonSessionProjectorEngine"/> projecting typed session
    /// rows from the normalized relational store.
    /// </summary>
    /// <param name="reader">Relational reader supplying sessions, records, and typed children.</param>
    /// <param name="projections">Typed projection store receiving session, context, schema, timeline, and finding rows.</param>
    /// <param name="operationalStore">Operational store backing the token counter and projection watermarks.</param>
    /// <param name="pricing">Model rate-table repository used when resolving context windows.</param>
    /// <param name="tokenizer">Local tokenizer that counts each text under the model it was sent to (D17).</param>
    /// <param name="readPathSessionRecordBudget">Per-session record count a read-path pass may rebuild before deferring the session.</param>
    /// <param name="readPathPassRecordBudget">Total record count one read-path pass may rebuild before deferring every later session.</param>
    public CanonSessionProjectorEngine(
        IRelationalStoreReader reader,
        IProjectionStore projections,
        IOperationalStore operationalStore,
        IPricingRepository pricing,
        ITextTokenizer tokenizer,
        int readPathSessionRecordBudget = DefaultReadPathSessionRecordBudget,
        int readPathPassRecordBudget = DefaultReadPathPassRecordBudget)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(operationalStore);
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentOutOfRangeException.ThrowIfLessThan(readPathSessionRecordBudget, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(readPathPassRecordBudget, 1);

        this.reader = reader;
        this.projections = projections;
        this.operationalStore = operationalStore;
        this.pricing = pricing;
        this.tokenizer = tokenizer;
        this.readPathSessionRecordBudget = readPathSessionRecordBudget;
        this.readPathPassRecordBudget = readPathPassRecordBudget;
    }

    [GeneratedRegex(@"^invoke_agent(?:[ :]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex InvokeAgentNameRegex();

    /// <summary>
    /// Collapses same-turn twin observations across ID joins, one-to-one Codex counter pairs,
    /// exact-counter clusters, and evidenced Cursor overlap shares without mutating input records.
    /// </summary>
    /// <param name="records">Candidate canonical records.</param>
    /// <param name="shareKey">Fallback session key used when a record has no <see cref="CanonicalRecord.SessionId"/>.</param>
    /// <returns>Deduplicated canonical records preserving input order.</returns>
    public static IReadOnlyList<CanonicalRecord> DedupeTwinTurns(
        IReadOnlyList<CanonicalRecord> records,
        string? shareKey = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return [];
        }

        HashSet<CanonicalRecord> dropped = new(ReferenceEqualityComparer.Instance);
        Dictionary<CanonicalRecord, CanonicalRecord> transplant = new(ReferenceEqualityComparer.Instance);
        HashSet<CanonicalRecord> idJoined = CollapseIdJoins(records, shareKey, dropped, transplant);
        HashSet<CanonicalRecord> codexPaired = PairCodexFileModelCalls(records, shareKey, dropped, transplant);

        Dictionary<string, List<CanonicalRecord>> byCounter = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal))
            {
                continue;
            }

            if (idJoined.Contains(record) || codexPaired.Contains(record))
            {
                continue;
            }

            if (IsUnreportedCounters(record.Tokens))
            {
                continue;
            }

            string key = CounterKey(record, shareKey);
            if (!byCounter.TryGetValue(key, out List<CanonicalRecord>? list))
            {
                list = [];
                byCounter[key] = list;
            }

            list.Add(record);
        }

        foreach (List<CanonicalRecord> turns in byCounter.Values)
        {
            turns.Sort(CompareRecordTimestamps);
            List<CanonicalRecord> cluster = [];
            foreach (CanonicalRecord record in turns)
            {
                if (cluster.Count > 0 && RecordTimestampMs(record) - RecordTimestampMs(cluster[0]) > TwinTurnMaxSkewMs)
                {
                    CollapseCluster(cluster, dropped, transplant);
                    cluster.Clear();
                }

                cluster.Add(record);
            }

            CollapseCluster(cluster, dropped, transplant);
        }

        Dictionary<CanonicalRecord, TokenUsage> tokenOverride = new(ReferenceEqualityComparer.Instance);
        CollapseOverlapFileJoins(records, shareKey, dropped, transplant, tokenOverride);

        return MergeDedupedOutput(records, dropped, transplant, tokenOverride);
    }


    /// <summary>
    /// Derives a complete <see cref="SessionSummaryRow"/> using this engine's cached token counter.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="records">Session records in timestamp order.</param>
    /// <returns>Derived session summary row.</returns>
    public SessionSummaryRow BuildSessionSummary(string sessionId, IReadOnlyList<CanonicalRecord> records)
    {
        _ = pricing.LoadPricingTables();
        CachedTokenCounter counter = CreateTokenCounter();

        // Each text counts under the model its own record was sent to; a record that names no
        // model counts under the tokenizer's labelled stand-in (D19).
        SessionSummaryRow row = BuildSessionSummary(sessionId, records, counter.Count);
        counter.Flush();
        return row;
    }

    /// <summary>
    /// Builds the engine's cached token counter over <see cref="IOperationalStore"/> on the
    /// relational path (decision D16), counting through the constructor's tokenizer. The legacy
    /// path, which has no operational store, counts without persisting now that the transitional
    /// counter constructor is gone.
    /// </summary>
    /// <returns>Cached token counter bound to the engine's tokenizer and operational store, or a counting-only counter.</returns>
    private CachedTokenCounter CreateTokenCounter() => new(operationalStore, tokenizer);

    /// <summary>
    /// Derives a complete <see cref="SessionSummaryRow"/> (including the summary totals,
    /// context analysis, schema ranking, timeline without raw attributes, and turns) from
    /// one session's records.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="records">Session records.</param>
    /// <param name="countTokens">Counts a text with the tokenizer of the model it was sent to.</param>
    /// <param name="isSubagent">Whether this session represents a delegated subagent execution.</param>
    /// <param name="parentSession">Parent session identifier when delegated.</param>
    /// <param name="agentName">Delegated agent name when available.</param>
    /// <param name="label">Display label override.</param>
    /// <param name="includedInParent">Whether this subagent's spend is included in its parent.</param>
    /// <returns>Projected <see cref="SessionSummaryRow"/>.</returns>
    public static SessionSummaryRow BuildSessionSummary(
        string sessionId,
        IReadOnlyList<CanonicalRecord> records,
        TextTokenCounter countTokens,
        bool isSubagent = false,
        string? parentSession = null,
        string? agentName = null,
        string? label = null,
        bool includedInParent = false) =>
        SessionSummaryBuilder.Build(
            sessionId,
            records,
            countTokens,
            isSubagent,
            parentSession,
            agentName,
            label,
            includedInParent);


    /// <summary>
    /// Returns <see langword="true"/> when the backing store already holds a complete, current
    /// session projection: it matches the <c>records</c> write marker, schema version, and
    /// detector version, and no session a read pass deferred is still dirty.
    /// </summary>
    /// <returns><see langword="true"/> when no maintenance pass is needed.</returns>
    /// <remarks>
    /// This is the maintenance gate: <c>kyber build</c> and the job scheduler skip their pass only
    /// when it holds, so a detector-version change or a deferred session always reaches a
    /// maintenance pass.
    /// </remarks>
    public bool IsProjectionCurrent()
    {
        return IsRelationalProjectionCurrent(forReads: false);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the read path has nothing to project: the stored
    /// projection matches the <c>records</c> write marker and schema version.
    /// </summary>
    /// <returns><see langword="true"/> when a read-path pass would have no new records to project.</returns>
    /// <remarks>
    /// A stale detector stamp and sessions a read pass deferred do not count here: rebuilding
    /// every session for a detector change and rebuilding deferred sessions is the maintenance
    /// pass's work, so a request never restarts a whole-store rebuild.
    /// </remarks>
    public bool IsProjectionCurrentForReads()
    {
        return IsRelationalProjectionCurrent(forReads: true);
    }

    private bool IsRelationalProjectionCurrent(bool forReads)
    {
        long currentMarker = operationalStore!.GetWriteMarker();
        ProjectionStateView? state = operationalStore.GetProjectionState();
        if (state is null)
        {
            return currentMarker == 0 && reader!.CountRecords() == 0 && projections!.ListSessionProjections().Count == 0;
        }

        if (state.RecordsMarker != currentMarker
            || state.ProjectionSchemaVersion != SqliteRelationalStore.SchemaVersion)
        {
            return false;
        }

        if (!forReads && (!HasCurrentProjectionEnvironment() || HasOutstandingDirtyKeys()))
        {
            return false;
        }

        return reader!.CountRecords() == 0 || projections!.ListSessionProjections().Count > 0;
    }

    /// <summary>True when the stored dirty state still names sessions to rebuild (or is wildcard).</summary>
    private bool HasOutstandingDirtyKeys()
    {
        DirtyProjectionState? dirty = operationalStore!.GetProjectionDirtyState();
        return dirty is not null && (dirty.IsWildcard || dirty.Keys is null || dirty.Keys.Count > 0);
    }

    /// <summary>
    /// Runs an incremental session projection pass over the store, rebuilding
    /// only session keys dirtied since the previous pass (or all keys on a full/first pass).
    /// </summary>
    /// <remarks>
    /// This is the maintenance entry: the CLI's <c>kyber build</c>, the refresh engine, the
    /// clean service, and the job scheduler call it, and it never defers a dirty session.
    /// </remarks>
    /// <returns>Summary of built, skipped, and pruned sessions plus emitted finding count.</returns>
    public ProjectionPassReport ProjectIncremental()
    {
        if (!TryBeginProjectionPass())
        {
            return RefusedProjectionPassReport();
        }

        try
        {
            return ProjectIncrementalRelational(forReads: false);
        }
        finally
        {
            EndProjectionPass();
        }
    }

    /// <summary>
    /// Runs the read-path variant of the incremental projection pass: the same attribution-driven
    /// identities and skip decisions, but a dirty session whose record count exceeds the
    /// per-session budget is deferred rather than rebuilt, stays dirty in the write-back, and is
    /// listed in <see cref="ProjectionPassReport.DeferredKeys"/> for a maintenance pass to
    /// rebuild later.
    /// </summary>
    /// <remarks>
    /// The entry exists so a request path never pays for a whole-store record read or an
    /// oversized rebuild: it reads full records only for the sessions it rebuilds. When another
    /// pass is already in flight the call returns a zero report instead of waiting.
    /// </remarks>
    /// <returns>Summary of built, skipped, and pruned sessions, emitted findings, and deferred keys.</returns>
    public ProjectionPassReport ProjectIncrementalForReads()
    {
        if (!TryBeginProjectionPass())
        {
            return RefusedProjectionPassReport();
        }

        try
        {
            return ProjectIncrementalRelational(forReads: true);
        }
        finally
        {
            EndProjectionPass();
        }
    }

    /// <summary>Claims the single projection pass slot; <see langword="false"/> when a pass is already in flight.</summary>
    private bool TryBeginProjectionPass() => Interlocked.CompareExchange(ref projectionPassInFlight, 1, 0) == 0;

    /// <summary>Releases the single projection pass slot; called from <c>finally</c> so a faulted pass cannot leave it claimed.</summary>
    private void EndProjectionPass() => Interlocked.Exchange(ref projectionPassInFlight, 0);

    /// <summary>The report a pass entry returns when another pass is already in flight.</summary>
    private static ProjectionPassReport RefusedProjectionPassReport() => new(
        Built: 0,
        Skipped: 0,
        Pruned: 0,
        Findings: 0,
        RebuiltKeys: [],
        SkippedKeys: [],
        DeferredKeys: null);

    /// <summary>
    /// True when the stored projection was built under this build's schema version and with its
    /// detector version (<see cref="SqliteRelationalStore.DetectorVersion"/>, stamped in
    /// <c>store_state.detector_version</c>); a missing row on either side reads as not current.
    /// </summary>
    private bool HasCurrentProjectionEnvironment()
    {
        ProjectionStateView? projectionState = operationalStore!.GetProjectionState();
        StoreStateView? storeState = operationalStore.GetStoreState();
        return projectionState is not null
            && projectionState.ProjectionSchemaVersion == SqliteRelationalStore.SchemaVersion
            && storeState is not null
            && storeState.DetectorVersion == SqliteRelationalStore.DetectorVersion;
    }

    /// <summary>
    /// Writes the projection watermark at <paramref name="recordsMarker"/> under the current schema
    /// version and, when <paramref name="stampDetector"/> is set, then stamps the current detector
    /// version in <c>store_state</c>.
    /// </summary>
    /// <param name="recordsMarker">Write marker the stored projection now covers.</param>
    /// <param name="stampDetector">
    /// <see langword="true"/> only for a full maintenance pass, the one pass that rebuilt every
    /// session under this build's detectors. A budget-bounded read pass never stamps, so a stale
    /// stamp keeps the store out of date until a maintenance pass rebuilds the sessions it deferred.
    /// </param>
    /// <remarks>
    /// The watermark is written first, so a failure between the two writes leaves the detector
    /// stamp stale and the next maintenance pass runs in full rather than skipping a session.
    /// </remarks>
    private void StampProjectionState(long recordsMarker, bool stampDetector) =>
        StampProjectionState(operationalStore!, recordsMarker, stampDetector);

    /// <summary>
    /// Writes the projection watermark and, when <paramref name="stampDetector"/> is set, the
    /// detector version on <paramref name="operationalStore"/>, exactly as a projection pass does;
    /// see <see cref="StampProjectionState(long, bool)"/>. A caller writes the dirty state first.
    /// </summary>
    /// <param name="operationalStore">Store whose projection watermark is written.</param>
    /// <param name="recordsMarker">Write marker the stored projection now covers.</param>
    /// <param name="stampDetector">Whether the stamp stands for a completed full maintenance pass.</param>
    internal static void StampProjectionState(IOperationalStore operationalStore, long recordsMarker, bool stampDetector)
    {
        operationalStore.SetProjectionState(new ProjectionStateView(SqliteRelationalStore.SchemaVersion, recordsMarker));
        if (!stampDetector)
        {
            return;
        }

        StoreStateView? storeState = operationalStore.GetStoreState();
        if (storeState is null || storeState.DetectorVersion != SqliteRelationalStore.DetectorVersion)
        {
            operationalStore.SetStoreState(new StoreStateView(
                storeState?.SchemaVersion ?? SqliteRelationalStore.SchemaVersion,
                SqliteRelationalStore.DetectorVersion));
        }
    }


    /// <summary>
    /// Runs an incremental relational session projection pass: rebuilds typed
    /// <see cref="StoredSessionProjection"/> rows (context, schema, timeline, turns, requests,
    /// and findings) for session keys dirtied since the previous pass, prunes stored projections
    /// whose records disappeared, and records the projection watermark.
    /// </summary>
    /// <returns>Summary of built, skipped, and pruned sessions plus emitted finding count.</returns>
    /// <remarks>
    /// <para>
    /// A relational projection row keys into the <c>session</c> table, so a session key whose
    /// canonical identity decorates its identifier (a harness-split key shared by two harnesses)
    /// is skipped rather than written: the relational ingest schema does not yet model
    /// harness-split or subagent session rows. Subagent and auxiliary involvement is recorded as
    /// timeline marks on the parent row instead.
    /// </para>
    /// <para>
    /// Every record-changing write moves <c>store_write_marker</c> and records its sessions as
    /// dirty, so a pass compares the marker with its stored watermark to find new records. A
    /// write that lands while the pass runs is left dirty for the next pass; see
    /// <see cref="StampMidPassWrites"/>.
    /// </para>
    /// </remarks>
    private ProjectionPassReport ProjectIncrementalRelational(bool forReads)
    {
        _ = pricing.LoadPricingTables();

        long currentMarker = operationalStore!.GetWriteMarker();
        ProjectionStateView? previousState = operationalStore.GetProjectionState();
        DirtyProjectionState? dirtyState = operationalStore.GetProjectionDirtyState();
        IncrementalPlan incremental = ResolveIncrementalPlan(forReads, previousState, dirtyState, currentMarker);

        ProjectionPlan plan = BuildProjectionPlan(forReads, incremental.CanIncremental, incremental.DirtySet);

        CachedTokenCounter counter = new(operationalStore, tokenizer);
        (int builtCount, int skippedCount, int findingsCount, List<string> rebuiltKeys) = RebuildPlannedSessions(plan, counter);
        counter.Flush();

        int prunedCount = PruneMissingProjections(plan.LiveSessionIds);

        RebuildRunExecutionProjection();

        long finalMarker = operationalStore.GetWriteMarker();
        if (finalMarker > currentMarker)
        {
            StampMidPassWrites(currentMarker, finalMarker, plan.DeferredKeys, incremental.StampDetector);
        }
        else
        {
            CompleteProjectionPass(finalMarker, plan.DeferredKeys, incremental.StampDetector);
        }

        return new ProjectionPassReport(
            Built: builtCount,
            Skipped: skippedCount,
            Pruned: prunedCount,
            Findings: findingsCount,
            RebuiltKeys: rebuiltKeys,
            SkippedKeys: plan.SkippedKeys,
            DeferredKeys: forReads ? plan.DeferredKeys : null);
    }

    private sealed record IncrementalPlan(bool CanIncremental, HashSet<string>? DirtySet, bool StampDetector);

    private sealed record ProjectionPlan(
        SessionIdentities Identities,
        Dictionary<string, CollectedSession> SessionsByKey,
        List<string> RebuildKeys,
        List<string> DeferredKeys,
        List<string> SkippedKeys,
        HashSet<string> LiveSessionIds);

    private IncrementalPlan ResolveIncrementalPlan(
        bool forReads,
        ProjectionStateView? previousState,
        DirtyProjectionState? dirtyState,
        long currentMarker)
    {
        // A maintenance pass on a stale detector stamp rebuilds every session; a read pass only
        // needs the projection schema to match, so a stale stamp never makes a request restart a
        // whole-store rebuild from the first session.
        bool environmentCurrent = forReads
            ? previousState is not null && previousState.ProjectionSchemaVersion == SqliteRelationalStore.SchemaVersion
            : HasCurrentProjectionEnvironment();

        // A read pass with no write since its watermark and nothing dirty has nothing new to
        // project: it continues incrementally over an empty dirty set.
        bool nothingDirtyForReads = forReads
            && previousState is not null
            && dirtyState is null
            && previousState.RecordsMarker == currentMarker;

        bool canIncremental = environmentCurrent
            && previousState is not null
            && (nothingDirtyForReads
                || (dirtyState is not null
                    && dirtyState.FromSeq == previousState.RecordsMarker
                    && dirtyState.ToSeq == currentMarker
                    && !dirtyState.IsWildcard
                    && dirtyState.Keys is not null));

        HashSet<string>? dirtySet = canIncremental
            ? new HashSet<string>(dirtyState?.Keys ?? [], StringComparer.Ordinal)
            : null;

        // Only a full maintenance pass rebuilt every session under this build's detectors.
        bool stampDetector = !forReads && !canIncremental;

        return new IncrementalPlan(canIncremental, dirtySet, stampDetector);
    }

    private ProjectionPlan BuildProjectionPlan(bool forReads, bool canIncremental, HashSet<string>? dirtySet)
    {
        // Attribution rows replace the whole-store record read: identities, skip decisions, and
        // the per-session and per-pass record budgets are planned from (session key, harness,
        // count) rows, and full records are read only for the keys this pass actually rebuilds.
        IReadOnlyList<StoredSessionAttribution> attributions = reader!.SessionAttributions();
        Dictionary<string, List<string>> harnessesByKey = new(StringComparer.Ordinal);
        Dictionary<string, int> recordCountByKey = new(StringComparer.Ordinal);
        foreach (StoredSessionAttribution attribution in attributions)
        {
            if (string.IsNullOrEmpty(attribution.SessionKey))
            {
                continue;
            }

            if (!harnessesByKey.TryGetValue(attribution.SessionKey, out List<string>? harnesses))
            {
                harnesses = [];
                harnessesByKey[attribution.SessionKey] = harnesses;
                recordCountByKey[attribution.SessionKey] = 0;
            }

            harnesses.Add(attribution.HarnessId);
            recordCountByKey[attribution.SessionKey] += attribution.RecordCount;
        }

        List<string> sessionKeys = [.. harnessesByKey.Keys.OrderBy(static key => key, StringComparer.Ordinal)];
        SessionIdentities identities = new(
            attributions
                .Where(static attribution => !string.IsNullOrEmpty(attribution.SessionKey))
                .Select(static attribution => (attribution.SessionKey, attribution.HarnessId)));

        Dictionary<string, CollectedSession> sessionsByKey = new(StringComparer.Ordinal);
        foreach (CollectedSession session in reader.Sessions())
        {
            sessionsByKey.TryAdd(session.SessionKey, session);
        }

        HashSet<string> storedProjectionIds = new(
            projections!.ListSessionProjections().Select(static projection => projection.SessionId),
            StringComparer.Ordinal);

        List<string> rebuildKeys = [];
        List<string> deferredKeys = [];
        List<string> skippedKeys = [];
        int passRecordCount = 0;
        bool passBudgetExhausted = false;
        foreach (string key in sessionKeys)
        {
            if (canIncremental
                && dirtySet is not null
                && !dirtySet.Contains(key)
                && PriorIdsOf(key, harnessesByKey[key], identities).All(storedProjectionIds.Contains))
            {
                skippedKeys.Add(key);
                continue;
            }

            if (forReads && recordCountByKey[key] > readPathSessionRecordBudget)
            {
                deferredKeys.Add(key);
                continue;
            }

            // The pass budget bounds the whole read pass, so a cold store of thousands of small
            // sessions cannot rebuild every one of them inside a request: once the running total
            // would cross it, the crossing key and every later key defer to later passes. The
            // first candidate is always admitted so a pass always rebuilds at least one session
            // and repeated passes converge.
            if (forReads && (passBudgetExhausted
                || (rebuildKeys.Count > 0 && passRecordCount + recordCountByKey[key] > readPathPassRecordBudget)))
            {
                passBudgetExhausted = true;
                deferredKeys.Add(key);
                continue;
            }

            rebuildKeys.Add(key);
            passRecordCount += recordCountByKey[key];
        }

        // A stored projection survives the pass when its session is still attributed records —
        // built, skipped-clean, or deferred — so only sessions whose records disappeared are
        // pruned, and a deferred session keeps its last stored projection for reads to answer.
        HashSet<string> liveSessionIds = new(StringComparer.Ordinal);
        foreach (string key in sessionKeys)
        {
            foreach (string id in PriorIdsOf(key, harnessesByKey[key], identities))
            {
                liveSessionIds.Add(id);
            }
        }

        return new ProjectionPlan(identities, sessionsByKey, rebuildKeys, deferredKeys, skippedKeys, liveSessionIds);
    }

    private (
        int BuiltCount,
        int SkippedCount,
        int FindingsCount,
        List<string> RebuiltKeys) RebuildPlannedSessions(
        ProjectionPlan plan,
        CachedTokenCounter counter)
    {
        int builtCount = 0;
        int skippedCount = plan.SkippedKeys.Count;
        int findingsCount = 0;
        List<string> rebuiltKeys = [];

        foreach (string key in plan.RebuildKeys)
        {
            // Each session's records are read when its turn comes and released after it is built:
            // reading the whole store at once outgrew the sqlite3 output a string can hold on a
            // real store, and held every session's text in memory for the length of the pass.
            List<CanonicalRecord> hydrated = [.. reader!.SessionRecordsForRebuild(key).Select(CanonRecordHydration.Hydrate)];
            hydrated.Sort(CompareRecordTimestamps);
            Dictionary<string, List<CanonicalRecord>> grouped = GroupByCanonicalHarness(hydrated);
            if (grouped.Count == 0)
            {
                skippedCount++;
                continue;
            }

            foreach (KeyValuePair<string, List<CanonicalRecord>> group in grouped)
            {
                string harness = group.Key;
                List<CanonicalRecord> groupRecords = group.Value;
                if (groupRecords.Count == 0 || !HasEvidence(groupRecords))
                {
                    skippedCount++;
                    continue;
                }

                IReadOnlyList<CanonicalRecord> merged = DedupeTwinTurns(groupRecords, key);
                if (merged.Count == 0 || !HasEvidence(merged))
                {
                    skippedCount++;
                    continue;
                }

                string sessionId = plan.Identities.Claim(key, harness);
                if (!plan.SessionsByKey.ContainsKey(sessionId))
                {
                    skippedCount++;
                    continue;
                }

                // Same rule as the summary path: each text counts under its own record's model,
                // and a record naming no model uses the stand-in.
                StoredSessionProjection projection = BuildStoredSessionProjection(
                    sessionId,
                    merged,
                    counter.Count,
                    plan.SessionsByKey[sessionId].Title);
                projections!.ReplaceSessionProjection(projection);
                builtCount++;

                findingsCount += RebuildSessionFindingsRelational(sessionId, merged, plan.Identities);
            }

            rebuiltKeys.Add(key);
        }

        return (builtCount, skippedCount, findingsCount, rebuiltKeys);
    }

    private int PruneMissingProjections(HashSet<string> liveSessionIds)
    {
        int prunedCount = 0;
        foreach (StoredSessionProjection stored in projections!.ListSessionProjections())
        {
            if (liveSessionIds.Contains(stored.SessionId))
            {
                continue;
            }

            projections.DeleteSessionProjection(stored.SessionId);
            projections.DeleteFindingsForSession(stored.SessionId);
            prunedCount++;
        }

        return prunedCount;
    }

    private void CompleteProjectionPass(long finalMarker, List<string> deferredKeys, bool stampDetector)
    {
        if (deferredKeys.Count > 0)
        {
            // Deferred keys stay dirty at the watermark this pass then writes, so the next read
            // pass continues with them and the maintenance pass sees the projection as not
            // current until it rebuilds them. Only a read pass defers, so this never stamps the
            // detector version. The dirty state is written before the watermark: a crash between
            // the two leaves a range that does not start at the stored watermark, which forces a
            // full pass, never a watermark that hides the deferred keys.
            operationalStore!.SetProjectionDirtyState(new DirtyProjectionState(
                FromSeq: finalMarker,
                ToSeq: finalMarker,
                Keys: [.. deferredKeys.Order(StringComparer.Ordinal)],
                IsWildcard: false));
        }
        else
        {
            // Dirty state before the watermark, as above: a crash between them leaves the old
            // watermark with nothing dirty, which forces a full pass.
            operationalStore!.ClearProjectionDirtyState();
        }

        StampProjectionState(finalMarker, stampDetector);
    }


    /// <summary>
    /// Ends a pass during which a record-changing write moved the marker: stamps the marker the
    /// pass read at its start and keeps the writes since then dirty, so the next pass rebuilds
    /// them.
    /// </summary>
    /// <param name="currentMarker">Write marker the pass read at its start.</param>
    /// <param name="finalMarker">Write marker read at the end of the pass.</param>
    /// <param name="deferredKeys">Keys this pass deferred.</param>
    /// <param name="stampDetector">Whether this pass may stamp the detector version; see <see cref="StampProjectionState"/>.</param>
    /// <remarks>
    /// A per-key or whole-store read cannot have seen a record written after it, so stamping
    /// <paramref name="finalMarker"/> would mark the projection current while a session misses
    /// that record. The writes merged their keys into the stored dirty range,
    /// so when that range still ends at <paramref name="finalMarker"/> it is rewritten to start
    /// at <paramref name="currentMarker"/> with the deferred keys added, or kept wildcard. Any
    /// other stored state, including a write landing between these reads and the rewrite, leaves
    /// a range whose end differs from the marker, which forces a full pass: every race here
    /// degrades to a full pass, never to a skipped session. The dirty state is written before the
    /// watermark, so a crash between the two leaves the previous watermark under a range that
    /// starts at <paramref name="currentMarker"/>, or under no range. The crash degrades to a full
    /// pass or a correct incremental rebuild over the retained keys, never to a skipped session.
    /// </remarks>
    private void StampMidPassWrites(long currentMarker, long finalMarker, IReadOnlyCollection<string> deferredKeys, bool stampDetector)
    {
        WriteMidPassDirtyState(currentMarker, finalMarker, deferredKeys);
        StampProjectionState(currentMarker, stampDetector);
    }

    /// <summary>Rewrites the dirty state <see cref="StampMidPassWrites"/> leaves behind it.</summary>
    private void WriteMidPassDirtyState(long currentMarker, long finalMarker, IReadOnlyCollection<string> deferredKeys)
    {
        DirtyProjectionState? stored = operationalStore!.GetProjectionDirtyState();
        if (stored is null || stored.ToSeq != finalMarker)
        {
            operationalStore.ClearProjectionDirtyState();
            return;
        }

        if (stored.IsWildcard || stored.Keys is null)
        {
            operationalStore.SetProjectionDirtyState(new DirtyProjectionState(
                FromSeq: currentMarker,
                ToSeq: finalMarker,
                Keys: null,
                IsWildcard: true));
            return;
        }

        HashSet<string> keys = new(stored.Keys, StringComparer.Ordinal);
        keys.UnionWith(deferredKeys);
        operationalStore.SetProjectionDirtyState(new DirtyProjectionState(
            FromSeq: currentMarker,
            ToSeq: finalMarker,
            Keys: [.. keys.Order(StringComparer.Ordinal)],
            IsWildcard: false));
    }

    /// <summary>
    /// Rebuilds typed run grouping (<c>run*</c>) and execution linkage (<c>execution*</c>) rows
    /// from the stored session projections, replacing legacy parent-linkage and
    /// payload columns with nullable typed child rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Explicit run identifiers and parent-session attributes are not observable through
    /// <see cref="IRelationalStoreReader"/> (a <see cref="CollectedRecord"/> carries no raw
    /// attribute bag), so every run groups by derivation (<c>derived</c>/
    /// <c>session_fallback</c>) and every execution is a root unless its stored projection names
    /// a parent session that is itself projected. Parent linkage is then the nullable typed
    /// <c>execution_parent</c> row: absent means no parent, never a JSON status object.
    /// </para>
    /// <para>
    /// The rebuild covers every stored session projection (not just the sessions this pass built)
    /// so repeated passes converge, then deletes runs and executions whose sessions are gone.
    /// </para>
    /// </remarks>
    private void RebuildRunExecutionProjection()
    {
        Dictionary<string, CollectedSession> sessionsByKey = new(StringComparer.Ordinal);
        foreach (CollectedSession session in reader!.Sessions())
        {
            sessionsByKey.TryAdd(session.SessionKey, session);
        }

        List<StoredSessionProjection> stored = [.. projections!.ListSessionProjections()];
        stored.Sort(static (left, right) => string.CompareOrdinal(left.SessionId, right.SessionId));

        Dictionary<string, List<RunMember>> membersByRun = new(StringComparer.Ordinal);
        Dictionary<string, string> harnessByRun = new(StringComparer.Ordinal);
        foreach (StoredSessionProjection projection in stored)
        {
            if (!sessionsByKey.TryGetValue(projection.SessionId, out CollectedSession? session))
            {
                continue;
            }

            string harness = CanonPricing.NormalizeHarnessName(session.HarnessId);
            if (CanonPricing.IsExcludedHarnessIdentity(harness))
            {
                continue;
            }

            string runId = $"derived:{harness}:{CanonPipelineShared.StripHarnessPrefix(harness, projection.SessionId)}";
            if (!membersByRun.TryGetValue(runId, out List<RunMember>? members))
            {
                members = [];
                membersByRun[runId] = members;
                harnessByRun[runId] = harness;
            }

            members.Add(new RunMember(projection, session));
        }

        HashSet<string> projectedSessionIds = new(stored.Select(static projection => projection.SessionId), StringComparer.Ordinal);

        List<StoredRun> runs = new(membersByRun.Count);
        List<StoredExecution> executions = [];
        foreach (string runId in membersByRun.Keys.OrderBy(static id => id, StringComparer.Ordinal))
        {
            List<RunMember> members = membersByRun[runId];
            members.Sort(static (left, right) =>
            {
                int started = string.CompareOrdinal(left.Projection.StartedAtUtc, right.Projection.StartedAtUtc);
                return started != 0 ? started : string.CompareOrdinal(left.Projection.SessionId, right.Projection.SessionId);
            });

            string? startedAt = null;
            string? endedAt = null;
            foreach (RunMember member in members)
            {
                if (startedAt is null || string.CompareOrdinal(member.Projection.StartedAtUtc, startedAt) < 0)
                {
                    startedAt = member.Projection.StartedAtUtc;
                }

                if (endedAt is null || string.CompareOrdinal(member.Projection.EndedAtUtc, endedAt) > 0)
                {
                    endedAt = member.Projection.EndedAtUtc;
                }
            }

            RunMember earliest = members[0];
            string? workingDirectory = earliest.Session.Workspace?.WorkingDirectory ?? earliest.Session.Workspace?.Repository;
            runs.Add(new StoredRun(
                RunId: runId,
                HarnessId: harnessByRun[runId],
                GroupingBasis: "derived",
                GroupingRule: "session_fallback",
                ExecutionCount: members.Count,
                Label: earliest.Projection.Label,
                WorkingDirectory: workingDirectory,
                StartedAtUtc: startedAt,
                EndedAtUtc: endedAt));

            foreach (RunMember member in members)
            {
                string? parentExecutionId = member.Projection.ParentSessionId is not null
                    && projectedSessionIds.Contains(member.Projection.ParentSessionId)
                    ? $"exec:{member.Projection.ParentSessionId}"
                    : null;
                executions.Add(new StoredExecution(
                    ExecutionId: $"exec:{member.Projection.SessionId}",
                    RunId: runId,
                    HarnessId: harnessByRun[runId],
                    IsRoot: !member.Projection.IsSubagent && parentExecutionId is null,
                    SessionId: member.Projection.SessionId,
                    ParentExecutionId: parentExecutionId,
                    AgentName: member.Projection.AgentName,
                    StartedAtUtc: member.Projection.StartedAtUtc,
                    EndedAtUtc: member.Projection.EndedAtUtc));
            }
        }

        projections.ReplaceRuns(runs);

        // ReplaceExecutions upserts by execution id without removing executions that no longer
        // belong to a live run, so each live run's executions are cleared before the new set lands.
        foreach (StoredRun run in runs)
        {
            projections.DeleteExecutionsForRun(run.RunId);
        }

        projections.ReplaceExecutions(executions);

        HashSet<string> liveRuns = new(runs.Select(static run => run.RunId), StringComparer.Ordinal);
        foreach (StoredRun existing in projections.ListRuns())
        {
            if (!liveRuns.Contains(existing.RunId))
            {
                projections.DeleteExecutionsForRun(existing.RunId);
                projections.DeleteRun(existing.RunId);
            }
        }
    }

    /// <summary>Strips a leading <c>{harness}:</c> prefix from a session seed, when present.</summary>
    /// <param name="harness">Canonical harness identifier.</param>
    /// <param name="seed">Session identifier seed.</param>
    /// <returns>Seed without the harness prefix.</returns>
    /// <summary>A stored session projection paired with its relational session metadata for run grouping.</summary>
    /// <param name="Projection">Stored projected session summary.</param>
    /// <param name="Session">Relational session metadata.</param>
    private sealed record RunMember(StoredSessionProjection Projection, CollectedSession Session);


    internal sealed record DroidSessionSplit(
        List<CanonicalRecord> TurnRecords,
        List<CanonicalRecord> CounterRecords,
        List<CanonicalRecord> FileToolRecords);

    internal class SessionIdentities
    {
        private readonly Dictionary<string, Dictionary<string, string>> ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> assignedSessionIds = new(StringComparer.Ordinal);

        public SessionIdentities(IEnumerable<(string Key, string Harness)> pairs)
        {
            Dictionary<string, HashSet<string>> harnessesByKey = new(StringComparer.Ordinal);
            foreach ((string key, string rawHarness) in pairs)
            {
                string? harness = CanonPricing.CanonicalHarnessId(rawHarness);
                if (harness is null)
                {
                    continue;
                }

                if (!harnessesByKey.TryGetValue(key, out HashSet<string>? harnesses))
                {
                    harnesses = new HashSet<string>(StringComparer.Ordinal);
                    harnessesByKey[key] = harnesses;
                }

                harnesses.Add(harness);
            }

            List<(string Key, List<string> Harnesses)> split = [];
            foreach (KeyValuePair<string, HashSet<string>> kv in harnessesByKey)
            {
                if (kv.Value.Count == 1)
                {
                    Assign(kv.Key, kv.Value.First(), kv.Key);
                }
                else
                {
                    split.Add((kv.Key, kv.Value.OrderBy(static h => h, StringComparer.Ordinal).ToList()));
                }
            }

            split.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
            foreach ((string key, List<string> harnesses) in split)
            {
                foreach (string harness in harnesses)
                {
                    Claim(key, harness);
                }
            }
        }

        public string? IdFor(string key, string harness)
        {
            string canonical = CanonPricing.NormalizeHarnessName(harness);
            return ids.TryGetValue(key, out Dictionary<string, string>? byHarness)
                && byHarness.TryGetValue(canonical, out string? id)
                ? id
                : null;
        }

        public string Claim(string key, string harness)
        {
            string canonical = CanonPricing.NormalizeHarnessName(harness);
            string? existing = IdFor(key, canonical);
            if (existing is not null)
            {
                return existing;
            }

            string candidate = $"{canonical}:{key}";
            while (assignedSessionIds.Contains(candidate))
            {
                candidate = $"{canonical}:{candidate}";
            }

            Assign(key, canonical, candidate);
            return candidate;
        }

        private void Assign(string key, string harness, string id)
        {
            if (!ids.TryGetValue(key, out Dictionary<string, string>? byHarness))
            {
                byHarness = new Dictionary<string, string>(StringComparer.Ordinal);
                ids[key] = byHarness;
            }

            byHarness[harness] = id;
            assignedSessionIds.Add(id);
        }
    }

    private int RebuildSessionFindingsRelational(
        string sessionId,
        IReadOnlyList<CanonicalRecord> sessionRecords,
        SessionIdentities identities)
    {
        projections!.DeleteFindingsForSession(sessionId);
        OutcomeBlock outcome = CanonFindings.DeriveOutcome(sessionRecords);
        IReadOnlyList<Finding> findings = CanonFindings.DetectFindings(new DetectFindingsInput(
            RunId: sessionId,
            SessionId: sessionId,
            Records: sessionRecords,
            Outcome: outcome,
            SessionIdentityLookup: identities.IdFor));
        if (findings.Count > 0)
        {
            projections.ReplaceFindings(findings.Select(ToStoredFinding));
        }

        return findings.Count;
    }
}

/// <summary>Builds a session summary row from one session's records.</summary>
internal static class SessionSummaryBuilder
{
    internal static SessionSummaryRow Build(
        string sessionId,
        IReadOnlyList<CanonicalRecord> records,
        TextTokenCounter countTokens,
        bool isSubagent,
        string? parentSession,
        string? agentName,
        string? label,
        bool includedInParent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(countTokens);
        if (records.Count == 0)
        {
            throw new ArgumentException("At least one record is required to build a session summary.", nameof(records));
        }

        SummaryInputs inputs = PrepareInputs(records);
        bool paired = inputs.Paired;
        List<CanonicalRecord> turnRecords = inputs.TurnRecords;
        IReadOnlyList<CanonicalRecord> structureRecords = inputs.StructureRecords;
        List<CanonicalRecord> counterRecords = inputs.CounterRecords;
        string harness = inputs.Harness;
        List<CanonicalRecord> measuredTurns = inputs.MeasuredTurns;

        List<ContextTurn> contextTurns = BuildContextTurns(measuredTurns);

        ContextParts contextParts = BuildContextParts(records, inputs, contextTurns, countTokens);

        ToolsParts tools = BuildToolsParts(records, inputs, contextParts.Measurability, countTokens);

        CostParts cost = BuildCostParts(paired, turnRecords, counterRecords);

        (long totalInput, long totalOutput, long totalCacheRead, long totalCacheCreation) = SumTokenTotals(contextParts.TotalRecords);

        List<string> turnModels = ModelsOf(turnRecords);
        List<string> models = turnModels.Count > 0 ? turnModels : ModelsOf(counterRecords);
        (string started, string ended) = TimestampBounds(records);

        (int? toolCalls, List<string>? toolsInvoked) = ResolveToolUsage(
            tools.ToolRecords,
            tools.ToolSourceRecords,
            contextParts.Measurability,
            harness);

        (List<string> offeredTools, bool hasDefParts) = CollectOfferedTools(records, countTokens);

        List<CanonicalRecord> requestRoots = structureRecords.Where(static r => r.ParentSpanId is null).ToList();
        Dictionary<string, List<CanonicalRecord>> turnsByRoot = TurnsByRequestRoot(structureRecords, turnRecords);
        bool isNested = requestRoots.Count > 0 && HasNesting(structureRecords);
        IReadOnlyList<CanonicalRecord> durationRecords = paired ? counterRecords : isNested ? requestRoots : structureRecords;
        long totalDurationMs = durationRecords.Sum(static r => r.DurationMs);

        TimelineParts timelineParts = BuildTimelineParts(structureRecords);

        SummaryIdentity identity = ResolveIdentity(records, sessionId, harness, isSubagent, parentSession, agentName, label);

        Dictionary<string, object?> summaryDict = BuildSummaryDict(
            turnRecords.Count,
            requestRoots.Count,
            totalDurationMs,
            models,
            cost.Result,
            contextParts.TotalRecords,
            contextParts.TotalMeasurability,
            totalInput,
            totalOutput,
            totalCacheRead,
            totalCacheCreation,
            toolCalls,
            toolsInvoked,
            hasDefParts,
            offeredTools,
            contextParts.Measurability,
            includedInParent);

        Dictionary<string, object?> contextDict = SerializeContextPayload(
            contextParts.Context,
            contextParts.Window.ContextLimitSource,
            measuredTurns,
            inputs.CounterInput,
            inputs.UnmeasuredTurns,
            contextParts.TotalMeasurability);

        List<Dictionary<string, object?>> turnEntries = BuildTurnEntries(turnRecords);
        List<Dictionary<string, object?>> requestEntries = BuildRequestEntries(requestRoots, turnsByRoot);
        List<Dictionary<string, object?>> reconciliation = BuildReconciliation(turnRecords);

        (object timelinePayload, object schemaPayload) = BuildAvailabilityPayloads(timelineParts.Timeline, tools.Schema, contextParts.Measurability);

        Problem[] problems = cost.Result.Ok || cost.Result.Problem is null ? [] : [cost.Result.Problem];

        Dictionary<string, object?> payloadDict = new(StringComparer.Ordinal)
        {
            ["id"] = sessionId,
            ["session_id"] = sessionId,
            ["harness"] = harness,
            ["label"] = identity.Label,
            ["agent_name"] = identity.AgentName,
            ["repo"] = identity.Repo,
            ["branch"] = identity.Branch,
            ["span_count"] = records.Count,
            ["summary"] = summaryDict,
            ["context"] = contextDict,
            ["tools"] = tools.ToolsPayload,
            ["schema"] = schemaPayload,
            ["turns"] = turnEntries,
            ["timeline"] = timelinePayload,
            ["requests"] = requestEntries,
            ["servers"] = tools.ServersPayload,
            ["coverage"] = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["schema"] = tools.Schema.Measurable ? 1 : 0,
                ["context"] = contextParts.Context.Measurable ? 1 : 0,
            },
            ["problems"] = problems,
            ["costBasisMismatch"] = cost.BasisMismatch,
            ["reconciliation"] = reconciliation,
            ["subagents"] = timelineParts.Subagents,
            ["auxiliary"] = timelineParts.Auxiliary,
            ["measurability"] = contextParts.Measurability ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        };

        string payloadJson = JsonSerializer.Serialize(payloadDict, ProjectionJsonOptions);
        JsonElement payloadElement = JsonSerializer.Deserialize<JsonElement>(payloadJson);
        string summaryJson = JsonSerializer.Serialize(summaryDict, ProjectionJsonOptions);

        return new SessionSummaryRow(
            SessionId: sessionId,
            Harness: harness,
            Label: identity.Label,
            IsSubagent: identity.IsSubagent,
            ParentSession: identity.ParentSession,
            AgentName: identity.AgentName,
            Repo: identity.Repo,
            Branch: identity.Branch,
            Started: started,
            Ended: ended,
            SummaryJson: summaryJson,
            ProblemsCount: problems.Length,
            ContextLimitSource: contextParts.Window.ContextLimitSource,
            Payload: payloadElement);
    }

    private sealed record ContextParts(
        ContextWindowResult Window,
        ContextAnalysis Context,
        Dictionary<string, JsonElement>? Measurability,
        IReadOnlyList<CanonicalRecord> TotalRecords,
        Dictionary<string, JsonElement>? TotalMeasurability);

    private static ContextParts BuildContextParts(
        IReadOnlyList<CanonicalRecord> records,
        SummaryInputs inputs,
        List<ContextTurn> contextTurns,
        TextTokenCounter countTokens)
    {
        IReadOnlyList<CanonicalRecord> windowRecords = inputs.ContextRecords.Count > 0 ? inputs.ContextRecords : records;
        ContextWindowResult window = CanonPricing.ContextLimitOf(windowRecords);
        long analysisLimit = window.ContextLimit > 0 ? window.ContextLimit : CanonPricing.DefaultContextLimit;

        Dictionary<string, JsonElement>? measurability = MergeMeasurability(records);
        IReadOnlyList<CanonicalRecord> totalRecords = inputs.CounterRecords.Count > 0 ? inputs.CounterRecords : inputs.TurnRecords;
        Dictionary<string, JsonElement>? totalMeasurability = MergeMeasurability(totalRecords);
        HashSet<string> carriedBuckets = CarriedBucketsOf(records);
        IReadOnlyDictionary<string, JsonElement>? compMeasurability = CompositionDeclaration(measurability, carriedBuckets);

        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            contextTurns,
            new ContextCountOptions(
                ContextLimit: analysisLimit,
                ContextLimitSource: window.ContextLimitSource,
                Measurability: compMeasurability),
            countTokens);

        return new ContextParts(window, context, measurability, totalRecords, totalMeasurability);
    }

    private sealed record ToolsParts(
        IReadOnlyList<CanonicalRecord> ToolSourceRecords,
        List<CanonicalRecord> ToolRecords,
        SchemaCostAnalysis Schema,
        List<Dictionary<string, object?>> ToolsPayload,
        List<Dictionary<string, object?>> ServersPayload);

    private static ToolsParts BuildToolsParts(
        IReadOnlyList<CanonicalRecord> records,
        SummaryInputs inputs,
        Dictionary<string, JsonElement>? measurability,
        TextTokenCounter countTokens)
    {
        List<ToolDefinition> definitions = ToolDefinitionsOf(records, countTokens);
        IReadOnlyList<CanonicalRecord> toolSourceRecords = inputs.Paired && inputs.Split.FileToolRecords.Count > 0
            ? inputs.Split.FileToolRecords
            : inputs.StructureRecords;
        SchemaCostAnalysis schema = CanonContextAnalysis.RankSchemas(
            definitions,
            inputs.TurnRecords.Count,
            InvocationsOf(toolSourceRecords),
            measurability: measurability);

        List<CanonicalRecord> toolRecords = toolSourceRecords.Where(static r => string.Equals(r.Op, "tool.invoke", StringComparison.Ordinal)).ToList();
        (Dictionary<string, int> invocationCounts, Dictionary<string, int> errorCounts) = CountToolInvocations(toolRecords);

        Dictionary<string, ToolDefinition> definitionsByName = new(StringComparer.Ordinal);
        foreach (ToolDefinition def in definitions)
        {
            definitionsByName.TryAdd(def.Name, def);
        }

        List<RankedTool> rankedTools = schema.Measurable
            ? [.. schema.Ranked]
            : definitions.Select(d => new RankedTool(d.Name, d.Server, d.Tokens * (d.TurnsResident ?? inputs.TurnRecords.Count), false)).ToList();

        List<Dictionary<string, object?>> toolsPayload = BuildToolsPayload(
            rankedTools,
            definitionsByName,
            invocationCounts,
            errorCounts,
            inputs.TurnRecords.Count);
        List<Dictionary<string, object?>> serversPayload = BuildServersPayload(
            schema,
            rankedTools,
            definitionsByName,
            invocationCounts);

        return new ToolsParts(toolSourceRecords, toolRecords, schema, toolsPayload, serversPayload);
    }

    private sealed record CostParts(CostTotalResult Result, CostBasisMismatch? BasisMismatch);

    private static CostParts BuildCostParts(
        bool paired,
        List<CanonicalRecord> turnRecords,
        List<CanonicalRecord> counterRecords)
    {
        List<CostBlock> costBlocks = [.. turnRecords.Select(static r => r.Cost), .. counterRecords.Select(static r => r.Cost)];
        List<CostBlock> pricedBlocks = costBlocks.Where(static b => b.Value.HasValue && double.IsFinite(b.Value.Value)).ToList();
        CostTotalResult costResult = CanonPricing.SumCosts(paired && pricedBlocks.Count > 0 ? pricedBlocks : costBlocks);
        CostBasisMismatch? costBasisMismatch = CanonPricing.CostBasisMismatchOf(costBlocks, costResult);
        return new CostParts(costResult, costBasisMismatch);
    }

    private sealed record TimelineParts(
        TimelineNode Timeline,
        IReadOnlyList<SubagentSession> Subagents,
        CostBlock Auxiliary);

    private static TimelineParts BuildTimelineParts(IReadOnlyList<CanonicalRecord> structureRecords)
    {
        TimelineNode timeline = CanonContextAnalysis.BuildTimeline(structureRecords);
        IReadOnlyList<SubagentSession> subagents = CanonContextAnalysis.SubagentSessions(timeline);
        CostBlock auxiliary = CanonContextAnalysis.AuxiliarySpend(timeline);
        return new TimelineParts(timeline, subagents, auxiliary);
    }

    private sealed record SummaryIdentity(
        string? AgentName,
        string Label,
        string? Repo,
        string? Branch,
        bool IsSubagent,
        string? ParentSession);

    private static SummaryIdentity ResolveIdentity(
        IReadOnlyList<CanonicalRecord> records,
        string sessionId,
        string harness,
        bool isSubagent,
        string? parentSession,
        string? agentName,
        string? label)
    {
        string? resolvedAgentName = agentName ?? AttributeOf(records[0], AgentNameKeys);
        string resolvedLabel = SessionLabelOf(records, label ?? records[0].Name, harness, sessionId);
        string? repo = AttributeOf(records[0], RepoKeys);
        string? branch = AttributeOf(records[0], BranchKeys);

        string? parentAttr = AttributeOf(records[0], ParentSessionKeys);
        bool resolvedIsSubagent = isSubagent || parentAttr is not null || AttributeOf(records[0], ["gen_ai.is_subagent", "is_subagent"]) is not null;
        string? resolvedParent = parentSession ?? parentAttr;
        return new SummaryIdentity(resolvedAgentName, resolvedLabel, repo, branch, resolvedIsSubagent, resolvedParent);
    }

    private sealed record SummaryInputs(
        DroidSessionSplit Split,
        bool Paired,
        List<CanonicalRecord> TurnRecords,
        IReadOnlyList<CanonicalRecord> StructureRecords,
        List<CanonicalRecord> CounterRecords,
        string Harness,
        List<CanonicalRecord> ContextRecords,
        List<CanonicalRecord> MeasuredTurns,
        int UnmeasuredTurns,
        long? CounterInput);

    private static SummaryInputs PrepareInputs(IReadOnlyList<CanonicalRecord> records)
    {
        DroidSessionSplit split = SplitDroidSessionRecords(records);
        bool paired = split.TurnRecords.Count > 0 && split.CounterRecords.Count > 0;
        List<CanonicalRecord> turnRecords = paired ? TurnsOf(split.TurnRecords) : TurnsOf(records);
        IReadOnlyList<CanonicalRecord> structureRecords = paired ? split.TurnRecords : records;
        List<CanonicalRecord> counterRecords = paired
            ? split.CounterRecords.Where(static r => !string.Equals(r.Op, "tool.invoke", StringComparison.Ordinal)).ToList()
            : [];

        string harness = CanonPricing.NormalizeHarnessName(records[0].Harness);

        List<CanonicalRecord> mainThreadRecords = turnRecords.Where(static r => !IsSideAgentTurn(r)).ToList();
        List<CanonicalRecord> contextRecords = mainThreadRecords.Count > 0 ? mainThreadRecords : turnRecords;
        List<CanonicalRecord> measuredTurns = contextRecords.Where(static r => CanonPricing.MeasuredInput(r.Tokens) > 0).ToList();
        int unmeasuredTurns = contextRecords.Count - measuredTurns.Count;

        long? counterInput = counterRecords.Count > 0
            ? counterRecords.Sum(static r => CanonPricing.MeasuredInput(r.Tokens))
            : null;

        return new SummaryInputs(
            split,
            paired,
            turnRecords,
            structureRecords,
            counterRecords,
            harness,
            contextRecords,
            measuredTurns,
            unmeasuredTurns,
            counterInput);
    }

    private static List<ContextTurn> BuildContextTurns(List<CanonicalRecord> measuredTurns)
    {
        List<ContextPart> lastSystem = [];
        List<ContextTurn> contextTurns = new(measuredTurns.Count);
        foreach (CanonicalRecord record in measuredTurns)
        {
            List<ContextPart> parts = PartsOf(record);
            List<ContextPart> ownSystem = parts.Where(static p => string.Equals(p.Part, "system_prompt", StringComparison.Ordinal)).ToList();
            if (ownSystem.Count > 0)
            {
                lastSystem = ownSystem;
            }

            IReadOnlyList<ContextPart> effective = ownSystem.Count > 0 ? parts : [.. CarriedTo(lastSystem, record), .. parts];
            contextTurns.Add(new ContextTurn(
                Parts: effective,
                InputTokens: CanonPricing.MeasuredInput(record.Tokens),
                FreshInput: MeasuredFreshInput(record.Tokens)));
        }

        return contextTurns;
    }

    private static (Dictionary<string, int> InvocationCounts, Dictionary<string, int> ErrorCounts) CountToolInvocations(
        List<CanonicalRecord> toolRecords)
    {
        Dictionary<string, int> invocationCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> errorCounts = new(StringComparer.Ordinal);
        foreach (CanonicalRecord toolRecord in toolRecords)
        {
            string toolName = (AttributeOf(toolRecord, ["gen_ai.tool.name", "tool.name", "tool_name"]) ?? toolRecord.Name).Trim();
            if (toolName.Length == 0 || string.Equals(toolName, "tool.invoke", StringComparison.Ordinal))
            {
                continue;
            }

            invocationCounts[toolName] = invocationCounts.GetValueOrDefault(toolName) + 1;
            if (IsToolError(toolRecord))
            {
                errorCounts[toolName] = errorCounts.GetValueOrDefault(toolName) + 1;
            }
        }

        return (invocationCounts, errorCounts);
    }

    private static List<Dictionary<string, object?>> BuildToolsPayload(
        List<RankedTool> rankedTools,
        Dictionary<string, ToolDefinition> definitionsByName,
        Dictionary<string, int> invocationCounts,
        Dictionary<string, int> errorCounts,
        int turnCount)
    {
        List<Dictionary<string, object?>> toolsPayload = [];
        HashSet<string> seenToolNames = new(StringComparer.Ordinal);
        foreach (RankedTool ranked in rankedTools)
        {
            seenToolNames.Add(ranked.Name);
            definitionsByName.TryGetValue(ranked.Name, out ToolDefinition? def);
            long schemaTokens = def?.Tokens ?? 0;
            int turnsResident = def?.TurnsResident ?? turnCount;
            int inv = invocationCounts.GetValueOrDefault(ranked.Name);
            int errs = errorCounts.GetValueOrDefault(ranked.Name);
            Dictionary<string, object?> entry = new(StringComparer.Ordinal)
            {
                ["name"] = ranked.Name,
                ["schema_tokens"] = schemaTokens,
                ["invocations"] = inv,
                ["turns_resident"] = turnsResident,
                ["errors"] = errs,
                ["total_schema_cost"] = ranked.Cost,
            };
            if (def?.Server is not null)
            {
                entry["server"] = def.Server;
            }

            toolsPayload.Add(entry);
        }

        foreach (KeyValuePair<string, int> kv in invocationCounts)
        {
            if (!seenToolNames.Add(kv.Key))
            {
                continue;
            }

            toolsPayload.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = kv.Key,
                ["schema_tokens"] = 0L,
                ["invocations"] = kv.Value,
                ["turns_resident"] = 0,
                ["errors"] = errorCounts.GetValueOrDefault(kv.Key),
            });
        }

        return toolsPayload;
    }

    private static List<Dictionary<string, object?>> BuildServersPayload(
        SchemaCostAnalysis schema,
        List<RankedTool> rankedTools,
        Dictionary<string, ToolDefinition> definitionsByName,
        Dictionary<string, int> invocationCounts)
    {
        List<Dictionary<string, object?>> serversPayload = [];
        if (!schema.Measurable)
        {
            return serversPayload;
        }

        foreach (string server in schema.ByServer.Keys)
        {
            List<RankedTool> serverTools = rankedTools.Where(t => string.Equals(t.Server, server, StringComparison.Ordinal)).ToList();
            serversPayload.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["server"] = server,
                ["is_mcp"] = true,
                ["tools"] = serverTools.Count,
                ["schema_tokens"] = serverTools.Sum(t => definitionsByName.GetValueOrDefault(t.Name)?.Tokens ?? 0),
                ["invocations"] = serverTools.Sum(t => invocationCounts.GetValueOrDefault(t.Name)),
                ["unused_tools"] = serverTools.Count(t => invocationCounts.GetValueOrDefault(t.Name) == 0),
                ["unused_cost"] = serverTools.Where(t => invocationCounts.GetValueOrDefault(t.Name) == 0).Sum(t => t.Cost),
            });
        }

        return serversPayload;
    }

    private static (
        long TotalInput,
        long TotalOutput,
        long TotalCacheRead,
        long TotalCacheCreation) SumTokenTotals(IReadOnlyList<CanonicalRecord> totalRecords)
    {
        long totalInput = 0;
        long totalOutput = 0;
        long totalCacheRead = 0;
        long totalCacheCreation = 0;
        foreach (CanonicalRecord r in totalRecords)
        {
            totalInput += CanonPricing.MeasuredInput(r.Tokens);
            totalOutput += r.Tokens.Output;
            totalCacheRead += r.Tokens.CacheRead;
            totalCacheCreation += r.Tokens.CacheCreation;
        }

        return (totalInput, totalOutput, totalCacheRead, totalCacheCreation);
    }

    private static (int? ToolCalls, List<string>? ToolsInvoked) ResolveToolUsage(
        List<CanonicalRecord> toolRecords,
        IReadOnlyList<CanonicalRecord> toolSourceRecords,
        IReadOnlyDictionary<string, JsonElement>? measurability,
        string harness)
    {
        int? toolCalls = null;
        List<string>? toolsInvoked = null;
        if (toolRecords.Count > 0)
        {
            bool lowerBound = toolRecords.Any(static r => IsNotMeasurableElement(r.Measurability, "tool_calls"));
            if (!lowerBound)
            {
                toolCalls = toolRecords.Count;
            }

            toolsInvoked = [.. InvocationsOf(toolSourceRecords).Distinct(StringComparer.Ordinal)];
        }
        else if (IsMeasuredOrDerived(measurability, "tool_calls") || CanonPricing.SurveyFamily(harness) == "claude-code")
        {
            toolCalls = 0;
            toolsInvoked = [];
        }

        return (toolCalls, toolsInvoked);
    }

    private static (List<string> OfferedTools, bool HasDefParts) CollectOfferedTools(
        IReadOnlyList<CanonicalRecord> records,
        TextTokenCounter countTokens)
    {
        List<string> offeredTools = [];
        HashSet<string> seenOfferedTools = new(StringComparer.Ordinal);
        bool hasDefParts = false;
        foreach (CanonicalRecord record in records)
        {
            foreach (ToolDefinition def in ToolDefinitionsOf([record], countTokens))
            {
                hasDefParts = true;
                if (seenOfferedTools.Add(def.Name))
                {
                    offeredTools.Add(def.Name);
                }
            }

            if (record.Parts is not null && record.Parts.Any(static p => string.Equals(p.Part, "tool_definitions", StringComparison.Ordinal) || string.Equals(p.Part, "tool_definition", StringComparison.Ordinal)))
            {
                hasDefParts = true;
            }
        }

        return (offeredTools, hasDefParts);
    }

    private static Dictionary<string, object?> BuildSummaryDict(
        int turnCount,
        int requestCount,
        long totalDurationMs,
        List<string> models,
        CostTotalResult costResult,
        IReadOnlyList<CanonicalRecord> totalRecords,
        IReadOnlyDictionary<string, JsonElement>? totalMeasurability,
        long totalInput,
        long totalOutput,
        long totalCacheRead,
        long totalCacheCreation,
        int? toolCalls,
        List<string>? toolsInvoked,
        bool hasDefParts,
        List<string> offeredTools,
        IReadOnlyDictionary<string, JsonElement>? measurability,
        bool includedInParent)
    {
        object? summaryTotalInput = UnavailableOrValue(totalMeasurability, ["token_usage"], totalInput);
        object? summaryTotalOutput = UnavailableOrValue(totalMeasurability, ["output", "output_tokens"], totalOutput);
        object? summaryTotalCacheRead = UnavailableOrValue(totalMeasurability, ["cache_read"], totalCacheRead);
        object? summaryTotalCacheCreation = UnavailableOrValue(totalMeasurability, ["cache_creation"], totalCacheCreation);

        Dictionary<string, object?> summaryDict = new(StringComparer.Ordinal)
        {
            ["turn_count"] = turnCount,
            ["request_count"] = requestCount,
            ["total_input"] = summaryTotalInput,
            ["total_output"] = summaryTotalOutput,
            ["total_cache_read"] = summaryTotalCacheRead,
            ["total_cache_creation"] = summaryTotalCacheCreation,
            ["duration_ms"] = totalDurationMs,
            ["models"] = models,
            ["cost"] = costResult.Ok && costResult.Total is not null
                ? costResult.Total
                : new CostBlock(CostBasis.Unknown, CostStatus.NoRate),
        };

        if (summaryTotalInput is long numIn && numIn > 0 && summaryTotalCacheRead is long numCr)
        {
            summaryDict["cache_hit_ratio"] = (double)numCr / numIn;
        }

        if (summaryTotalInput is long && totalRecords.Count > 0 && summaryTotalInput is long numIn2 && numIn2 > 0 && summaryTotalCacheCreation is long && turnCount > 0)
        {
            summaryDict["cache_creation_coverage"] = totalRecords.Count(static r => r.Tokens.CacheCreation > 0);
        }

        if (toolCalls.HasValue)
        {
            summaryDict["tool_calls"] = toolCalls.Value;
        }

        if (toolsInvoked is not null)
        {
            summaryDict["tools_invoked"] = toolsInvoked;
        }

        if (hasDefParts)
        {
            summaryDict["tools_offered"] = offeredTools;
        }
        else if (TryUnavailable(measurability, "tool_definitions", out JsonElement toolDefUnavail))
        {
            summaryDict["tools_offered"] = toolDefUnavail;
        }

        if (includedInParent)
        {
            summaryDict["included_in_parent"] = true;
        }

        return summaryDict;
    }

    private static List<Dictionary<string, object?>> BuildTurnEntries(List<CanonicalRecord> turnRecords)
    {
        List<Dictionary<string, object?>> turnEntries = [];
        for (int i = 0; i < turnRecords.Count; i++)
        {
            CanonicalRecord tr = turnRecords[i];
            turnEntries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = i,
                ["spanId"] = tr.SpanId,
                ["timestamp"] = tr.Timestamp,
                ["model"] = AttributeOf(tr, ModelKeys),
                ["input"] = CanonPricing.MeasuredInput(tr.Tokens),
                ["output"] = tr.Tokens.Output,
                ["fresh"] = tr.Tokens.FreshInput,
                ["cache_read"] = tr.Tokens.CacheRead,
                ["cache_creation"] = tr.Tokens.CacheCreation,
                ["reasoning"] = tr.Tokens.Reasoning,
            });
        }

        return turnEntries;
    }

    private static List<Dictionary<string, object?>> BuildRequestEntries(
        List<CanonicalRecord> requestRoots,
        Dictionary<string, List<CanonicalRecord>> turnsByRoot)
    {
        List<Dictionary<string, object?>> requestEntries = [];
        foreach (CanonicalRecord req in requestRoots)
        {
            turnsByRoot.TryGetValue(req.SpanId, out List<CanonicalRecord>? ownedTurns);
            string? reqModel = AttributeOf(req, ModelKeys)
                ?? ownedTurns?.Select(static t => AttributeOf(t, ModelKeys)).FirstOrDefault(static m => m is not null);
            object? promptValue = PromptPreviewOf(req);
            if (promptValue is null && TryUnavailable(req.Measurability, "user_prompt", out JsonElement promptUnavail))
            {
                promptValue = promptUnavail;
            }

            requestEntries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = req.SpanId,
                ["timestamp"] = req.Timestamp,
                ["turns"] = ownedTurns?.Count ?? 0,
                ["model"] = reqModel,
                ["prompt"] = promptValue,
            });
        }

        return requestEntries;
    }

    private static List<Dictionary<string, object?>> BuildReconciliation(List<CanonicalRecord> turnRecords) =>
        turnRecords.Select(static r =>
        {
            long measuredIn = CanonPricing.MeasuredInput(r.Tokens);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = r.SpanId,
                ["root_input"] = r.Tokens.ReportedInput,
                ["sum_chat_input"] = measuredIn,
                ["input_match"] = r.Tokens.ReportedInput == measuredIn,
                ["root_output"] = r.Tokens.ReportedOutput,
                ["sum_chat_output"] = r.Tokens.Output,
                ["output_match"] = r.Tokens.ReportedOutput == r.Tokens.Output,
            };
        }).ToList();

    private static (object TimelinePayload, object SchemaPayload) BuildAvailabilityPayloads(
        TimelineNode timeline,
        SchemaCostAnalysis schema,
        IReadOnlyDictionary<string, JsonElement>? measurability)
    {
        object timelinePayload = TryUnavailable(measurability, "execution_structure", out JsonElement timelineUnavail)
            ? timelineUnavail
            : timeline.Children.Select(WithoutAttributes).ToList();

        object schemaPayload = schema.Measurable
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["measurable"] = true,
                ["byServer"] = schema.ByServer,
                ["neverInvoked"] = schema.NeverInvoked,
                ["unusedRange"] = schema.UnusedRange,
                ["turns"] = schema.Turns,
            }
            : TryUnavailable(measurability, "schema_cost", out JsonElement schemaUnavail)
                ? schemaUnavail
                : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["availability"] = "not_measurable",
                    ["reason"] = "The source did not provide tool-definition schema data.",
                };

        return (timelinePayload, schemaPayload);
    }
}

internal static class TwinDeduplicator
{
    internal static List<CanonicalRecord> MergeDedupedOutput(
        IReadOnlyList<CanonicalRecord> records,
        HashSet<CanonicalRecord> dropped,
        Dictionary<CanonicalRecord, CanonicalRecord> transplant,
        Dictionary<CanonicalRecord, TokenUsage> tokenOverride)
    {
        List<CanonicalRecord> output = new(records.Count);
        foreach (CanonicalRecord record in records)
        {
            if (dropped.Contains(record))
            {
                continue;
            }

            TokenUsage tokens = tokenOverride.GetValueOrDefault(record, record.Tokens);
            if (!transplant.TryGetValue(record, out CanonicalRecord? donor))
            {
                output.Add(ReferenceEquals(tokens, record.Tokens) ? record : record with { Tokens = tokens });
                continue;
            }

            if (!IsFileSource(record.Source))
            {
                CanonicalRecord mergedOtel = WithDonorSkills(MergeOtelFirst(record, donor), donor);
                output.Add(ReferenceEquals(tokens, record.Tokens) ? mergedOtel : mergedOtel with { Tokens = tokens });
                continue;
            }

            IReadOnlyList<ContentPart> keeperParts = record.Parts ?? [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (ContentPart part in keeperParts)
            {
                seen.Add($"{part.Part}\u0000{part.Text}");
            }

            List<ContentPart> extraParts = [];
            foreach (ContentPart part in donor.Parts ?? [])
            {
                if (seen.Add($"{part.Part}\u0000{part.Text}"))
                {
                    extraParts.Add(part);
                }
            }

            List<ContentPart> newParts = [.. keeperParts, .. extraParts];
            Dictionary<string, string> mergedContent = MergeDictionaries(donor.Content, record.Content);
            if (newParts.Count > 0)
            {
                mergedContent = MergeDictionaries(mergedContent, CanonValidation.ContentFromParts(newParts));
            }

            bool keepParts = (donor.Parts is { Count: > 0 }) || extraParts.Count > 0 || (record.Parts is { Count: > 0 });
            output.Add(WithDonorSkills(record, donor) with
            {
                Tokens = tokens,
                Parts = keepParts ? newParts : record.Parts,
                Content = mergedContent,
            });
        }

        return output;
    }

    internal static HashSet<CanonicalRecord> CollapseIdJoins(
        IReadOnlyList<CanonicalRecord> records,
        string? shareKey,
        HashSet<CanonicalRecord> dropped,
        Dictionary<CanonicalRecord, CanonicalRecord> transplant)
    {
        HashSet<CanonicalRecord> joined = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, CanonicalRecord> keeperByMessageId = new(StringComparer.Ordinal);
        Dictionary<string, CanonicalRecord> keeperByRequestId = new(StringComparer.Ordinal);

        void RememberEarliest(Dictionary<string, CanonicalRecord> map, string key, CanonicalRecord record)
        {
            if (!map.TryGetValue(key, out CanonicalRecord? known)
                || RecordTimestampMs(record) < RecordTimestampMs(known))
            {
                map[key] = record;
            }
        }

        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal) || IsFileSource(record.Source))
            {
                continue;
            }

            string session = SessionKeyOf(record, shareKey);
            string? messageId = OtelMessageIdOf(record);
            if (messageId is not null)
            {
                RememberEarliest(keeperByMessageId, $"{session}\u0000{messageId}", record);
            }

            string? requestId = OtelRequestJoinId(record);
            if (requestId is not null)
            {
                RememberEarliest(keeperByRequestId, $"{session}\u0000{requestId}", record);
            }
        }

        if (keeperByMessageId.Count == 0 && keeperByRequestId.Count == 0)
        {
            return joined;
        }

        void Adopt(CanonicalRecord keeper, CanonicalRecord file)
        {
            dropped.Add(file);
            joined.Add(file);
            joined.Add(keeper);
            if (!IsDonor(file))
            {
                return;
            }

            transplant[keeper] = transplant.TryGetValue(keeper, out CanonicalRecord? existing)
                ? MergeDonors(existing, file)
                : file;
        }

        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal) || !IsFileSource(record.Source))
            {
                continue;
            }

            string session = SessionKeyOf(record, shareKey);
            string? messageId = FileNativeIdOf(record);
            if (messageId is not null && keeperByMessageId.TryGetValue($"{session}\u0000{messageId}", out CanonicalRecord? byMessage))
            {
                Adopt(byMessage, record);
                continue;
            }

            string? requestId = FileRequestIdOf(record);
            if (requestId is not null && keeperByRequestId.TryGetValue($"{session}\u0000{requestId}", out CanonicalRecord? byRequest))
            {
                Adopt(byRequest, record);
            }
        }

        return joined;
    }

    /// <summary>
    /// Pairs Codex rollout-file model calls with the OTLP model calls that observed the same turn,
    /// on data both sources carry (D21). Codex OTel carries no per-model-call id — its
    /// <c>auth.request_id</c> names an auth request — so the pairing data is the reported counters:
    /// same session, equal <see cref="CounterKey"/>, timestamps within
    /// <see cref="TwinTurnMaxSkewMs"/>, and strictly one OTLP call per file call. Within each
    /// counter group, each file call in timestamp order pairs with the earliest unpaired OTLP call
    /// within the skew. A paired file call is dropped and, when it has parts, donates them to its
    /// OTLP call (the OTel row is the turn; the file row supplies only what OTLP lacks). An unpaired
    /// call of either source stays a turn; nothing pairs by timestamp or turn order alone.
    /// </summary>
    /// <param name="records">Candidate canonical records.</param>
    /// <param name="shareKey">Fallback session key used when a record has no <see cref="CanonicalRecord.SessionId"/>.</param>
    /// <param name="dropped">Receives each paired file model call.</param>
    /// <param name="transplant">Receives each paired file model call with parts, keyed by its OTLP call.</param>
    /// <returns>Every Codex model call with reported counters, paired or not, so the shared counter-cluster path skips it.</returns>
    internal static HashSet<CanonicalRecord> PairCodexFileModelCalls(
        IReadOnlyList<CanonicalRecord> records,
        string? shareKey,
        HashSet<CanonicalRecord> dropped,
        Dictionary<CanonicalRecord, CanonicalRecord> transplant)
    {
        HashSet<CanonicalRecord> handled = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, (List<CanonicalRecord> Otels, List<CanonicalRecord> Files)> groups = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            if (!IsCodexModelCall(record) || IsUnreportedCounters(record.Tokens))
            {
                continue;
            }

            handled.Add(record);
            string key = CounterKey(record, shareKey);
            if (!groups.TryGetValue(key, out (List<CanonicalRecord> Otels, List<CanonicalRecord> Files) group))
            {
                group = ([], []);
                groups[key] = group;
            }

            (IsFileSource(record.Source) ? group.Files : group.Otels).Add(record);
        }

        foreach ((List<CanonicalRecord> otels, List<CanonicalRecord> files) in groups.Values)
        {
            if (otels.Count == 0 || files.Count == 0)
            {
                continue;
            }

            otels.Sort(CompareRecordTimestamps);
            files.Sort(CompareRecordTimestamps);
            HashSet<CanonicalRecord> pairedOtels = new(ReferenceEqualityComparer.Instance);
            foreach (CanonicalRecord file in files)
            {
                double moment = RecordTimestampMs(file);
                CanonicalRecord? otel = otels.Find(o =>
                    !pairedOtels.Contains(o) && Math.Abs(RecordTimestampMs(o) - moment) <= TwinTurnMaxSkewMs);
                if (otel is null)
                {
                    continue;
                }

                pairedOtels.Add(otel);
                dropped.Add(file);
                if (IsDonor(file))
                {
                    transplant[otel] = transplant.TryGetValue(otel, out CanonicalRecord? existing)
                        ? MergeDonors(existing, file)
                        : file;
                }
            }
        }

        return handled;
    }

    internal static bool IsCodexModelCall(CanonicalRecord record) =>
        CodexHarnessIds.Contains(record.Harness)
        && string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal);

    internal static void CollapseCluster(
        IReadOnlyList<CanonicalRecord> cluster,
        HashSet<CanonicalRecord> dropped,
        Dictionary<CanonicalRecord, CanonicalRecord> transplant)
    {
        if (cluster.Count == 0)
        {
            return;
        }

        List<CanonicalRecord> otels = cluster.Where(static r => !IsFileSource(r.Source)).ToList();
        List<CanonicalRecord> files = cluster.Where(static r => IsFileSource(r.Source)).ToList();
        if (otels.Count == 0)
        {
            if (files.Count <= 1)
            {
                return;
            }

            if (!files.All(static f => string.Equals(f.Source, CanonSynth.FileSourcePrefix + "claude-desktop", StringComparison.Ordinal)
                    || string.Equals(f.Harness, "claude-desktop", StringComparison.Ordinal)))
            {
                return;
            }

            files.Sort(CompareRecordTimestamps);
            CanonicalRecord keeper = files[0];
            CanonicalRecord? donor = null;
            for (int i = 1; i < files.Count; i++)
            {
                CanonicalRecord file = files[i];
                dropped.Add(file);
                if (IsDonor(file))
                {
                    donor = donor is null ? file : MergeDonors(donor, file);
                }
            }

            if (donor is not null)
            {
                transplant[keeper] = donor;
            }

            return;
        }

        if (files.Count == 0)
        {
            return;
        }

        foreach (CanonicalRecord file in files)
        {
            dropped.Add(file);
        }

        List<CanonicalRecord> donors = files.Where(IsDonor).OrderBy(RecordTimestampMs).ToList();
        HashSet<CanonicalRecord> free = new(donors, ReferenceEqualityComparer.Instance);

        List<CanonicalRecord> keepers =
        [
            .. otels.Where(static o => !HasParts(o)).OrderBy(RecordTimestampMs),
            .. otels.Where(HasParts).OrderBy(RecordTimestampMs),
        ];

        foreach (CanonicalRecord keeper in keepers)
        {
            double moment = RecordTimestampMs(keeper);
            CanonicalRecord? best = null;
            double bestDist = double.PositiveInfinity;
            foreach (CanonicalRecord donor in donors)
            {
                if (!free.Contains(donor))
                {
                    continue;
                }

                double dist = Math.Abs(RecordTimestampMs(donor) - moment);
                if (best is null || dist < bestDist)
                {
                    best = donor;
                    bestDist = dist;
                }
            }

            if (best is null)
            {
                break;
            }

            free.Remove(best);
            transplant[keeper] = best;
        }

        foreach (CanonicalRecord donor in donors.Where(free.Contains))
        {
            double donorMoment = RecordTimestampMs(donor);
            CanonicalRecord nearest = otels[0];
            double bestDist = Math.Abs(RecordTimestampMs(nearest) - donorMoment);
            foreach (CanonicalRecord candidate in otels.Skip(1))
            {
                double dist = Math.Abs(RecordTimestampMs(candidate) - donorMoment);
                if (dist < bestDist)
                {
                    nearest = candidate;
                    bestDist = dist;
                }
            }

            transplant[nearest] = transplant.TryGetValue(nearest, out CanonicalRecord? existing)
                ? MergeDonors(existing, donor)
                : donor;
        }
    }

    internal static void CollapseOverlapFileJoins(
        IReadOnlyList<CanonicalRecord> records,
        string? shareKey,
        HashSet<CanonicalRecord> dropped,
        Dictionary<CanonicalRecord, CanonicalRecord> transplant,
        Dictionary<CanonicalRecord, TokenUsage> tokenOverride)
    {
        Dictionary<string, List<CanonicalRecord>> bySession = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            if (dropped.Contains(record))
            {
                continue;
            }

            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsFileSource(record.Source) || IsUnreportedCounters(record.Tokens))
            {
                continue;
            }

            string key = SessionKeyOf(record, shareKey);
            if (!bySession.TryGetValue(key, out List<CanonicalRecord>? list))
            {
                list = [];
                bySession[key] = list;
            }

            list.Add(record);
        }

        foreach (List<CanonicalRecord> turns in bySession.Values)
        {
            if (!HasEvidencedCursorTwinShare(turns))
            {
                continue;
            }

            turns.Sort(CompareRecordTimestamps);
            List<CanonicalRecord> cluster = [];
            foreach (CanonicalRecord record in turns)
            {
                if (cluster.Count > 0 && RecordTimestampMs(record) - RecordTimestampMs(cluster[0]) > TwinTurnMaxSkewMs)
                {
                    CollapseOverlapCluster(cluster, dropped, transplant, tokenOverride);
                    cluster.Clear();
                }

                cluster.Add(record);
            }

            CollapseOverlapCluster(cluster, dropped, transplant, tokenOverride);
        }
    }

    internal static void CollapseOverlapCluster(
        IReadOnlyList<CanonicalRecord> cluster,
        HashSet<CanonicalRecord> dropped,
        Dictionary<CanonicalRecord, CanonicalRecord> transplant,
        Dictionary<CanonicalRecord, TokenUsage> tokenOverride)
    {
        if (cluster.Count < 2)
        {
            return;
        }

        List<CanonicalRecord> completes = cluster.Where(IsCompleteCursorAgentObservation).ToList();
        if (completes.Count == 0)
        {
            return;
        }

        Dictionary<CanonicalRecord, List<CanonicalRecord>> groups = new(ReferenceEqualityComparer.Instance);
        foreach (CanonicalRecord complete in completes)
        {
            groups[complete] = [complete];
        }

        List<CanonicalRecord> partials = cluster
            .Where(static r => !IsCompleteCursorAgentObservation(r) && !IsCompleteCursorIdeObservation(r))
            .OrderBy(RecordTimestampMs)
            .ToList();

        foreach (CanonicalRecord partial in partials)
        {
            CanonicalRecord? best = FindBestJoin(partial, completes);
            if (best is not null)
            {
                groups[best].Add(partial);
            }
        }

        HashSet<CanonicalRecord> availableIdes = new(cluster.Where(IsCompleteCursorIdeObservation), ReferenceEqualityComparer.Instance);
        foreach (CanonicalRecord complete in completes.OrderBy(RecordTimestampMs))
        {
            CanonicalRecord? best = FindBestJoin(complete, availableIdes);
            if (best is not null)
            {
                availableIdes.Remove(best);
                groups[complete].Add(best);
            }
        }

        foreach (List<CanonicalRecord> members in groups.Values)
        {
            if (members.Count < 2)
            {
                continue;
            }

            CanonicalRecord keeper = members[0];
            for (int i = 1; i < members.Count; i++)
            {
                keeper = PreferFullerKeeper(keeper, members[i]);
            }

            TokenUsage mergedTokens = keeper.Tokens;
            foreach (CanonicalRecord member in members)
            {
                if (ReferenceEquals(member, keeper))
                {
                    continue;
                }

                mergedTokens = MaxTokens(mergedTokens, member.Tokens);
                dropped.Add(member);
                if (!IsDonor(member))
                {
                    continue;
                }

                transplant[keeper] = transplant.TryGetValue(keeper, out CanonicalRecord? existing)
                    ? MergeDonors(existing, member)
                    : member;
            }

            if (mergedTokens != keeper.Tokens)
            {
                tokenOverride[keeper] = mergedTokens;
            }
        }
    }

    internal static CanonicalRecord? FindBestJoin(CanonicalRecord source, IEnumerable<CanonicalRecord> candidates)
    {
        CanonicalRecord? best = null;
        double bestDist = double.PositiveInfinity;
        double sourceTime = RecordTimestampMs(source);
        foreach (CanonicalRecord candidate in candidates)
        {
            if (!CanJoinDistinctCounters(source.Tokens, candidate.Tokens))
            {
                continue;
            }

            double candidateTime = RecordTimestampMs(candidate);
            double dist = Math.Abs(sourceTime - candidateTime);
            if (best is null || dist < bestDist || (dist == bestDist && candidateTime < RecordTimestampMs(best)))
            {
                best = candidate;
                bestDist = dist;
            }
        }

        return best;
    }

    internal static CanonicalRecord MergeOtelFirst(CanonicalRecord record, CanonicalRecord donor)
    {
        IReadOnlyList<ContentPart> keeperParts = record.Parts ?? [];
        HashSet<string> keeperBuckets = new(keeperParts.Select(static p => p.Part), StringComparer.Ordinal);
        HashSet<string> held = new([.. keeperBuckets, .. record.Content.Keys], StringComparer.Ordinal);

        List<ContentPart> fillParts = (donor.Parts ?? []).Where(p => !held.Contains(p.Part)).ToList();
        Dictionary<string, string> fillContent = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in donor.Content)
        {
            if (!held.Contains(kv.Key))
            {
                fillContent[kv.Key] = kv.Value;
            }
        }

        if (keeperParts.Count == 0 && fillParts.Count == 0)
        {
            return record with { Content = MergeDictionaries(fillContent, record.Content) };
        }

        HashSet<string> fillBuckets = new(fillParts.Select(static p => p.Part), StringComparer.Ordinal);
        List<ContentPart> otelFlatParts = record.Content
            .Where(kv => !keeperBuckets.Contains(kv.Key))
            .Select(static kv => new ContentPart(kv.Key, kv.Value))
            .ToList();
        List<ContentPart> fileFlatParts = fillContent
            .Where(kv => !fillBuckets.Contains(kv.Key))
            .Select(static kv => new ContentPart(kv.Key, kv.Value))
            .ToList();

        List<ContentPart> parts = [.. keeperParts, .. otelFlatParts, .. fillParts, .. fileFlatParts];
        Dictionary<string, string> mergedContent = MergeDictionaries(fillContent, record.Content);
        mergedContent = MergeDictionaries(mergedContent, CanonValidation.ContentFromParts(parts));
        return record with { Parts = parts, Content = mergedContent };
    }

    internal static CanonicalRecord MergeDonors(CanonicalRecord first, CanonicalRecord second)
    {
        List<ContentPart> parts = [.. first.Parts ?? [], .. second.Parts ?? []];
        Dictionary<string, string> merged = MergeDictionaries(first.Content, second.Content);
        if (parts.Count > 0)
        {
            merged = MergeDictionaries(merged, CanonValidation.ContentFromParts(parts));
        }

        return WithDonorSkills(first, second) with { Parts = parts, Content = merged };
    }

    /// <summary>Raw attributes a twin keeps from the observation it absorbed when it has none of its own.</summary>
    internal static readonly string[] DonorSkillKeys = [CanonSynth.SkillsListedKey, CanonSynth.SkillsInvokedKey];

    /// <summary>Raw attributes a kept twin must inherit from an absorbed twin: skills and the compaction boundary.</summary>
    internal static readonly string[] DonorKeys = [.. DonorSkillKeys, CanonRecordHydration.CompactBoundaryRawKey];

    /// <summary>
    /// Carries the skill listing and skill invocations of an absorbed twin onto the kept record: a
    /// Claude Code listing is stored only on the transcript turn, so an OTLP keeper would otherwise drop
    /// it and the inactive-skill-reference detector would not see it. The keeper's own listing wins;
    /// invoked names are unioned, keeper's first. The compaction boundary marker is carried too, so the
    /// projection still resets its cumulative context when the post-compaction turn joins an OTLP twin.
    /// </summary>
    internal static CanonicalRecord WithDonorSkills(CanonicalRecord keeper, CanonicalRecord donor)
    {
        if (donor.Raw is null)
        {
            return keeper;
        }

        Dictionary<string, JsonElement>? raw = null;
        Dictionary<string, JsonElement> Writable() =>
            raw ??= keeper.Raw is null ? new(StringComparer.Ordinal) : new(keeper.Raw, StringComparer.Ordinal);

        if (donor.Raw.TryGetValue(CanonRecordHydration.CompactBoundaryRawKey, out JsonElement boundary)
            && (keeper.Raw is null || !keeper.Raw.ContainsKey(CanonRecordHydration.CompactBoundaryRawKey)))
        {
            Writable()[CanonRecordHydration.CompactBoundaryRawKey] = boundary;
        }

        if (donor.Raw.TryGetValue(CanonSynth.SkillsListedKey, out JsonElement listed)
            && (keeper.Raw is null || !keeper.Raw.ContainsKey(CanonSynth.SkillsListedKey)))
        {
            Writable()[CanonSynth.SkillsListedKey] = listed;
        }

        if (donor.Raw.TryGetValue(CanonSynth.SkillsInvokedKey, out JsonElement donorInvoked))
        {
            if (keeper.Raw is null || !keeper.Raw.TryGetValue(CanonSynth.SkillsInvokedKey, out JsonElement keeperInvoked))
            {
                Writable()[CanonSynth.SkillsInvokedKey] = donorInvoked;
            }
            else if (keeperInvoked.ValueKind == JsonValueKind.Array && donorInvoked.ValueKind == JsonValueKind.Array)
            {
                List<string> names = [];
                foreach (JsonElement name in keeperInvoked.EnumerateArray().Concat(donorInvoked.EnumerateArray()))
                {
                    if (name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text && !names.Contains(text, StringComparer.Ordinal))
                    {
                        names.Add(text);
                    }
                }

                if (names.Count != keeperInvoked.GetArrayLength())
                {
                    Writable()[CanonSynth.SkillsInvokedKey] = JsonSerializer.SerializeToElement(names);
                }
            }
        }

        return raw is null ? keeper : keeper with { Raw = raw };
    }

    internal static bool IsFileSource(string source) =>
        source.StartsWith(CanonSynth.FileSourcePrefix, StringComparison.Ordinal);

    internal static string SessionKeyOf(CanonicalRecord record, string? shareKey) =>
        record.SessionId ?? shareKey ?? string.Empty;

    internal static string CounterKey(CanonicalRecord record, string? shareKey)
    {
        TokenUsage t = record.Tokens;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{SessionKeyOf(record, shareKey)}:{t.FreshInput}:{t.CacheRead}:{t.CacheCreation}:{t.Output}:{t.ReportedInput}:{t.ReportedOutput}");
    }

    internal static bool IsUnreportedCounters(TokenUsage tokens) =>
        tokens.FreshInput == 0
        && tokens.CacheRead == 0
        && tokens.CacheCreation == 0
        && tokens.Output == 0
        && tokens.ReportedInput == 0
        && tokens.ReportedOutput == 0;

    internal static long[] TokenDims(TokenUsage tokens) =>
    [
        tokens.FreshInput,
        tokens.CacheRead,
        tokens.CacheCreation,
        tokens.Output,
        tokens.ReportedInput,
        tokens.ReportedOutput,
    ];

    internal static bool CanJoinDistinctCounters(TokenUsage a, TokenUsage b)
    {
        long[] da = TokenDims(a);
        long[] db = TokenDims(b);
        bool equal = true;
        bool aSubB = true;
        bool bSubA = true;
        bool comp = true;

        for (int i = 0; i < da.Length; i++)
        {
            if (da[i] != db[i])
            {
                equal = false;
            }

            if (da[i] > db[i])
            {
                aSubB = false;
            }

            if (db[i] > da[i])
            {
                bSubA = false;
            }

            if (da[i] != 0 && db[i] != 0 && da[i] != db[i])
            {
                comp = false;
            }
        }

        return !equal && (comp || aSubB || bSubA);
    }

    internal static TokenUsage MaxTokens(TokenUsage a, TokenUsage b)
    {
        long? reasoning = a.Reasoning.HasValue || b.Reasoning.HasValue
            ? Math.Max(a.Reasoning ?? 0, b.Reasoning ?? 0)
            : null;
        return new TokenUsage(
            FreshInput: Math.Max(a.FreshInput, b.FreshInput),
            CacheRead: Math.Max(a.CacheRead, b.CacheRead),
            CacheCreation: Math.Max(a.CacheCreation, b.CacheCreation),
            Output: Math.Max(a.Output, b.Output),
            ReportedInput: Math.Max(a.ReportedInput, b.ReportedInput),
            ReportedOutput: Math.Max(a.ReportedOutput, b.ReportedOutput),
            Reasoning: reasoning);
    }

    internal static CanonicalRecord PreferFullerKeeper(CanonicalRecord a, CanonicalRecord b)
    {
        (int nonZeroA, long magA) = CounterFullness(a.Tokens);
        (int nonZeroB, long magB) = CounterFullness(b.Tokens);
        if (nonZeroA != nonZeroB)
        {
            return nonZeroA > nonZeroB ? a : b;
        }

        if (magA != magB)
        {
            return magA > magB ? a : b;
        }

        return RecordTimestampMs(a) <= RecordTimestampMs(b) ? a : b;
    }

    internal static (int NonZero, long Magnitude) CounterFullness(TokenUsage tokens)
    {
        int nonZero = 0;
        long mag = 0;
        foreach (long v in TokenDims(tokens))
        {
            if (v > 0)
            {
                nonZero++;
            }

            mag += v;
        }

        return (nonZero, mag);
    }

    internal static bool IsCursorCollector(CanonicalRecord record) =>
        !string.Equals(record.Harness, "cursor-agent", StringComparison.Ordinal)
        && (string.Equals(record.Harness, "cursor", StringComparison.Ordinal)
            || string.Equals(record.Source, CanonSynth.FileSourcePrefix + "cursor", StringComparison.Ordinal));

    internal static bool IsCursorAgentCollector(CanonicalRecord record) =>
        string.Equals(record.Harness, "cursor-agent", StringComparison.Ordinal)
        || record.Source.Contains("cursor-agent", StringComparison.Ordinal);

    internal static bool HasEvidencedCursorTwinShare(IReadOnlyList<CanonicalRecord> records)
    {
        bool cursor = false;
        bool agent = false;
        foreach (CanonicalRecord record in records)
        {
            if (IsCursorAgentCollector(record))
            {
                agent = true;
            }
            else if (IsCursorCollector(record))
            {
                cursor = true;
            }

            if (cursor && agent)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasCompleteCounters(CanonicalRecord record) =>
        (record.Tokens.FreshInput > 0 || record.Tokens.ReportedInput > 0)
        && (record.Tokens.Output > 0 || record.Tokens.ReportedOutput > 0);

    internal static bool IsCompleteCursorAgentObservation(CanonicalRecord record) =>
        IsCursorAgentCollector(record) && HasCompleteCounters(record);

    internal static bool IsCompleteCursorIdeObservation(CanonicalRecord record) =>
        IsCursorCollector(record) && HasCompleteCounters(record);

    internal static string? OtelMessageIdOf(CanonicalRecord record) => RawString(record, "message.id");

    internal static string? OtelRequestJoinId(CanonicalRecord record) => RawString(record, "request_id");

    internal static string? FileRequestIdOf(CanonicalRecord record) => RawString(record, "requestId");

    internal static string? FileNativeIdOf(CanonicalRecord record)
    {
        if (record.Raw is null
            || !record.Raw.TryGetValue("provenance", out JsonElement provEl)
            || provEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (provEl.TryGetProperty("recordDigest", out JsonElement digestEl) && digestEl.ValueKind != JsonValueKind.Undefined && digestEl.ValueKind != JsonValueKind.Null)
        {
            return null;
        }

        if (provEl.TryGetProperty("nativeRecordId", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            string? val = idEl.GetString();
            return string.IsNullOrEmpty(val) ? null : val;
        }

        return null;
    }

    internal static string? RawString(CanonicalRecord record, string key)
    {
        if (record.Raw is null || !record.Raw.TryGetValue(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? val = el.GetString();
        return string.IsNullOrEmpty(val) ? null : val;
    }

    /// <summary>
    /// Whether an absorbed twin has anything to hand its keeper: content, or a skill listing or skill
    /// invocations or compaction boundary the keeper would otherwise lose (<see cref="WithDonorSkills"/>).
    /// </summary>
    internal static bool IsDonor(CanonicalRecord record) =>
        HasParts(record) || (record.Raw is not null && DonorKeys.Any(record.Raw.ContainsKey));

    internal static bool HasParts(CanonicalRecord record) =>
        (record.Parts is { Count: > 0 }) || record.Content.Count > 0;

    internal static Dictionary<string, string> MergeDictionaries(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        Dictionary<string, string> result = new(first.Count + second.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in first)
        {
            result[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, string> kv in second)
        {
            result[kv.Key] = kv.Value;
        }

        return result;
    }

    internal static double RecordTimestampMs(CanonicalRecord record) =>
        DateTimeOffset.TryParse(record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dto)
            ? dto.ToUnixTimeMilliseconds()
            : double.NaN;

    internal static int CompareRecordTimestamps(CanonicalRecord a, CanonicalRecord b)
    {
        double ta = RecordTimestampMs(a);
        double tb = RecordTimestampMs(b);
        bool aOk = double.IsFinite(ta);
        bool bOk = double.IsFinite(tb);
        if (aOk && bOk)
        {
            return ta.CompareTo(tb);
        }

        if (aOk)
        {
            return -1;
        }

        return bOk ? 1 : 0;
    }
}

internal static class SessionProjectionHelpers
{
    internal static Dictionary<string, object?> SerializeContextPayload(
        ContextAnalysis context,
        string? contextLimitSource,
        IReadOnlyList<CanonicalRecord> measuredTurns,
        long? counterInput,
        int unmeasuredTurns,
        IReadOnlyDictionary<string, JsonElement>? totalMeasurability)
    {
        Dictionary<string, string> bucketMap = new(StringComparer.Ordinal)
        {
            ["system_prompt"] = "system_prompt",
            ["conversation_history"] = "conversation_history",
            ["tool_definitions"] = "tool_definitions",
            ["tool_results"] = "tool_result_content",
            ["response"] = "response",
        };

        bool hideLimit = string.Equals(contextLimitSource, "absent", StringComparison.Ordinal);
        List<Dictionary<string, object?>> serializedTurns = [];
        if (context.Measurable)
        {
            foreach (TurnPressure turn in context.Turns)
            {
                Dictionary<string, object?> entry = new(StringComparer.Ordinal)
                {
                    ["index"] = turn.Index,
                    ["inputTokens"] = turn.InputTokens,
                    ["reported_input"] = turn.InputTokens,
                    ["buckets"] = turn.Buckets,
                    ["toolDefinitionsByServer"] = turn.ToolDefinitionsByServer,
                    ["builtinToolDefinitionTokens"] = turn.BuiltinToolDefinitionTokens,
                    ["strippedInstructionBlocks"] = turn.StrippedInstructionBlocks,
                    ["bucketedTokens"] = turn.BucketedTokens,
                    ["residual"] = turn.Residual,
                    ["accumulationRate"] = turn.AccumulationRate,
                    ["freshInput"] = turn.FreshInput,
                };

                if (!hideLimit)
                {
                    entry["headroom"] = turn.Headroom;
                    entry["pressure"] = turn.Pressure;
                }

                if (turn.FreshInputJump is not null)
                {
                    entry["freshInputJump"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["previous"] = turn.FreshInputJump.Previous,
                        ["factor"] = double.IsFinite(turn.FreshInputJump.Factor) ? turn.FreshInputJump.Factor : null,
                    };
                }

                if (turn.NonResidentToolDefinitions.HasValue)
                {
                    entry["nonResidentToolDefinitions"] = turn.NonResidentToolDefinitions.Value;
                }

                serializedTurns.Add(entry);
            }
        }

        Dictionary<string, object?> firstBucket = BuildContextBucket(
            context.Measurable && context.Turns.Count > 0 ? context.Turns[0] : null,
            measuredTurns.Count > 0 ? measuredTurns[0].Tokens.ReportedInput : (counterInput ?? 0),
            bucketMap,
            totalMeasurability);
        Dictionary<string, object?> lastBucket = BuildContextBucket(
            context.Measurable && context.Turns.Count > 0 ? context.Turns[^1] : null,
            measuredTurns.Count > 0 ? measuredTurns[^1].Tokens.ReportedInput : (counterInput ?? 0),
            bucketMap,
            totalMeasurability);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["measurable"] = context.Measurable,
            ["reason"] = context.Reason,
            ["contextLimit"] = hideLimit ? 0L : context.ContextLimit,
            ["contextLimitSource"] = contextLimitSource,
            ["turnCount"] = context.TurnCount,
            ["turns"] = serializedTurns,
            ["observedParts"] = context.ObservedParts,
            ["residualTotal"] = context.ResidualTotal,
            ["derivedCounts"] = context.DerivedCounts,
            ["freshJumpFactor"] = context.FreshJumpFactor,
            ["flaggedTurns"] = context.FlaggedTurns,
            ["sessionAccumulationRate"] = context.SessionAccumulationRate,
            ["first"] = firstBucket,
            ["last"] = lastBucket,
            ["unmeasuredTurns"] = unmeasuredTurns,
        };
    }

    internal static Dictionary<string, object?> BuildContextBucket(
        TurnPressure? turn,
        long reportedInput,
        Dictionary<string, string> bucketMap,
        IReadOnlyDictionary<string, JsonElement>? totalMeasurability)
    {
        Dictionary<string, object?> buckets = new(StringComparer.Ordinal);
        if (turn is not null)
        {
            foreach (KeyValuePair<string, long> kv in turn.Buckets)
            {
                buckets[kv.Key] = kv.Value;
            }
        }
        else if (totalMeasurability is not null)
        {
            foreach (KeyValuePair<string, string> kv in bucketMap)
            {
                if (TryUnavailable(totalMeasurability, kv.Value, out JsonElement unavail))
                {
                    buckets[kv.Value] = unavail;
                    buckets[kv.Key] = unavail;
                }
            }
        }

        object reported = TryUnavailable(totalMeasurability, "token_usage", out JsonElement tokUnavail)
            ? tokUnavail
            : reportedInput;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["buckets"] = buckets,
            ["reported_input"] = reported,
        };
    }

    internal static Dictionary<string, object?> WithoutAttributes(TimelineNode node) =>
        new(StringComparer.Ordinal)
        {
            ["spanId"] = node.SpanId,
            ["parentId"] = node.ParentId,
            ["startMs"] = node.StartMs,
            ["durationMs"] = node.DurationMs,
            ["kind"] = node.Kind,
            ["name"] = node.Name,
            ["status"] = node.Status,
            ["attributes"] = new Dictionary<string, object?>(StringComparer.Ordinal),
            ["isSubagent"] = node.IsSubagent,
            ["isAuxiliary"] = node.IsAuxiliary,
            ["cost"] = node.Cost,
            ["children"] = node.Children.Select(WithoutAttributes).ToList(),
        };


    internal static DroidSessionSplit SplitDroidSessionRecords(IReadOnlyList<CanonicalRecord> records)
    {
        List<CanonicalRecord> turnRecords = [];
        List<CanonicalRecord> counterRecords = [];
        List<CanonicalRecord> fileToolRecords = [];

        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Harness, DroidHarness, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(record.Source, DroidPluginSource, StringComparison.Ordinal))
            {
                turnRecords.Add(record);
                continue;
            }

            if (IsFileSource(record.Source))
            {
                counterRecords.Add(record);
                if (string.Equals(record.Op, "tool.invoke", StringComparison.Ordinal))
                {
                    fileToolRecords.Add(record);
                }

                continue;
            }

            turnRecords.Add(record);
        }

        return new DroidSessionSplit(turnRecords, counterRecords, fileToolRecords);
    }

    internal static bool IsRollup(CanonicalRecord record) =>
        string.Equals(record.Op, "agent.invoke", StringComparison.Ordinal)
        || (string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal)
            && string.Equals(AttributeOf(record, ["gen_ai.operation.name"]), "invoke_agent", StringComparison.Ordinal));

    internal static List<CanonicalRecord> TurnsOf(IReadOnlyList<CanonicalRecord> records)
    {
        List<CanonicalRecord> calls = records
            .Where(static r => string.Equals(r.Op, "llm.invoke", StringComparison.Ordinal) && !IsRollup(r))
            .ToList();
        if (calls.Count > 0)
        {
            return calls;
        }

        return records
            .Where(static r => IsRollup(r) && (CanonPricing.MeasuredInput(r.Tokens) > 0 || r.Tokens.Output > 0))
            .ToList();
    }

    internal static bool IsSideAgentTurn(CanonicalRecord record)
    {
        string? agentName = AttributeOf(record, ["gen_ai.agent.name", "agent.name", "agentName"]);
        return agentName is not null && SideAgentNames.Contains(agentName);
    }

    /// <summary>
    /// The analysis parts of <paramref name="record"/>, each stamped with the model the record was
    /// sent to so its text counts under that model's tokenizer (decision D17).
    /// </summary>
    internal static List<ContextPart> PartsOf(CanonicalRecord record)
    {
        string? model = ModelOf(record);
        if (record.Parts is { Count: > 0 })
        {
            return record.Parts.Select(part => ContextPart.FromContentPart(part, model)).ToList();
        }

        return record.Content.Select(kv => new ContextPart(kv.Key, kv.Value, Model: model)).ToList();
    }

    /// <summary>
    /// Re-stamps system-prompt parts carried forward from an earlier turn with the model of
    /// <paramref name="record"/>: the carried prompt is sent again to this turn's model, so this
    /// turn's tokenizer counts it.
    /// </summary>
    internal static IEnumerable<ContextPart> CarriedTo(IReadOnlyList<ContextPart> carried, CanonicalRecord record)
    {
        string? model = ModelOf(record);
        return carried.Select(part => part with { Model = model });
    }

    /// <summary>The model <paramref name="record"/> was sent to, or <see langword="null"/> when it names none.</summary>
    internal static string? ModelOf(CanonicalRecord record) => AttributeOf(record, ModelKeys);

    internal static HashSet<string> CarriedBucketsOf(IReadOnlyList<CanonicalRecord> records)
    {
        HashSet<string> carried = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            foreach (ContextPart part in PartsOf(record))
            {
                if (part.Text.Length > 0)
                {
                    carried.Add(part.Part);
                }
            }
        }

        return carried;
    }

    internal static Dictionary<string, JsonElement>? MergeMeasurability(IReadOnlyList<CanonicalRecord> records)
    {
        Dictionary<string, List<JsonElement>> seen = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            if (record.Measurability is null)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonElement> kv in record.Measurability)
            {
                if (!seen.TryGetValue(kv.Key, out List<JsonElement>? list))
                {
                    list = [];
                    seen[kv.Key] = list;
                }

                list.Add(kv.Value);
            }
        }

        if (seen.Count == 0)
        {
            return null;
        }

        Dictionary<string, JsonElement> merged = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<JsonElement>> kv in seen)
        {
            JsonElement? measured = null;
            JsonElement? derived = null;
            JsonElement? notMeasurable = null;
            foreach (JsonElement el in kv.Value)
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    string? s = el.GetString();
                    if (string.Equals(s, "measured", StringComparison.Ordinal))
                    {
                        measured ??= el.Clone();
                    }
                    else if (string.Equals(s, "derived", StringComparison.Ordinal))
                    {
                        derived ??= el.Clone();
                    }
                }
                else if (el.ValueKind == JsonValueKind.Object
                         && el.TryGetProperty("availability", out JsonElement avail)
                         && string.Equals(avail.GetString(), "not_measurable", StringComparison.Ordinal))
                {
                    notMeasurable ??= el.Clone();
                }
            }

            merged[kv.Key] = measured ?? derived ?? notMeasurable
                ?? JsonSerializer.Deserialize<JsonElement>("{\"availability\":\"not_measurable\",\"reason\":\"The source did not provide this metric.\"}");
        }

        return merged;
    }

    internal static IReadOnlyDictionary<string, JsonElement>? CompositionDeclaration(
        Dictionary<string, JsonElement>? measurability,
        HashSet<string> carriedBuckets)
    {
        if (measurability is null)
        {
            return null;
        }

        Dictionary<string, JsonElement> adjusted = new(measurability, StringComparer.Ordinal);
        foreach (string key in carriedBuckets)
        {
            if (adjusted.TryGetValue(key, out JsonElement el)
                && el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty("availability", out JsonElement avail)
                && string.Equals(avail.GetString(), "not_measurable", StringComparison.Ordinal))
            {
                adjusted.Remove(key);
            }
        }

        return adjusted;
    }

    internal static List<ToolDefinition> ToolDefinitionsOf(IReadOnlyList<CanonicalRecord> records, TextTokenCounter countTokens)
    {
        Dictionary<string, (string? Server, TokenCount Count, int TurnsResident)> byName = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            string? model = ModelOf(record);
            List<(string Text, string? Server, bool Catalogue)> rawDefs = [];
            if (record.Parts is { Count: > 0 })
            {
                foreach (ContentPart p in record.Parts)
                {
                    bool isDefs = string.Equals(p.Part, "tool_definitions", StringComparison.Ordinal);
                    bool isDef = string.Equals(p.Part, "tool_definition", StringComparison.Ordinal);
                    if (!isDefs && !isDef)
                    {
                        continue;
                    }

                    rawDefs.Add((p.Text, p.Server, isDefs));
                }
            }
            else
            {
                if (record.Content.TryGetValue("tool_definitions", out string? td))
                {
                    rawDefs.Add((td, null, false));
                }

                if (record.Content.TryGetValue("tool_definition", out string? t))
                {
                    rawDefs.Add((t, null, false));
                }
            }

            foreach ((string text, string? server, bool catalogue) in rawDefs)
            {
                if (!catalogue || string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                foreach ((string name, string itemText) in SplitToolDefinitionsText(text))
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string trimmed = name.Trim();
                    if (!byName.TryGetValue(trimmed, out (string? Server, TokenCount Count, int TurnsResident) existing))
                    {
                        byName[trimmed] = (server, countTokens(itemText, model), 1);
                    }
                    else
                    {
                        byName[trimmed] = existing with { TurnsResident = existing.TurnsResident + 1 };
                    }
                }
            }
        }

        return byName.Select(kv => new ToolDefinition(kv.Key, kv.Value.Server, kv.Value.Count.Count, kv.Value.TurnsResident, kv.Value.Count.Tokenizer, kv.Value.Count.IsModelOwn)).ToList();
    }

    internal static List<(string Name, string Text)> SplitToolDefinitionsText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tools", out JsonElement toolsEl) && toolsEl.ValueKind == JsonValueKind.Array
                    ? toolsEl.EnumerateArray()
                    : [root];

            List<(string Name, string Text)> result = [];
            foreach (JsonElement item in items)
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        result.Add((s.Trim(), s));
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    string itemText = item.GetRawText();
                    string name = string.Empty;
                    if (item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                    {
                        name = n.GetString()!.Trim();
                    }
                    else if (item.TryGetProperty("function", out JsonElement fn) && fn.ValueKind == JsonValueKind.Object
                             && fn.TryGetProperty("name", out JsonElement fnName) && fnName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(fnName.GetString()))
                    {
                        name = fnName.GetString()!.Trim();
                    }

                    result.Add((name, itemText));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [(string.Empty, text)];
        }
    }

    internal static List<string> InvocationsOf(IReadOnlyList<CanonicalRecord> records)
    {
        List<string> result = [];
        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "tool.invoke", StringComparison.Ordinal))
            {
                continue;
            }

            string name = (AttributeOf(record, ["gen_ai.tool.name", "tool.name", "tool_name"]) ?? record.Name).Trim();
            if (name.Length > 0 && !string.Equals(name, "tool.invoke", StringComparison.Ordinal))
            {
                result.Add(name);
            }
        }

        return result;
    }

    internal static bool IsToolError(CanonicalRecord record)
    {
        if (string.Equals(record.Status, "error", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(AttributeOf(record, ["gen_ai.tool.status", "status"]), "error", StringComparison.Ordinal))
        {
            return true;
        }

        if (record.Raw is not null)
        {
            if (record.Raw.TryGetValue("gen_ai.tool.status", out JsonElement st) && st.ValueKind == JsonValueKind.String && string.Equals(st.GetString(), "error", StringComparison.Ordinal))
            {
                return true;
            }

            if (record.Raw.TryGetValue("is_error", out JsonElement ie) && ie.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (record.Raw.TryGetValue("isError", out JsonElement ie2) && ie2.ValueKind == JsonValueKind.True)
            {
                return true;
            }
        }

        return false;
    }

    internal static List<string> ModelsOf(IReadOnlyList<CanonicalRecord> records)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> list = [];
        foreach (CanonicalRecord record in records)
        {
            string? model = AttributeOf(record, ModelKeys);
            if (model is not null && seen.Add(model))
            {
                list.Add(model);
            }
        }

        return list;
    }

    internal static (string Started, string Ended) TimestampBounds(IReadOnlyList<CanonicalRecord> records)
    {
        string? minTs = null;
        string? maxTs = null;
        double minMs = double.PositiveInfinity;
        double maxMs = double.NegativeInfinity;

        foreach (CanonicalRecord record in records)
        {
            double ms = RecordTimestampMs(record);
            if (!double.IsFinite(ms))
            {
                continue;
            }

            if (minTs is null || ms < minMs)
            {
                minTs = record.Timestamp;
                minMs = ms;
            }

            if (maxTs is null || ms >= maxMs)
            {
                maxTs = record.Timestamp;
                maxMs = ms;
            }
        }

        return (minTs ?? records[0].Timestamp, maxTs ?? records[^1].Timestamp);
    }

    internal static string SessionLabelOf(
        IReadOnlyList<CanonicalRecord> records,
        string fallbackNamed,
        string harness,
        string sessionId)
    {
        string? bestLabel = null;
        double bestTime = double.NegativeInfinity;
        foreach (CanonicalRecord record in records)
        {
            string? label = AttributeOf(record, [SessionLabelKey])?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            double time = RecordTimestampMs(record);
            bool later = bestLabel is null
                || (double.IsFinite(time) && (!double.IsFinite(bestTime) || time >= bestTime))
                || (!double.IsFinite(time) && !double.IsFinite(bestTime));
            if (later)
            {
                bestLabel = label;
                bestTime = time;
            }
        }

        return bestLabel ?? CanonPricing.FormatSessionTitle(new SessionTitleInput(
            Label: fallbackNamed,
            Harness: harness,
            SessionId: sessionId));
    }

    internal static Dictionary<string, List<CanonicalRecord>> TurnsByRequestRoot(
        IReadOnlyList<CanonicalRecord> records,
        IReadOnlyList<CanonicalRecord> turnRecords)
    {
        Dictionary<string, CanonicalRecord> bySpan = new(StringComparer.Ordinal);
        foreach (CanonicalRecord r in records)
        {
            bySpan.TryAdd(r.SpanId, r);
        }

        Dictionary<string, string?> rootOf = new(StringComparer.Ordinal);
        string? Resolve(CanonicalRecord start)
        {
            List<string> path = [];
            HashSet<string> onPath = new(StringComparer.Ordinal);
            string? root = null;
            CanonicalRecord? cursor = start;
            while (cursor is not null)
            {
                if (rootOf.TryGetValue(cursor.SpanId, out string? known))
                {
                    root = known;
                    break;
                }

                if (!onPath.Add(cursor.SpanId))
                {
                    break;
                }

                path.Add(cursor.SpanId);
                if (cursor.ParentSpanId is null)
                {
                    root = cursor.SpanId;
                    break;
                }

                bySpan.TryGetValue(cursor.ParentSpanId, out cursor);
            }

            foreach (string id in path)
            {
                rootOf[id] = root;
            }

            return root;
        }

        Dictionary<string, List<CanonicalRecord>> owned = new(StringComparer.Ordinal);
        foreach (CanonicalRecord turn in turnRecords)
        {
            string? root = Resolve(turn);
            if (root is null)
            {
                continue;
            }

            if (!owned.TryGetValue(root, out List<CanonicalRecord>? list))
            {
                list = [];
                owned[root] = list;
            }

            list.Add(turn);
        }

        return owned;
    }

    internal static bool HasNesting(IReadOnlyList<CanonicalRecord> records)
    {
        HashSet<string> ids = new(records.Select(static r => r.SpanId), StringComparer.Ordinal);
        return records.Any(r => r.ParentSpanId is not null && !string.Equals(r.ParentSpanId, r.SpanId, StringComparison.Ordinal) && ids.Contains(r.ParentSpanId));
    }

    internal static string? PromptPreviewOf(CanonicalRecord record)
    {
        string? history = null;
        if (record.Parts is { Count: > 0 })
        {
            history = record.Parts
                .Where(static p => string.Equals(p.Part, "conversation_history", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(p.Text))
                .Select(static p => p.Text)
                .FirstOrDefault();
        }
        else if (record.Content.TryGetValue("conversation_history", out string? flat) && !string.IsNullOrWhiteSpace(flat))
        {
            history = flat;
        }

        if (history is null)
        {
            return null;
        }

        if (history.Length <= PromptPreviewLength)
        {
            return history;
        }

        int end = char.IsHighSurrogate(history[PromptPreviewLength - 1]) ? PromptPreviewLength - 1 : PromptPreviewLength;
        return history[..end];
    }

    internal static Dictionary<string, List<CanonicalRecord>> GroupByCanonicalHarness(IReadOnlyList<CanonicalRecord> records)
    {
        Dictionary<string, List<CanonicalRecord>> groups = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            string? id = CanonPricing.CanonicalHarnessId(record.Harness);
            if (id is null)
            {
                continue;
            }

            if (!groups.TryGetValue(id, out List<CanonicalRecord>? list))
            {
                list = [];
                groups[id] = list;
            }

            list.Add(record);
        }

        return groups;
    }

    internal static bool HasEvidence(IReadOnlyList<CanonicalRecord> records) =>
        TurnsOf(records).Any(static r =>
            !string.Equals(CanonPricing.NormalizeHarnessName(r.Harness), "unattributed", StringComparison.Ordinal)
            || CanonPricing.MeasuredInput(r.Tokens) > 0
            || r.Tokens.Output > 0
            || (r.Parts is { Count: > 0 } && r.Parts.Any(static p => p.Text.Length > 0))
            || ((r.Parts is null || r.Parts.Count == 0) && r.Content.Count > 0));

    internal static bool IsInvokeAgentSpan(CanonicalRecord record) =>
        string.Equals(record.Op, "agent.invoke", StringComparison.Ordinal)
        || string.Equals(AttributeOf(record, ["gen_ai.operation.name"]), "invoke_agent", StringComparison.Ordinal)
        || InvokeAgentNameRegex().IsMatch(record.Name)
        || string.Equals(AttributeOf(record, ["gen_ai.tool.name", "tool.name", "tool_name"]), "invoke_agent", StringComparison.Ordinal);

    internal static List<CanonicalRecord> CollectDescendantRecords(string rootSpanId, IReadOnlyList<CanonicalRecord> records)
    {
        Dictionary<string, List<CanonicalRecord>> byParent = new(StringComparer.Ordinal);
        foreach (CanonicalRecord r in records)
        {
            if (string.IsNullOrEmpty(r.ParentSpanId))
            {
                continue;
            }

            if (!byParent.TryGetValue(r.ParentSpanId, out List<CanonicalRecord>? list))
            {
                list = [];
                byParent[r.ParentSpanId] = list;
            }

            list.Add(r);
        }

        List<CanonicalRecord> result = [];
        Queue<string> queue = new();
        HashSet<string> seen = new(StringComparer.Ordinal) { rootSpanId };
        queue.Enqueue(rootSpanId);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!byParent.TryGetValue(current, out List<CanonicalRecord>? children))
            {
                continue;
            }

            foreach (CanonicalRecord child in children)
            {
                if (!seen.Add(child.SpanId))
                {
                    continue;
                }

                result.Add(child);
                if (!IsInvokeAgentSpan(child))
                {
                    queue.Enqueue(child.SpanId);
                }
            }
        }

        return result;
    }

    internal static string? ExtractAgentName(CanonicalRecord invokeSpan)
    {
        string? attr = AttributeOf(invokeSpan, AgentNameKeys)?.Trim();
        if (!string.IsNullOrEmpty(attr))
        {
            return attr;
        }

        const string prefix = "invoke_agent ";
        if (invokeSpan.Name.StartsWith(prefix, StringComparison.Ordinal))
        {
            string candidate = invokeSpan.Name[prefix.Length..].Trim();
            if (candidate.Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    internal static string? ExtractTaskSummary(IReadOnlyList<CanonicalRecord> subagentRecords)
    {
        foreach (CanonicalRecord r in subagentRecords)
        {
            string? promptAttr = AttributeOf(r, ["gen_ai.prompt", "prompt", "task.description"]);
            if (!string.IsNullOrWhiteSpace(promptAttr))
            {
                return promptAttr.Trim();
            }

            string? preview = PromptPreviewOf(r);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                return preview;
            }
        }

        return null;
    }

    internal static bool IsClaudeHarness(string harness)
    {
        string norm = CanonPricing.NormalizeHarnessName(harness);
        return string.Equals(norm, "claude", StringComparison.Ordinal)
            || string.Equals(norm, "claude-code", StringComparison.Ordinal)
            || string.Equals(CanonPricing.SurveyFamily(harness), "claude-code", StringComparison.Ordinal);
    }

    internal static string? ExtractClaudeAgentId(CanonicalRecord record)
    {
        string? fromAttr = AttributeOf(record, ["agent_id", "agentId", "subagent_id", "subagentId", "agent.id", "gen_ai.agent.id"])?.Trim();
        if (!string.IsNullOrEmpty(fromAttr))
        {
            return fromAttr;
        }

        return record.SpanId.StartsWith("agent-", StringComparison.Ordinal) ? record.SpanId : null;
    }

    internal static string ClaudeSubagentSessionId(string parentSessionId, string agentId)
    {
        string trimmed = agentId.Trim();
        string bare = trimmed.StartsWith("agent-", StringComparison.Ordinal) ? trimmed["agent-".Length..] : trimmed;
        return $"{parentSessionId}#agent-{bare}";
    }

    internal static string? ExtractClaudeAgentName(IReadOnlyList<CanonicalRecord> records)
    {
        foreach (CanonicalRecord record in records)
        {
            string? attr = AttributeOf(record, ["agent_type", "agentType", "gen_ai.agent.name", "gen_ai.agent.type", "agent_name", "agentName", "agent.name", "agent"])?.Trim();
            if (!string.IsNullOrEmpty(attr))
            {
                return attr;
            }
        }

        foreach (CanonicalRecord record in records)
        {
            string? name = ExtractAgentName(record);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        return null;
    }

    internal static string? ExtractClaudeDescription(IReadOnlyList<CanonicalRecord> records)
    {
        foreach (CanonicalRecord record in records)
        {
            string? attr = AttributeOf(record, ["description", "task_description", "taskDescription", "task", "gen_ai.agent.description", "summary", "prompt"])?.Trim();
            if (!string.IsNullOrEmpty(attr))
            {
                return attr;
            }
        }

        return ExtractTaskSummary(records);
    }

    internal static string? AttributeOf(CanonicalRecord record, IReadOnlyList<string> keys)
    {
        if (record.Raw is null)
        {
            return null;
        }

        foreach (string key in keys)
        {
            if (!record.Raw.TryGetValue(key, out JsonElement el))
            {
                continue;
            }

            if (el.ValueKind == JsonValueKind.String)
            {
                string? s = el.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    return s;
                }
            }
            else if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) && double.IsFinite(d))
            {
                return d.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    internal static bool IsNotMeasurableElement(IReadOnlyDictionary<string, JsonElement>? measurability, string key) =>
        measurability is not null
        && measurability.TryGetValue(key, out JsonElement el)
        && el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty("availability", out JsonElement avail)
        && string.Equals(avail.GetString(), "not_measurable", StringComparison.Ordinal);

    internal static bool IsMeasuredOrDerived(IReadOnlyDictionary<string, JsonElement>? measurability, string key) =>
        measurability is not null
        && measurability.TryGetValue(key, out JsonElement el)
        && el.ValueKind == JsonValueKind.String
        && (string.Equals(el.GetString(), "measured", StringComparison.Ordinal)
            || string.Equals(el.GetString(), "derived", StringComparison.Ordinal));

    internal static bool TryUnavailable(
        IReadOnlyDictionary<string, JsonElement>? measurability,
        string key,
        out JsonElement element)
    {
        if (measurability is not null
            && measurability.TryGetValue(key, out JsonElement el)
            && el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("availability", out JsonElement avail)
            && string.Equals(avail.GetString(), "not_measurable", StringComparison.Ordinal))
        {
            element = el;
            return true;
        }

        element = default;
        return false;
    }

    internal static object UnavailableOrValue(
        IReadOnlyDictionary<string, JsonElement>? measurability,
        IReadOnlyList<string> keys,
        long value)
    {
        foreach (string key in keys)
        {
            if (TryUnavailable(measurability, key, out JsonElement el))
            {
                return el;
            }
        }

        return value;
    }

}

internal static class StoredProjectionHelpers
{
    internal static IEnumerable<string> PriorIdsOf(
        string key,
        IReadOnlyList<string> harnessIds,
        SessionIdentities identities)
    {
        foreach (string harness in harnessIds)
        {
            string? id = identities.IdFor(key, harness);
            if (id is not null)
            {
                yield return id;
            }
        }
    }


    internal static StoredFinding ToStoredFinding(Finding finding)
    {
        StoredCompactionHazard? hazard = null;
        if (finding.Payload is not null
            && finding.Payload.TryGetValue("contextLimit", out object? limitValue)
            && finding.Payload.TryGetValue("contextLimitSource", out object? sourceValue)
            && limitValue is long limit
            && sourceValue is string limitSource)
        {
            hazard = new StoredCompactionHazard(limit, limitSource);
        }

        return new StoredFinding(
            FindingId: finding.Id,
            DetectorId: finding.DetectorId,
            Title: finding.Title,
            Mechanism: finding.Mechanism,
            Confidence: finding.Confidence,
            Recommendation: finding.Recommendation,
            OutcomeRiskCaveat: finding.OutcomeRiskCaveat,
            RankScore: finding.RankScore,
            EvidenceLinks: finding.EvidenceLinks,
            EstimatedWasteTokens: finding.EstimatedWasteTokens,
            ErrorBar: finding.ErrorBar,
            RunId: finding.RunId,
            SessionId: finding.SessionId,
            MeasurementClass: finding.MeasurementClass,
            ConfidenceBasis: finding.ConfidenceBasis,
            WhatWouldRaiseIt: finding.WhatWouldRaiseIt,
            CompactionHazard: hazard);
    }

    /// <summary>
    /// Fresh input tokens for the fresh-input jump check, or <see langword="null"/> when the turn reported
    /// none. A zero fresh class carries no evidence the harness measured it (the canonical record holds
    /// unreported classes as zero), so it is not-measured rather than a measured zero (D15).
    /// </summary>
    internal static long? MeasuredFreshInput(TokenUsage tokens) => tokens.FreshInput > 0 ? tokens.FreshInput : null;

    /// <summary>
    /// Rebuilds one typed <see cref="StoredSessionProjection"/> from a session's deduplicated
    /// records: context pressure analysis, schema ranking, timeline marks, requests, and turns,
    /// with exclusive unmeasurable reasons in place of non-measurable figures.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier (a <c>session</c> table key).</param>
    /// <param name="records">Deduplicated session records in timestamp order.</param>
    /// <param name="countTokens">Counts a text with the tokenizer of the model it was sent to.</param>
    /// <param name="title">Session title from the relational <c>session</c> row, when present.</param>
    /// <returns>Projected typed session row.</returns>
    internal static StoredSessionProjection BuildStoredSessionProjection(
        string sessionId,
        IReadOnlyList<CanonicalRecord> records,
        TextTokenCounter countTokens,
        string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(countTokens);
        if (records.Count == 0)
        {
            throw new ArgumentException("At least one record is required to build a session projection.", nameof(records));
        }

        DroidSessionSplit split = SplitDroidSessionRecords(records);
        bool paired = split.TurnRecords.Count > 0 && split.CounterRecords.Count > 0;
        List<CanonicalRecord> turnRecords = paired ? TurnsOf(split.TurnRecords) : TurnsOf(records);
        IReadOnlyList<CanonicalRecord> structureRecords = paired ? split.TurnRecords : records;

        CanonicalRecord first = records[0];
        string harness = CanonPricing.NormalizeHarnessName(first.Harness);

        string? resolvedAgentName = AttributeOf(first, AgentNameKeys);
        string resolvedLabel = SessionLabelOf(records, title ?? first.Name, harness, sessionId);
        string? parentAttr = AttributeOf(first, ParentSessionKeys);
        string? subagentKeyParent = TryExtractSubagentParentId(sessionId);
        string? resolvedParent = parentAttr ?? subagentKeyParent;
        bool resolvedIsSubagent = resolvedParent is not null
            || AttributeOf(first, ["gen_ai.is_subagent", "is_subagent"]) is not null;

        List<CanonicalRecord> mainThreadRecords = turnRecords
            .Where(record => !IsSideAgentTurn(record) && (resolvedIsSubagent || TryExtractSubagentParentId(record.SessionId) is null))
            .ToList();
        List<CanonicalRecord> contextRecords = mainThreadRecords.Count > 0 ? mainThreadRecords : turnRecords;

        List<ContextTurn> contextTurns = IsClaudeHarness(harness)
            ? BuildClaudeContextTurns(contextRecords, countTokens)
            : BuildStandardContextTurns(contextRecords.Where(static record => CanonPricing.MeasuredInput(record.Tokens) > 0).ToList());

        IReadOnlyList<CanonicalRecord> windowRecords = contextRecords.Count > 0 ? contextRecords : records;
        ContextWindowResult window = CanonPricing.ContextLimitOf(windowRecords);
        long analysisLimit = window.ContextLimit > 0 ? window.ContextLimit : CanonPricing.DefaultContextLimit;

        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            contextTurns,
            new ContextCountOptions(
                ContextLimit: analysisLimit,
                ContextLimitSource: window.ContextLimitSource),
            countTokens);

        List<ToolDefinition> definitions = [.. TypedToolDefinitionsOf(records)];
        HashSet<string> knownDefinitions = [.. definitions.Select(static definition => definition.Name)];
        IReadOnlyList<CanonicalRecord> toolSourceRecords = paired && split.FileToolRecords.Count > 0
            ? split.FileToolRecords
            : structureRecords;
        definitions.AddRange(ToolDefinitionsOf(records, countTokens)
            .Where(definition => knownDefinitions.Add(definition.Name)));
        SchemaCostAnalysis schema = CanonContextAnalysis.RankSchemas(
            definitions,
            turnRecords.Count,
            InvocationsOf(toolSourceRecords));

        TimelineNode timeline = CanonContextAnalysis.BuildTimeline(structureRecords);

        List<CanonicalRecord> requestRoots = structureRecords.Where(static record => record.ParentSpanId is null).ToList();
        (string started, string ended) = TimestampBounds(records);

        List<StoredSessionTurn> turns = new(turnRecords.Count);
        for (int position = 0; position < turnRecords.Count; position++)
        {
            turns.Add(new StoredSessionTurn(sessionId, position, turnRecords[position].SpanId));
        }

        return new StoredSessionProjection(
            SessionId: sessionId,
            Label: resolvedLabel,
            IsSubagent: resolvedIsSubagent,
            SpanCount: records.Count,
            StartedAtUtc: started,
            EndedAtUtc: ended,
            ParentSessionId: resolvedParent,
            AgentName: resolvedAgentName,
            IncludedInParent: false,
            RequestSpanIds: [.. requestRoots.Select(static record => record.SpanId)],
            Turns: turns,
            Context: context.Measurable ? ToStoredContext(context) : null,
            ContextUnmeasurableReason: context.Measurable ? null : context.Reason ?? NoMessageStructureReason,
            Schema: schema.Measurable ? ToStoredSchema(schema) : null,
            SchemaUnmeasurableReason: schema.Measurable ? null : schema.Reason ?? NoToolDefinitionSchemaReason,
            TimelineSubagentSpanIds: TimelineSubagentSpanIdsOf(timeline),
            TimelineAuxiliarySpanIds: TimelineAuxiliarySpanIdsOf(timeline));
    }

    internal static List<ContextTurn> BuildStandardContextTurns(IReadOnlyList<CanonicalRecord> measuredTurns)
    {
        List<ContextTurn> contextTurns = new(measuredTurns.Count);
        List<ContextPart> lastSystem = [];
        foreach (CanonicalRecord record in measuredTurns)
        {
            List<ContextPart> parts = PartsOf(record);
            List<ContextPart> ownSystem = parts.Where(static part => string.Equals(part.Part, "system_prompt", StringComparison.Ordinal)).ToList();
            if (ownSystem.Count > 0)
            {
                lastSystem = ownSystem;
            }

            IReadOnlyList<ContextPart> effective = ownSystem.Count > 0 ? parts : [.. CarriedTo(lastSystem, record), .. parts];
            contextTurns.Add(new ContextTurn(
                Parts: effective,
                InputTokens: CanonPricing.MeasuredInput(record.Tokens),
                FreshInput: MeasuredFreshInput(record.Tokens)));
        }

        return contextTurns;
    }

    internal static List<ContextTurn> BuildClaudeContextTurns(
        IReadOnlyList<CanonicalRecord> contextRecords,
        TextTokenCounter countTokens)
    {
        List<ContextTurn> contextTurns = new(contextRecords.Count);
        Dictionary<CumulativeSummaryKey, CumulativeBucketEntry> cumulative = [];

        foreach (CanonicalRecord record in contextRecords)
        {
            if (HasCompactBoundary(record))
            {
                cumulative.Clear();
            }

            if (CanonPricing.MeasuredInput(record.Tokens) <= 0)
            {
                continue;
            }

            (List<ContextPart> ownInputParts, List<ContextPart> ownAssistantParts) = SplitClaudeTurnParts(record);
            IReadOnlyList<ContextPart> reducedInput = ownInputParts.Count > 0
                ? CanonContextAnalysis.ReduceContextParts(ownInputParts, countTokens)
                : [];
            IReadOnlyList<ContextPart> reducedAssistant = ownAssistantParts.Count > 0
                ? CanonContextAnalysis.ReduceContextParts(ownAssistantParts, countTokens)
                : [];

            bool hasOwnSystemPrompt = reducedInput.Any(
                static part => string.Equals(part.Part, "system_prompt", StringComparison.Ordinal) && part.Resident != false);
            if (hasOwnSystemPrompt && cumulative.Count > 0)
            {
                List<CumulativeSummaryKey> systemKeys = [.. cumulative.Keys.Where(static key => string.Equals(key.Part, "system_prompt", StringComparison.Ordinal))];
                foreach (CumulativeSummaryKey systemKey in systemKeys)
                {
                    cumulative.Remove(systemKey);
                }
            }

            List<ContextPart> effective = new(cumulative.Count + reducedInput.Count);
            foreach (KeyValuePair<CumulativeSummaryKey, CumulativeBucketEntry> entry in cumulative)
            {
                CumulativeSummaryKey key = entry.Key;
                CumulativeBucketEntry summary = entry.Value;
                effective.Add(new ContextPart(
                    Part: key.Part,
                    Text: string.Empty,
                    Tokens: summary.Tokens,
                    Derived: summary.Derived,
                    InstructionTokens: summary.HasInstructionTokens ? summary.InstructionTokens : null,
                    StrippedCount: summary.HasStrippedCount ? summary.StrippedCount : null,
                    Tokenizer: key.Tokenizer,
                    TokenizerIsModelOwn: key.TokenizerIsModelOwn));
            }

            effective.AddRange(reducedInput);
            contextTurns.Add(new ContextTurn(
                Parts: effective,
                InputTokens: CanonPricing.MeasuredInput(record.Tokens),
                FreshInput: MeasuredFreshInput(record.Tokens)));

            AccumulateClaudeSummaryParts(cumulative, reducedInput);
            AccumulateClaudeSummaryParts(cumulative, reducedAssistant);
        }

        return contextTurns;
    }

    internal static (List<ContextPart> OwnInputParts, List<ContextPart> OwnAssistantParts) SplitClaudeTurnParts(CanonicalRecord record)
    {
        string? model = ModelOf(record);
        List<ContextPart> ownInputParts = [];
        List<ContextPart> ownAssistantParts = [];

        if (record.Parts is { Count: > 0 })
        {
            foreach (ContentPart part in record.Parts)
            {
                ContextPart contextPart = ContextPart.FromContentPart(part, model);
                if (string.Equals(part.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    if (part.Resident != false && ClaudeCumulativeBuckets.Contains(part.Part))
                    {
                        ownAssistantParts.Add(contextPart);
                    }
                }
                else
                {
                    ownInputParts.Add(contextPart);
                }
            }

            return (ownInputParts, ownAssistantParts);
        }

        foreach (KeyValuePair<string, string> kv in record.Content)
        {
            ownInputParts.Add(new ContextPart(kv.Key, kv.Value, Model: model));
        }

        return (ownInputParts, ownAssistantParts);
    }

    internal static void AccumulateClaudeSummaryParts(
        Dictionary<CumulativeSummaryKey, CumulativeBucketEntry> cumulative,
        IReadOnlyList<ContextPart> parts)
    {
        foreach (ContextPart part in parts)
        {
            if (part.Resident == false || !ClaudeCumulativeBuckets.Contains(part.Part))
            {
                continue;
            }

            long? tokens = CanonPricing.MeasuredCount(part.Tokens);
            if (!tokens.HasValue && !part.InstructionTokens.HasValue)
            {
                continue;
            }

            CumulativeSummaryKey key = new(part.Part, part.Tokenizer, part.TokenizerIsModelOwn);
            if (!cumulative.TryGetValue(key, out CumulativeBucketEntry? entry))
            {
                entry = new CumulativeBucketEntry();
                cumulative[key] = entry;
            }

            entry.Tokens += tokens ?? 0L;
            if (part.Derived || part.Tokenizer is not null)
            {
                entry.Derived = true;
            }

            if (part.InstructionTokens.HasValue)
            {
                entry.InstructionTokens += part.InstructionTokens.Value;
                entry.HasInstructionTokens = true;
            }

            if (part.StrippedCount.HasValue)
            {
                entry.StrippedCount += part.StrippedCount.Value;
                entry.HasStrippedCount = true;
            }
        }
    }

    internal static bool HasCompactBoundary(CanonicalRecord record) =>
        record.Raw is not null
        && record.Raw.TryGetValue(CanonRecordHydration.CompactBoundaryRawKey, out JsonElement element)
        && element.ValueKind == JsonValueKind.True;

    internal static string? TryExtractSubagentParentId(string? sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            return null;
        }

        int markerIndex = sessionKey.IndexOf(SubagentSessionKeyMarker, StringComparison.Ordinal);
        if (markerIndex <= 0 || markerIndex + SubagentSessionKeyMarker.Length >= sessionKey.Length)
        {
            return null;
        }

        string parent = sessionKey[..markerIndex];
        return string.IsNullOrWhiteSpace(parent) ? null : parent;
    }

    internal readonly record struct CumulativeSummaryKey(
        string Part,
        string? Tokenizer,
        bool? TokenizerIsModelOwn);

    internal sealed class CumulativeBucketEntry
    {
        public long Tokens { get; set; }

        public bool Derived { get; set; }

        public long InstructionTokens { get; set; }

        public bool HasInstructionTokens { get; set; }

        public int StrippedCount { get; set; }

        public bool HasStrippedCount { get; set; }
    }

    internal static StoredSessionContext ToStoredContext(ContextAnalysis context)
    {
        List<StoredSessionContextTurn> turns = new(context.Turns.Count);
        foreach (TurnPressure turn in context.Turns)
        {
            turns.Add(new StoredSessionContextTurn(
                Position: turn.Index - 1,
                InputTokens: turn.InputTokens,
                BucketedTokens: turn.BucketedTokens,
                ResidualTokens: turn.Residual.Tokens,
                ResidualAttribution: turn.Residual.Attribution,
                BuiltinToolDefinitionTokens: turn.BuiltinToolDefinitionTokens,
                FreshInput: turn.FreshInput ?? 0,
                Buckets: [.. turn.Buckets
                    .Where(static entry => entry.Value != 0)
                    .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .Select(static entry => new StoredTurnBucketTokens(entry.Key, entry.Value))],
                ServerDefinitions: [.. turn.ToolDefinitionsByServer
                    .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .Select(static entry => new StoredTurnServerDefinitionTokens(entry.Key, entry.Value))],
                Headroom: turn.Headroom,
                Pressure: turn.Pressure,
                AccumulationRate: turn.AccumulationRate,
                StrippedBlockCount: turn.StrippedInstructionBlocks.Count,
                StrippedTokens: turn.StrippedInstructionBlocks.Tokens,
                PreviousFreshInput: turn.FreshInputJump?.Previous,
                FreshJumpFactor: turn.FreshInputJump is null
                    ? null
                    : double.IsFinite(turn.FreshInputJump.Factor) ? turn.FreshInputJump.Factor : null,
                NonResidentTokens: turn.NonResidentToolDefinitions)
            {
                Tokenizers = turn.Tokenizers,
            });
        }

        return new StoredSessionContext(
            ContextLimit: context.ContextLimit,
            ContextLimitSource: context.ContextLimitSource ?? DefaultContextLimitSource,
            ResidualTotal: context.ResidualTotal,
            FreshJumpFactor: context.FreshJumpFactor,
            AccumulationRate: context.SessionAccumulationRate,
            ObservedBuckets: context.ObservedParts,
            // The analysis reports only whether some counts were derived; it does not expose
            // per-counter attribution, so no derived-count rows can be named honestly here.
            DerivedCounts: [],
            FlaggedTurnIndexes: [.. context.FlaggedTurns.Select(static index => index - 1)],
            Turns: turns);
    }

    internal static StoredSessionSchema ToStoredSchema(SchemaCostAnalysis schema)
    {
        List<StoredSchemaRankedTool> rankedTools = new(schema.Ranked.Count);
        for (int position = 0; position < schema.Ranked.Count; position++)
        {
            RankedTool tool = schema.Ranked[position];
            rankedTools.Add(new StoredSchemaRankedTool(
                Position: position,
                ToolName: tool.Name,
                CostTokens: tool.Cost,
                Invoked: tool.Invoked,
                ServerName: tool.Server));
        }

        return new StoredSessionSchema(
            InvocationCount: schema.InvocationCount,
            TurnCount: schema.Turns,
            RankedTools: rankedTools,
            Servers: [.. schema.ByServer.Select(static entry => new StoredSchemaServerTokens(entry.Key, entry.Value))],
            UnusedTokenResidencies: schema.UnusedRange?.TokenResidencies);
    }

    internal static IReadOnlyList<string> TimelineSubagentSpanIdsOf(TimelineNode root)
    {
        List<string> spanIds = [];
        void Walk(TimelineNode node)
        {
            if (node.IsSubagent && !IsSyntheticTimelineId(node.SpanId))
            {
                spanIds.Add(node.SpanId);
            }

            foreach (TimelineNode child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(root);
        return spanIds;
    }

    internal static IReadOnlyList<string> TimelineAuxiliarySpanIdsOf(TimelineNode root)
    {
        List<string> spanIds = [];
        void Walk(TimelineNode node)
        {
            if (node.IsAuxiliary && !IsSyntheticTimelineId(node.SpanId))
            {
                spanIds.Add(node.SpanId);
            }

            foreach (TimelineNode child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(root);
        return spanIds;
    }

    internal static bool IsSyntheticTimelineId(string spanId) => spanId.StartsWith('(');

    /// <summary>
    /// Collects the typed tool definitions carried on hydrated records: each relational
    /// <c>tool_definition</c> row hydrates as a <c>tool_definitions</c> part whose text is the
    /// tool name and whose token count is the measured <c>schema_tokens</c>.
    /// </summary>
    /// <param name="records">Hydrated session records.</param>
    /// <returns>Distinct tool definitions with residency counts.</returns>
    internal static List<ToolDefinition> TypedToolDefinitionsOf(IReadOnlyList<CanonicalRecord> records)
    {
        Dictionary<string, (string? Server, long Tokens, int TurnsResident)> byName = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            HashSet<string> seenInRecord = new(StringComparer.Ordinal);
            foreach (ContentPart part in record.Parts ?? [])
            {
                if (!string.Equals(part.Part, "tool_definitions", StringComparison.Ordinal) || part.Tokens is not > 0)
                {
                    continue;
                }

                string name = part.Text.Trim();
                if (name.Length == 0 || !seenInRecord.Add(name))
                {
                    continue;
                }

                if (byName.TryGetValue(name, out (string? Server, long Tokens, int TurnsResident) existing))
                {
                    byName[name] = (existing.Server ?? part.Server, Math.Max(existing.Tokens, part.Tokens.Value), existing.TurnsResident + 1);
                }
                else
                {
                    byName[name] = (part.Server, part.Tokens.Value, 1);
                }
            }
        }

        return [.. byName.Select(static entry => new ToolDefinition(entry.Key, entry.Value.Server, entry.Value.Tokens, entry.Value.TurnsResident))];
    }
}
