using System.Text;
using System.Text.Json;
using KyberDash.Application.Tests;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Read-path projection pass behaviour: an incremental pass plans from
/// <see cref="IRelationalStoreReader.SessionAttributions"/> and materializes only the rebuilt
/// session's records, attribution-driven claiming assigns the session ids record iteration
/// assigned, the read-path entry defers oversized dirty sessions and writes them back as still
/// dirty, a second <see cref="KyberBridgeEngine.EnsureProjected"/> returns while a first pass
/// holds the reader (single flight), and deferred sessions surface in projection health.
/// </summary>
public class ProjectionReadPathTests
{
    private const int ReadPathBudget = 2;

    /// <summary>
    /// One dirty session of two: the incremental pass materializes only the dirty session's
    /// records (instrumented fake read log), never issues a whole-store record read, skips the
    /// clean session, and the rebuilt projection is identical to a full pass's.
    /// </summary>
    [Fact]
    public void AnIncrementalPassReadsOnlyTheDirtySessionsRecords()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine engine = NewEngine(store);

        const string alphaText = "alpha session turn content";
        const string betaFirstText = "beta first turn content";
        const string betaSecondText = "beta second turn content";

        WriteSession(
            store,
            "sess-a",
            "claude-code",
            [Turn("span-a-1", "sess-a", 0, harnessId: "claude-code", inputTokens: 400, outputTokens: 40, parts: [TextPart(0, alphaText)])]);
        WriteSession(
            store,
            "sess-b",
            "codex",
            [Turn("span-b-1", "sess-b", 0, harnessId: "codex", inputTokens: 300, outputTokens: 30, parts: [TextPart(0, betaFirstText)])]);
        store.SetWriteMarker(1);
        _ = engine.ProjectIncremental();

        WriteSession(
            store,
            "sess-b",
            "codex",
            [Turn("span-b-2", "sess-b", 1, harnessId: "codex", inputTokens: 200, outputTokens: 20, parts: [TextPart(0, betaSecondText)])]);
        store.SetWriteMarker(2);
        store.SetProjectionDirtyState(new DirtyProjectionState(FromSeq: 1, ToSeq: 2, Keys: ["sess-b"], IsWildcard: false));
        store.ClearReadLog();

        ProjectionPassReport pass = engine.ProjectIncremental();

        Assert.Equal(["sess-b"], pass.RebuiltKeys);
        Assert.Contains("sess-a", pass.SkippedKeys);

        Assert.Contains(nameof(InMemoryRelationalStore.SessionAttributions), store.ReadLog);
        Assert.Contains(nameof(InMemoryRelationalStore.SessionRecordsForRebuild), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.Records), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance), store.ReadLog);

        long expectedBetaBytes = Encoding.UTF8.GetByteCount(betaFirstText) + Encoding.UTF8.GetByteCount(betaSecondText);
        Assert.Equal(expectedBetaBytes, store.MaterializedContentBytes);

        string incrementalProjection = Serialize(store.GetSessionProjection("sess-b"));
        store.ClearReadLog();
        ProjectionPassReport full = engine.ProjectIncremental();
        Assert.Contains(nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance), store.ReadLog);
        Assert.Contains("sess-b", full.RebuiltKeys);
        Assert.Equal(incrementalProjection, Serialize(store.GetSessionProjection("sess-b")));
    }

    /// <summary>
    /// Attribution-driven claiming assigns the same session ids today's sorted-key record
    /// iteration assigned: single-harness keys claim their own key, a harness-split key claims
    /// <c>{harness}:{key}</c> per harness, and a colliding id takes the prefix the claim loop
    /// always assigned.
    /// </summary>
    [Fact]
    public void AttributionDrivenClaimingAssignsTheSameSessionIdsAsRecordIteration()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine engine = NewEngine(store);

        WriteSession(store, "alpha", "claude-code", [Turn("span-alpha-1", "alpha", 0, harnessId: "claude-code", inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "codex:shared", "codex", [Turn("span-codex-shared-1", "codex:shared", 0, harnessId: "codex", inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "claude-code:shared", "claude-code", []);
        WriteSession(store, "codex:codex:shared", "codex", []);
        WriteSession(
            store,
            "shared",
            "claude-code",
            [
                Turn("span-shared-1", "shared", 0, harnessId: "claude-code", inputTokens: 100, outputTokens: 10),
                Turn("span-shared-2", "shared", 1, harnessId: "claude-code", inputTokens: 100, outputTokens: 10),
                Turn("span-shared-3", "shared", 2, harnessId: "codex", inputTokens: 100, outputTokens: 10),
            ]);

        Assert.Equal(
            new StoredSessionAttribution[]
            {
                new("alpha", "claude-code", 1),
                new("codex:shared", "codex", 1),
                new("shared", "claude-code", 2),
                new("shared", "codex", 1),
            },
            store.SessionAttributions());

        ProjectionPassReport pass = engine.ProjectIncremental();
        Assert.Equal(4, pass.Built);

        HashSet<string> projectedIds = new(
            store.ListSessionProjections().Select(static projection => projection.SessionId),
            StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "alpha",
                "codex:shared",
                "claude-code:shared",
                "codex:codex:shared",
            },
            projectedIds);
    }

    /// <summary>
    /// A read-path pass with a per-session record-count budget of two rebuilds the small dirty
    /// session, defers the oversized one into <see cref="ProjectionPassReport.DeferredKeys"/>,
    /// leaves its stored projection unchanged, keeps its key dirty in the write-back, and a full
    /// pass then rebuilds it and clears the dirty state.
    /// </summary>
    [Fact]
    public void AReadPathPassDefersOversizedDirtySessionsAndKeepsThemDirty()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine engine = new(
            store,
            store,
            store,
            new FakePricingRepository(),
            readPathSessionRecordBudget: ReadPathBudget);

        WriteSession(store, "sess-small", "claude-code", [Turn("span-small-1", "sess-small", 0, harnessId: "claude-code", inputTokens: 100, outputTokens: 10)]);
        WriteSession(
            store,
            "sess-big",
            "codex",
            [
                Turn("span-big-1", "sess-big", 0, harnessId: "codex", inputTokens: 100, outputTokens: 10),
                Turn("span-big-2", "sess-big", 1, harnessId: "codex", inputTokens: 100, outputTokens: 10),
                Turn("span-big-3", "sess-big", 2, harnessId: "codex", inputTokens: 100, outputTokens: 10),
            ]);
        store.SetWriteMarker(1);
        ProjectionPassReport full = engine.ProjectIncremental();
        Assert.Equal(2, full.Built);

        WriteSession(store, "sess-small", "claude-code", [Turn("span-small-2", "sess-small", 1, harnessId: "claude-code", inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-big", "codex", [Turn("span-big-4", "sess-big", 3, harnessId: "codex", inputTokens: 100, outputTokens: 10)]);
        store.SetWriteMarker(2);
        store.SetProjectionDirtyState(new DirtyProjectionState(FromSeq: 1, ToSeq: 2, Keys: ["sess-small", "sess-big"], IsWildcard: false));

        StoredSessionProjection? bigBefore = store.GetSessionProjection("sess-big");
        Assert.NotNull(bigBefore);

        ProjectionPassReport readPass = engine.ProjectIncrementalForReads();

        Assert.Equal(["sess-small"], readPass.RebuiltKeys);
        Assert.Equal(["sess-big"], readPass.DeferredKeys);
        Assert.Equal(Serialize(bigBefore), Serialize(store.GetSessionProjection("sess-big")));

        DirtyProjectionState? dirty = store.GetProjectionDirtyState();
        Assert.NotNull(dirty);
        Assert.Equal(["sess-big"], dirty!.Keys);
        Assert.False(dirty.IsWildcard);

        ProjectionPassReport fullPass = engine.ProjectIncremental();
        Assert.Contains("sess-big", fullPass.RebuiltKeys);
        Assert.Null(store.GetProjectionDirtyState());
        Assert.NotEqual(Serialize(bigBefore), Serialize(store.GetSessionProjection("sess-big")));
    }

    /// <summary>
    /// While a first projection pass holds the reader (blocked on a gate inside
    /// <see cref="IRelationalStoreReader.SessionAttributions"/>), a second
    /// <see cref="KyberBridgeEngine.EnsureProjected"/> returns instead of blocking, and once the
    /// first pass finishes a later pass proceeds and clears the dirty state it wrote.
    /// </summary>
    [Fact]
    public async Task ASecondEnsureProjectedReturnsWhileTheFirstPassHoldsTheReader()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();

        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-1", "sess-a", 0, harnessId: "claude-code", inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-b", "codex", [Turn("span-b-1", "sess-b", 0, harnessId: "codex", inputTokens: 100, outputTokens: 10)]);
        store.SetWriteMarker(1);
        _ = NewEngine(store).ProjectIncremental();

        WriteSession(store, "sess-b", "codex", [Turn("span-b-2", "sess-b", 1, harnessId: "codex", inputTokens: 100, outputTokens: 10)]);
        store.SetWriteMarker(2);
        store.SetProjectionDirtyState(new DirtyProjectionState(FromSeq: 1, ToSeq: 2, Keys: ["sess-b"], IsWildcard: false));

        using GateReader reader = new(store);
        CanonSessionProjectorEngine projector = new(reader, store, store, pricing);
        CanonRollupsEngine rollups = new(reader, store, pricing);
        KyberBridgeEngine bridge = new(reader, store, store, pricing, projector, rollups);

        Task firstPass = Task.Run(() => bridge.EnsureProjected());
        await AwaitForCompletion(reader.Entered);

        Task secondCall = Task.Run(() => bridge.EnsureProjected());
        try
        {
            await AwaitForCompletion(secondCall);
            Assert.False(firstPass.IsCompleted, "The first pass must still hold the reader when the second call returns.");
        }
        finally
        {
            reader.ReleaseGate();
        }

        await AwaitForCompletion(firstPass);

        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-2", "sess-a", 1, harnessId: "claude-code", inputTokens: 100, outputTokens: 10)]);
        store.SetWriteMarker(3);
        store.SetProjectionDirtyState(new DirtyProjectionState(FromSeq: 2, ToSeq: 3, Keys: ["sess-a"], IsWildcard: false));
        bridge.EnsureProjected();
        Assert.Null(store.GetProjectionDirtyState());
    }

    /// <summary>
    /// A read-path pass that defers an oversized session surfaces it in
    /// <see cref="KyberBridgeEngine.GetCoverage"/> projection health as a deferred session, so
    /// the dashboard reports projection lag without failing coverage.
    /// </summary>
    [Fact]
    public void DeferredSessionsSurfaceInProjectionHealth()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        CanonSessionProjectorEngine projector = new(
            store,
            store,
            store,
            pricing,
            readPathSessionRecordBudget: ReadPathBudget);
        KyberBridgeEngine bridge = new(store, store, store, pricing, projector, new CanonRollupsEngine(store, store, pricing));

        WriteSession(store, "sess-small", "claude-code", [Turn("span-small-1", "sess-small", 0, harnessId: "claude-code", inputTokens: 100, outputTokens: 10)]);
        WriteSession(
            store,
            "sess-big",
            "codex",
            [
                Turn("span-big-1", "sess-big", 0, harnessId: "codex", inputTokens: 100, outputTokens: 10),
                Turn("span-big-2", "sess-big", 1, harnessId: "codex", inputTokens: 100, outputTokens: 10),
                Turn("span-big-3", "sess-big", 2, harnessId: "codex", inputTokens: 100, outputTokens: 10),
            ]);
        store.SetWriteMarker(1);

        JsonElement coverage = bridge.GetCoverage();

        JsonElement projectionHealth = coverage.GetProperty("projection");
        Assert.Equal("ok", projectionHealth.GetProperty("status").GetString());
        List<string> deferredSessions =
        [
            .. projectionHealth.GetProperty("deferredSessions").EnumerateArray().Select(static value => value.GetString() ?? string.Empty),
        ];
        Assert.Equal(["sess-big"], deferredSessions);
    }

    private static CanonSessionProjectorEngine NewEngine(InMemoryRelationalStore store) =>
        new(store, store, store, new FakePricingRepository());

    /// <summary>
    /// Awaits <paramref name="task"/> through a deadlock guard rather than a blocking wait: the
    /// guard only bounds a hung test, while the completion-order assertions it enables stay
    /// deterministic and never measure elapsed time.
    /// </summary>
    /// <param name="task">Task expected to complete.</param>
    private static async Task AwaitForCompletion(Task task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(task, completed);
        await task;
    }

    private static string Serialize(StoredSessionProjection? projection) =>
        JsonSerializer.Serialize(projection);

    private static void WriteSession(
        InMemoryRelationalStore store,
        string sessionKey,
        string harnessId,
        IReadOnlyList<CollectedRecord> records)
    {
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: sessionKey,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));
    }

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        int minuteOffset,
        string harnessId = "claude-code",
        long inputTokens = 100,
        long outputTokens = 10,
        IReadOnlyList<CollectedContentPart>? parts = null)
    {
        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero).AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens:
            [
                new CollectedTokenCount(TokenClass.Input, inputTokens),
                new CollectedTokenCount(TokenClass.CacheRead, 0),
                new CollectedTokenCount(TokenClass.CacheWrite, 0),
                new CollectedTokenCount(TokenClass.Output, outputTokens),
            ],
            ReportedCost: null,
            Parts: parts ?? [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: inputTokens,
            ReportedOutputTokens: outputTokens);
    }

    private static CollectedContentPart TextPart(int position, string text) =>
        new(
            Position: position,
            Bucket: ContentBucket.UserPrompt,
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    /// <summary>
    /// Reader decorator holding the first projection pass inside
    /// <see cref="IRelationalStoreReader.SessionAttributions"/> until the test releases the gate,
    /// so a concurrent caller's behaviour is observed deterministically without timing.
    /// </summary>
    private sealed class GateReader(InMemoryRelationalStore inner) : IRelationalStoreReader, IDisposable
    {
        private readonly ManualResetEventSlim gate = new(false);
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the first gated reader call has been entered.</summary>
        public Task Entered => entered.Task;

        /// <summary>Opens the gate so the held pass can proceed.</summary>
        public void ReleaseGate() => gate.Set();

        /// <inheritdoc />
        public void Dispose() => gate.Dispose();

        /// <inheritdoc />
        public IReadOnlyList<StoredSessionAttribution> SessionAttributions()
        {
            _ = entered.TrySetResult();
            gate.Wait();
            return inner.SessionAttributions();
        }

        /// <inheritdoc />
        public IReadOnlyList<CollectedRecord> SessionRecordsForRebuild(string sessionKey) => inner.SessionRecordsForRebuild(sessionKey);

        /// <inheritdoc />
        public IReadOnlyList<CollectedRecord> AllSessionRecordsForMaintenance() => inner.AllSessionRecordsForMaintenance();

        /// <inheritdoc />
        public IReadOnlyList<string> Harnesses() => inner.Harnesses();

        /// <inheritdoc />
        public IReadOnlyList<StoredSource> Sources(string? harnessId = null) => inner.Sources(harnessId);

        /// <inheritdoc />
        public int CountRecords(string? harnessId = null) => inner.CountRecords(harnessId);

        /// <inheritdoc />
        public CollectedRecord? GetRecord(string spanId) => inner.GetRecord(spanId);

        /// <inheritdoc />
        public IReadOnlyList<CollectedRecord> Records(
            string? sessionKey = null,
            string? harnessId = null,
            string? sourceId = null) => inner.Records(sessionKey, harnessId, sourceId);

        /// <inheritdoc />
        public IReadOnlyList<StoredTokenCount> TokenCounts(string? spanId = null, string? sessionKey = null) => inner.TokenCounts(spanId, sessionKey);

        /// <inheritdoc />
        public IReadOnlyList<StoredReportedTotal> ReportedTotals(string? spanId = null, string? sessionKey = null) => inner.ReportedTotals(spanId, sessionKey);

        /// <inheritdoc />
        public IReadOnlyList<StoredReportedCost> ReportedCosts(string? spanId = null, string? sessionKey = null) => inner.ReportedCosts(spanId, sessionKey);

        /// <inheritdoc />
        public IReadOnlyList<StoredSkillInvocation> SkillsInvoked(string? spanId = null, string? sessionKey = null) => inner.SkillsInvoked(spanId, sessionKey);

        /// <inheritdoc />
        public IReadOnlyList<StoredToolCall> ToolCalls(string? spanId = null, string? sessionKey = null) => inner.ToolCalls(spanId, sessionKey);

        /// <inheritdoc />
        public IReadOnlyList<StoredContentPart> ContentParts(string? spanId = null, string? sessionKey = null) => inner.ContentParts(spanId, sessionKey);

        /// <inheritdoc />
        public IReadOnlyList<StoredToolDefinition> ToolDefinitions(string? spanId = null, string? sessionKey = null) => inner.ToolDefinitions(spanId, sessionKey);

        /// <inheritdoc />
        public IReadOnlyList<StoredAgentInvocation> AgentInvocations(string? spanId = null, string? sessionKey = null) => inner.AgentInvocations(spanId, sessionKey);

        /// <inheritdoc />
        public IReadOnlyList<StoredRecordSignals> RecordSignals(string? spanId = null, string? sessionKey = null) => inner.RecordSignals(spanId, sessionKey);

        /// <inheritdoc />
        public CollectedSession? GetSession(string sessionKey) => inner.GetSession(sessionKey);

        /// <inheritdoc />
        public IReadOnlyList<CollectedSession> Sessions(string? harnessId = null) => inner.Sessions(harnessId);

        /// <inheritdoc />
        public IReadOnlyList<StoredSessionModelUsage> SessionModelUsages(string? sessionKey = null) => inner.SessionModelUsages(sessionKey);

        /// <inheritdoc />
        public StoredFieldsView StoredFields(string sessionKey) => inner.StoredFields(sessionKey);

        /// <inheritdoc />
        public SessionAggregateView? GetSessionAggregate(string sessionKey, IModelPricingLookup? pricing = null) => inner.GetSessionAggregate(sessionKey, pricing);

        /// <inheritdoc />
        public IReadOnlyList<SessionAggregateView> ListSessionAggregates(string? harnessId = null, IModelPricingLookup? pricing = null) => inner.ListSessionAggregates(harnessId, pricing);

        /// <inheritdoc />
        public IReadOnlyList<ReadTimeTurnSummary> SessionTurnSummaries(string sessionKey, IModelPricingLookup? pricing = null) => inner.SessionTurnSummaries(sessionKey, pricing);
    }
}
