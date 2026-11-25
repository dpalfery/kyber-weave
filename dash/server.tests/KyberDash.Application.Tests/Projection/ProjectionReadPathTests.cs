using System.Text;
using System.Text.Json;
using KyberDash.Application.Tests;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Read-path projection pass behaviour: an incremental pass plans from
/// <see cref="IRelationalStoreReader.SessionAttributions"/> and materializes only the rebuilt
/// session's records, attribution-driven claiming assigns the session ids record iteration
/// assigned, the read-path entry defers oversized dirty sessions and sessions past the pass
/// record budget and writes them back as still dirty, a second
/// <see cref="KyberBridgeEngine.EnsureProjected"/> returns while a first pass holds the reader
/// (single flight), and deferred sessions surface in projection health.
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
        _ = engine.ProjectIncremental();

        WriteSession(
            store,
            "sess-b",
            "codex",
            [Turn("span-b-2", "sess-b", 1, harnessId: "codex", inputTokens: 200, outputTokens: 20, parts: [TextPart(0, betaSecondText)])]);
        MarkSessionsDirty(store, "sess-b");
        store.ClearReadLog();

        ProjectionPassReport pass = engine.ProjectIncremental();

        Assert.Equal(["sess-b"], pass.RebuiltKeys);
        Assert.Contains("sess-a", pass.SkippedKeys);

        Assert.Contains(nameof(InMemoryRelationalStore.SessionAttributions), store.ReadLog);
        Assert.Contains(nameof(InMemoryRelationalStore.SessionRecordsForRebuild), store.ReadLog);
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
            new FakeTextTokenizer(),
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
        ProjectionPassReport full = engine.ProjectIncremental();
        Assert.Equal(2, full.Built);

        WriteSession(store, "sess-small", "claude-code", [Turn("span-small-2", "sess-small", 1, harnessId: "claude-code", inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-big", "codex", [Turn("span-big-4", "sess-big", 3, harnessId: "codex", inputTokens: 100, outputTokens: 10)]);
        MarkSessionsDirty(store, "sess-small", "sess-big");

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
    /// A cold read pass over many small dirty sessions rebuilds only the sessions whose records
    /// fit the pass record budget and defers the rest through the same dirty write-back the
    /// oversized-session deferral uses, and a later read pass picks the deferred sessions up.
    /// </summary>
    [Fact]
    public void AReadPathPassRebuildsUpToItsPassRecordBudgetAndALaterPassPicksUpTheRest()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine engine = new(
            store,
            store,
            store,
            new FakePricingRepository(),
            new FakeTextTokenizer(),
            readPathPassRecordBudget: 6);

        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-1", "sess-a", 0), Turn("span-a-2", "sess-a", 1)]);
        WriteSession(store, "sess-b", "claude-code", [Turn("span-b-1", "sess-b", 0), Turn("span-b-2", "sess-b", 1)]);
        WriteSession(store, "sess-c", "claude-code", [Turn("span-c-1", "sess-c", 0), Turn("span-c-2", "sess-c", 1)]);
        WriteSession(store, "sess-d", "claude-code", [Turn("span-d-1", "sess-d", 0), Turn("span-d-2", "sess-d", 1)]);

        ProjectionPassReport firstPass = engine.ProjectIncrementalForReads();

        // Three two-record sessions reach the six-record pass budget; the fourth would cross it.
        Assert.Equal(["sess-a", "sess-b", "sess-c"], firstPass.RebuiltKeys);
        Assert.Equal(["sess-d"], firstPass.DeferredKeys);

        DirtyProjectionState? dirty = store.GetProjectionDirtyState();
        Assert.NotNull(dirty);
        Assert.Equal(["sess-d"], dirty!.Keys);
        Assert.False(dirty.IsWildcard);

        ProjectionPassReport secondPass = engine.ProjectIncrementalForReads();
        Assert.Equal(["sess-d"], secondPass.RebuiltKeys);
        Assert.NotNull(secondPass.DeferredKeys);
        Assert.Empty(secondPass.DeferredKeys);
        Assert.Null(store.GetProjectionDirtyState());
    }

    /// <summary>
    /// A read pass whose pass record budget is smaller than any one session still rebuilds the
    /// first dirty session, so every pass makes progress and repeated passes converge instead of
    /// deferring the same sessions forever.
    /// </summary>
    [Fact]
    public void AReadPassAlwaysRebuildsAtLeastOneSessionUnderThePassBudget()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine engine = new(
            store,
            store,
            store,
            new FakePricingRepository(),
            new FakeTextTokenizer(),
            readPathPassRecordBudget: 1);

        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-1", "sess-a", 0), Turn("span-a-2", "sess-a", 1)]);
        WriteSession(store, "sess-b", "claude-code", [Turn("span-b-1", "sess-b", 0), Turn("span-b-2", "sess-b", 1)]);
        WriteSession(store, "sess-c", "claude-code", [Turn("span-c-1", "sess-c", 0), Turn("span-c-2", "sess-c", 1)]);

        ProjectionPassReport firstPass = engine.ProjectIncrementalForReads();
        Assert.Equal(["sess-a"], firstPass.RebuiltKeys);
        Assert.Equal(["sess-b", "sess-c"], firstPass.DeferredKeys);

        ProjectionPassReport secondPass = engine.ProjectIncrementalForReads();
        Assert.Equal(["sess-b"], secondPass.RebuiltKeys);
        Assert.Equal(["sess-c"], secondPass.DeferredKeys);

        ProjectionPassReport thirdPass = engine.ProjectIncrementalForReads();
        Assert.Equal(["sess-c"], thirdPass.RebuiltKeys);
        Assert.NotNull(thirdPass.DeferredKeys);
        Assert.Empty(thirdPass.DeferredKeys);
        Assert.Null(store.GetProjectionDirtyState());
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
        _ = NewEngine(store).ProjectIncremental();

        WriteSession(store, "sess-b", "codex", [Turn("span-b-2", "sess-b", 1, harnessId: "codex", inputTokens: 100, outputTokens: 10)]);
        MarkSessionsDirty(store, "sess-b");

        using GateReader reader = new(store);
        CanonSessionProjectorEngine projector = new(reader, store, store, pricing, new FakeTextTokenizer());
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
        MarkSessionsDirty(store, "sess-a");
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
            new FakeTextTokenizer(),
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

        JsonElement coverage = bridge.GetCoverage();

        JsonElement projectionHealth = coverage.GetProperty("projection");
        Assert.Equal("ok", projectionHealth.GetProperty("status").GetString());
        List<string> deferredSessions =
        [
            .. projectionHealth.GetProperty("deferredSessions").EnumerateArray().Select(static value => value.GetString() ?? string.Empty),
        ];
        Assert.Equal(["sess-big"], deferredSessions);
    }

    /// <summary>
    /// A projection pass that faults mid-body releases the single-flight slot: the entry
    /// throws, and the next pass entry runs and rebuilds rather than returning the refused
    /// zero report a stuck slot would produce.
    /// </summary>
    [Fact]
    public void AFaultedProjectionPassReleasesTheSingleFlightSlot()
    {
        InMemoryRelationalStore store = new();
        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-1", "sess-a", 0, harnessId: "claude-code", inputTokens: 100, outputTokens: 10)]);

        FaultingAttributionReader reader = new(store);
        CanonSessionProjectorEngine engine = new(reader, store, store, new FakePricingRepository(), new FakeTextTokenizer());

        Assert.Throws<InvalidOperationException>(() => engine.ProjectIncrementalForReads());

        ProjectionPassReport retry = engine.ProjectIncrementalForReads();
        Assert.Equal(1, retry.Built);
        Assert.Equal(["sess-a"], retry.RebuiltKeys);
    }

    /// <summary>
    /// A read-path pass that defers an oversized session keeps the keys dirty at pass start in
    /// the write-back when a span is ingested mid-pass: the rebuilt session's per-key read
    /// cannot have seen the span, so the write-back unions those keys with the deferred set and
    /// the next pass rebuilds the session with the span instead of skipping it clean.
    /// </summary>
    [Fact]
    public void AReadPathPassKeepsMidPassIngestedKeysDirtyInTheDeferredWriteBack()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine maintenanceEngine = NewEngine(store);
        CanonSessionProjectorEngine readEngine = new(
            store,
            store,
            store,
            new FakePricingRepository(),
            new FakeTextTokenizer(),
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
        _ = maintenanceEngine.ProjectIncremental();

        WriteSession(store, "sess-small", "claude-code", [Turn("span-small-2", "sess-small", 1, harnessId: "claude-code", inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-big", "codex", [Turn("span-big-4", "sess-big", 3, harnessId: "codex", inputTokens: 100, outputTokens: 10)]);
        MarkSessionsDirty(store, "sess-small", "sess-big");

        CollectedRecord midPassSpan = Turn("span-small-3", "sess-small", 2, harnessId: "claude-code", inputTokens: 100, outputTokens: 10);
        MidPassIngestReader reader = new(store, "sess-small", midPassSpan);
        CanonSessionProjectorEngine midPassEngine = new(reader, store, store, new FakePricingRepository(), new FakeTextTokenizer(), readPathSessionRecordBudget: ReadPathBudget);

        ProjectionPassReport readPass = midPassEngine.ProjectIncrementalForReads();
        Assert.Equal(["sess-small"], readPass.RebuiltKeys);
        Assert.Equal(["sess-big"], readPass.DeferredKeys);

        DirtyProjectionState? dirty = store.GetProjectionDirtyState();
        Assert.NotNull(dirty);
        Assert.Equal(["sess-big", "sess-small"], dirty!.Keys);

        ProjectionPassReport fullPass = maintenanceEngine.ProjectIncremental();
        Assert.Contains("sess-small", fullPass.RebuiltKeys);
        Assert.Contains("sess-big", fullPass.RebuiltKeys);
        Assert.Null(store.GetProjectionDirtyState());

        StoredSessionProjection? small = store.GetSessionProjection("sess-small");
        Assert.NotNull(small);
        Assert.Equal(3, small!.SpanCount);
    }

    /// <summary>
    /// A faulted batched figures read is recorded in coverage under the batch slot — not as an
    /// empty per-session failure key — while the per-session fallback keeps the session list
    /// answering, and the entry clears once a batched read succeeds again.
    /// </summary>
    [Fact]
    public void AFaultedBatchedFiguresReadSurfacesInCoverageAndClearsOnRecovery()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-1", "sess-a", 0, harnessId: "claude-code", inputTokens: 400, outputTokens: 40)]);
        _ = NewEngine(store).ProjectIncremental();

        FaultingBatchFiguresReader reader = new(store);
        KyberBridgeEngine bridge = new(
            reader,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(reader, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(reader, store, pricing));

        JsonElement firstPage = bridge.GetSessionPage().GetProperty("sessions");
        List<string> answeredSessions = [.. firstPage.EnumerateArray().Select(static node => node.GetProperty("sessionId").GetString() ?? string.Empty)];
        Assert.Equal(["sess-a"], answeredSessions);
        string degradedRow = Assert.Single(firstPage.EnumerateArray()).GetRawText();

        JsonElement faultedCoverage = bridge.GetCoverage();
        JsonElement faultedHealth = faultedCoverage.GetProperty("projection");
        Assert.NotNull(faultedHealth.GetProperty("lastBatchReadFault").GetString());
        Assert.Empty(faultedHealth.GetProperty("failedSessions").EnumerateArray());

        JsonElement recoveredPage = bridge.GetSessionPage().GetProperty("sessions");

        JsonElement recoveredCoverage = bridge.GetCoverage();
        Assert.Null(recoveredCoverage.GetProperty("projection").GetProperty("lastBatchReadFault").GetString());
        // A degraded page answers the same row a healthy one does: its per-session reads price
        // the page's own keys.
        Assert.Equal(degradedRow, Assert.Single(recoveredPage.EnumerateArray()).GetRawText());
    }

    /// <summary>
    /// On a detector-version mismatch a budget-bounded read pass that defers keeps the stale
    /// detector stamp, the next read pass continues with the deferred keys instead of restarting
    /// from the first session, and the next maintenance pass rebuilds every session, restamps the
    /// detector version, and leaves nothing dirty.
    /// </summary>
    [Fact]
    public void AReadPassThatDefersOnADetectorMismatchLeavesTheStampStaleForTheMaintenancePass()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine engine = new(
            store,
            store,
            store,
            new FakePricingRepository(),
            new FakeTextTokenizer(),
            readPathPassRecordBudget: 1);
        CanonProjectionService maintenance = NewService(store, engine);

        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-1", "sess-a", 0)]);
        WriteSession(store, "sess-b", "claude-code", [Turn("span-b-1", "sess-b", 0)]);
        WriteSession(store, "sess-c", "claude-code", [Turn("span-c-1", "sess-c", 0)]);
        Assert.Equal(3, maintenance.ProjectIncremental().Built);
        Assert.Equal(SqliteRelationalStore.DetectorVersion, store.GetStoreState()?.DetectorVersion);

        int staleDetector = SqliteRelationalStore.DetectorVersion - 1;
        store.SetStoreState(new StoreStateView(SqliteRelationalStore.SchemaVersion, staleDetector));
        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-2", "sess-a", 1)]);
        WriteSession(store, "sess-b", "claude-code", [Turn("span-b-2", "sess-b", 1)]);
        WriteSession(store, "sess-c", "claude-code", [Turn("span-c-2", "sess-c", 1)]);

        ProjectionPassReport firstRead = engine.ProjectIncrementalForReads();
        Assert.Equal(["sess-a"], firstRead.RebuiltKeys);
        Assert.Equal(["sess-b", "sess-c"], firstRead.DeferredKeys);
        Assert.Equal(staleDetector, store.GetStoreState()?.DetectorVersion);
        Assert.False(engine.IsProjectionCurrent());

        ProjectionPassReport secondRead = engine.ProjectIncrementalForReads();
        Assert.Equal(["sess-b"], secondRead.RebuiltKeys);
        Assert.Equal(["sess-c"], secondRead.DeferredKeys);
        Assert.Equal(staleDetector, store.GetStoreState()?.DetectorVersion);

        ProjectionPassReport full = maintenance.ProjectIncremental();
        Assert.Equal(3, full.Built);
        Assert.Empty(full.SkippedKeys);
        Assert.Equal(SqliteRelationalStore.DetectorVersion, store.GetStoreState()?.DetectorVersion);
        Assert.Null(store.GetProjectionDirtyState());
        Assert.True(engine.IsProjectionCurrent());

        ProjectionPassReport afterMaintenance = engine.ProjectIncrementalForReads();
        Assert.Empty(afterMaintenance.RebuiltKeys);
    }

    /// <summary>
    /// A stale detector stamp with no new records and nothing dirty is the maintenance pass's to
    /// clear: repeated read passes rebuild nothing rather than restarting from the first session
    /// on every request, and the stamp stays stale until a maintenance pass rebuilds the store.
    /// </summary>
    [Fact]
    public void ReadPassesOnAStaleDetectorStampWithNothingDirtyRebuildNothing()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine engine = new(
            store,
            store,
            store,
            new FakePricingRepository(),
            new FakeTextTokenizer(),
            readPathPassRecordBudget: 1);

        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-1", "sess-a", 0)]);
        WriteSession(store, "sess-b", "claude-code", [Turn("span-b-1", "sess-b", 0)]);
        Assert.Equal(2, NewService(store, engine).ProjectIncremental().Built);

        int staleDetector = SqliteRelationalStore.DetectorVersion - 1;
        store.SetStoreState(new StoreStateView(SqliteRelationalStore.SchemaVersion, staleDetector));

        Assert.Empty(engine.ProjectIncrementalForReads().RebuiltKeys);
        Assert.Empty(engine.ProjectIncrementalForReads().RebuiltKeys);
        Assert.Equal(staleDetector, store.GetStoreState()?.DetectorVersion);
        Assert.False(engine.IsProjectionCurrent());
    }

    /// <summary>
    /// Keys a read pass deferred keep the projection out of date for the maintenance pass even
    /// when the detector stamp is current, so the maintenance pass rebuilds them instead of
    /// short-circuiting on a watermark that matches the write marker.
    /// </summary>
    [Fact]
    public void TheMaintenancePassRebuildsKeysAReadPassDeferred()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine engine = new(
            store,
            store,
            store,
            new FakePricingRepository(),
            new FakeTextTokenizer(),
            readPathPassRecordBudget: 1);

        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-1", "sess-a", 0)]);
        WriteSession(store, "sess-b", "claude-code", [Turn("span-b-1", "sess-b", 0)]);
        store.SetStoreState(new StoreStateView(SqliteRelationalStore.SchemaVersion, SqliteRelationalStore.DetectorVersion));

        ProjectionPassReport readPass = engine.ProjectIncrementalForReads();
        Assert.Equal(["sess-b"], readPass.DeferredKeys);
        Assert.False(engine.IsProjectionCurrent());

        ProjectionPassReport maintenancePass = NewService(store, engine).ProjectIncremental();
        Assert.Equal(["sess-b"], maintenancePass.RebuiltKeys);
        Assert.Null(store.GetProjectionDirtyState());
        Assert.True(engine.IsProjectionCurrent());
    }

    /// <summary>
    /// A read pass that defers a session with no dirty range stored before it writes the deferred
    /// keys before it moves the watermark: a crash between the two write-back writes leaves the
    /// projection out of date for the maintenance pass, never current with the deferred session
    /// skipped.
    /// </summary>
    [Fact]
    public void ACrashBetweenTheDeferredWriteBackWritesNeverLeavesTheProjectionCurrent()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine engine = new(
            store,
            store,
            store,
            new FakePricingRepository(),
            new FakeTextTokenizer(),
            readPathPassRecordBudget: 1);

        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-1", "sess-a", 0)]);
        WriteSession(store, "sess-b", "claude-code", [Turn("span-b-1", "sess-b", 0)]);
        store.SetStoreState(new StoreStateView(SqliteRelationalStore.SchemaVersion, SqliteRelationalStore.DetectorVersion));
        store.ClearProjectionDirtyState();

        int writeBackCalls = 0;
        store.BeforeProjectionWriteBack = name =>
        {
            if (++writeBackCalls == 2)
            {
                throw new InvalidOperationException($"simulated crash before {name}");
            }
        };

        Assert.Throws<InvalidOperationException>(() => engine.ProjectIncrementalForReads());
        store.BeforeProjectionWriteBack = null;

        Assert.False(engine.IsProjectionCurrent());
        ProjectionPassReport maintenancePass = NewService(store, engine).ProjectIncremental();
        Assert.Contains("sess-b", maintenancePass.RebuiltKeys);
    }

    /// <summary>
    /// A dashboard read gates on the read-path check, not the maintenance check: with a stale
    /// detector stamp, a current write marker, and a current schema, <see cref="KyberBridgeEngine.EnsureProjected"/>
    /// starts no projection pass, while a write that moves the marker does start one.
    /// </summary>
    [Fact]
    public void ADashboardReadWithAStaleDetectorStampStartsNoProjectionPass()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        CanonSessionProjectorEngine engine = new(store, store, store, pricing, new FakeTextTokenizer());
        KyberBridgeEngine bridge = new(
            store,
            store,
            store,
            pricing,
            engine,
            new CanonRollupsEngine(store, store, pricing));

        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-1", "sess-a", 0)]);
        Assert.Equal(1, NewService(store, engine).ProjectIncremental().Built);

        int staleDetector = SqliteRelationalStore.DetectorVersion - 1;
        store.SetStoreState(new StoreStateView(SqliteRelationalStore.SchemaVersion, staleDetector));
        Assert.True(engine.IsProjectionCurrentForReads());
        Assert.False(engine.IsProjectionCurrent());

        store.ClearReadLog();
        bridge.EnsureProjected();
        bridge.EnsureProjected();
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.SessionAttributions), store.ReadLog);

        WriteSession(store, "sess-a", "claude-code", [Turn("span-a-2", "sess-a", 1)]);
        Assert.False(engine.IsProjectionCurrentForReads());

        store.ClearReadLog();
        bridge.EnsureProjected();
        Assert.Contains(nameof(InMemoryRelationalStore.SessionAttributions), store.ReadLog);
    }

    private static CanonProjectionService NewService(InMemoryRelationalStore store, CanonSessionProjectorEngine engine) =>
        new(store, store, store, engine, new CanonRollupsEngine(store, store, new FakePricingRepository()));

    private static CanonSessionProjectorEngine NewEngine(InMemoryRelationalStore store) =>
        new(store, store, store, new FakePricingRepository(), new FakeTextTokenizer());

    /// <summary>
    /// Arranges the dirty range the preceding writes already produced, against the store's real
    /// watermarks: the range starts at the marker the last pass stamped and ends at the current
    /// write marker, which is what <see cref="CanonSessionProjectorEngine"/> needs to take the
    /// incremental path.
    /// </summary>
    /// <param name="store">Store whose projection and write watermarks frame the range.</param>
    /// <param name="keys">Session keys the range marks dirty.</param>
    private static void MarkSessionsDirty(InMemoryRelationalStore store, params string[] keys)
    {
        ProjectionStateView? projectionState = store.GetProjectionState();
        Assert.NotNull(projectionState);
        store.SetProjectionDirtyState(new DirtyProjectionState(
            FromSeq: projectionState.RecordsMarker,
            ToSeq: store.GetWriteMarker(),
            Keys: keys,
            IsWildcard: false));
    }

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
    private sealed class GateReader(InMemoryRelationalStore innerStore) : DelegatingRelationalStoreReader(innerStore), IDisposable
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
        public override IReadOnlyList<StoredSessionAttribution> SessionAttributions()
        {
            _ = entered.TrySetResult();
            gate.Wait();
            return innerStore.SessionAttributions();
        }
    }

    /// <summary>
    /// Reader decorator whose first attribution read faults, so a projection pass entering the
    /// single-flight slot throws mid-body; later reads delegate.
    /// </summary>
    private sealed class FaultingAttributionReader(InMemoryRelationalStore innerStore) : DelegatingRelationalStoreReader(innerStore)
    {
        private bool hasFaulted;

        /// <inheritdoc />
        public override IReadOnlyList<StoredSessionAttribution> SessionAttributions()
        {
            if (!hasFaulted)
            {
                hasFaulted = true;
                throw new InvalidOperationException("attribution read exploded");
            }

            return innerStore.SessionAttributions();
        }
    }

    /// <summary>
    /// Reader decorator ingesting one span for a session while that session's records are being
    /// read, so the span lands after the per-key read but before the pass writes its final
    /// watermark: exactly the mid-pass ingestion window. The write moves the marker itself.
    /// </summary>
    private sealed class MidPassIngestReader(InMemoryRelationalStore innerStore, string sessionKey, CollectedRecord midPassRecord) : DelegatingRelationalStoreReader(innerStore)
    {
        /// <inheritdoc />
        public override IReadOnlyList<CollectedRecord> SessionRecordsForRebuild(string key)
        {
            IReadOnlyList<CollectedRecord> records = innerStore.SessionRecordsForRebuild(key);
            if (string.Equals(key, sessionKey, StringComparison.Ordinal))
            {
                WriteSession(innerStore, sessionKey, "claude-code", [midPassRecord]);
            }

            return records;
        }
    }

    /// <summary>
    /// Reader decorator whose first batched figures read faults with a transient store fault, so
    /// the session list degrades to per-session reads; later reads delegate.
    /// </summary>
    private sealed class FaultingBatchFiguresReader(InMemoryRelationalStore innerStore) : DelegatingRelationalStoreReader(innerStore)
    {
        private bool hasFaulted;

        /// <inheritdoc />
        public override IReadOnlyList<SessionListFigure> SessionListFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null)
        {
            if (!hasFaulted)
            {
                hasFaulted = true;
                throw new IOException("batched figures read exploded");
            }

            return innerStore.SessionListFigures(sessionKeys, pricing);
        }
    }
}
