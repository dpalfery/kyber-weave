using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Parity tests for the records write marker (<c>store_write_marker</c>) and the dirty projection
/// range (<c>projection_dirty_range</c>, <c>projection_dirty_session</c>) that the record-changing
/// write paths move: every case runs against <see cref="SqliteRelationalStore"/> over a real
/// database and against <see cref="InMemoryRelationalStore"/>, and asserts the same marker and
/// <see cref="DirtyProjectionState"/> on both.
/// </summary>
/// <remarks>
/// Only <see cref="IRelationalStoreWriter.WriteBatch"/> carrying a session or a record and
/// <see cref="IRelationalStoreWriter.DeleteSource"/> move the marker. A write continues the stored
/// range when the range ends at the marker it found, and restarts at that marker otherwise; a
/// wildcard (no session rows under a range) absorbs every keyed write that continues it.
/// </remarks>
public class StoreWriteMarkerParityTests : IDisposable
{
    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void KeyedBatchOnAnEmptyStore_MovesTheMarkerAndMarksSessionAndRecordKeysDirty(string kind)
    {
        StoreUnderTest store = Create(kind);

        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-1", "sess-r")]));

        Assert.Equal(1L, store.Operational.GetWriteMarker());
        AssertDirty(store.Operational, 0, 1, ["sess-a", "sess-r"]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void SecondKeyedBatch_ContinuesTheRangeAndMergesKeysWithoutDuplicates(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-1", "sess-a")]));

        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-b"], records: [Record("span-2", "sess-a")]));

        Assert.Equal(2L, store.Operational.GetWriteMarker());
        AssertDirty(store.Operational, 0, 2, ["sess-a", "sess-b"]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void BatchWithANullKeyRecord_MarksTheRangeWildcard(string kind)
    {
        StoreUnderTest store = Create(kind);

        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-1", null)]));

        Assert.Equal(1L, store.Operational.GetWriteMarker());
        AssertDirty(store.Operational, 0, 1, keys: null);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void BatchWithAnEmptyKeyRecord_IsRefusedAndLeavesTheMarkerAndRangeUnchanged(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-1", "sess-a")]));

        DashValidationException exception = Assert.Throws<DashValidationException>(
            () => store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-2", string.Empty)])));

        Assert.Equal("STORE_CONSTRAINT_VIOLATED", exception.Code);
        Assert.Equal(1L, store.Operational.GetWriteMarker());
        AssertDirty(store.Operational, 0, 1, ["sess-a"]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void BatchWithOnlyProblems_LeavesTheMarkerAndRangeUnchanged(string kind)
    {
        StoreUnderTest store = Create(kind);

        store.Writer.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions: [],
            Records: [],
            Problems: [new CollectedProblem("pk-1", "warning", "W1", "message")]));

        Assert.Equal(0L, store.Operational.GetWriteMarker());
        Assert.Null(store.Operational.GetProjectionDirtyState());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void DeleteSource_MovesTheMarkerAndMarksTheRangeWildcard(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-1", "sess-a")]));

        store.Writer.DeleteSource(store.Reader.Sources().Single().SourceId);

        Assert.Equal(2L, store.Operational.GetWriteMarker());
        AssertDirty(store.Operational, 0, 2, keys: null);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void KeyedWriteAfterAWildcard_StaysWildcard(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-1", null)]));

        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-b"], records: [Record("span-2", "sess-b")]));

        Assert.Equal(2L, store.Operational.GetWriteMarker());
        AssertDirty(store.Operational, 0, 2, keys: null);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void WriteAfterAStaleRange_RestartsTheRangeAtThePreviousMarker(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-1", "sess-a")]));
        store.Operational.SetProjectionDirtyState(new DirtyProjectionState(7, 9, ["sess-stale"], IsWildcard: false));

        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-b"], records: [Record("span-2", "sess-b")]));

        Assert.Equal(2L, store.Operational.GetWriteMarker());
        AssertDirty(store.Operational, 1, 2, ["sess-b"]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void WriteAfterAStaleWildcard_RestartsAsAKeyedRange(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-1", "sess-a")]));
        store.Operational.SetProjectionDirtyState(new DirtyProjectionState(7, 9, Keys: null, IsWildcard: true));

        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-b"], records: [Record("span-2", "sess-b")]));

        Assert.Equal(2L, store.Operational.GetWriteMarker());
        AssertDirty(store.Operational, 1, 2, ["sess-b"]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void ProblemQuarantineAndProvenanceWrites_LeaveTheMarkerAndRangeUnchanged(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-1", "sess-a")]));

        store.Writer.WriteProblem(new CollectedProblem("pk-1", "warning", "W1", "message", SessionKey: "sess-a"));
        store.Operational.PutQuarantinedSpan(new StoredQuarantinedSpan("q-1", "unclaimed", ["custom.ns"], HarnessId: "claude-code"));
        store.Operational.PutRecordProvenance(
        [
            new RecordProvenanceRow("span-1", "claude-code", "src-a", null, null, string.Empty, string.Empty, "2026-11-19T08:00:00Z", string.Empty),
        ]);

        Assert.Equal(1L, store.Operational.GetWriteMarker());
        AssertDirty(store.Operational, 0, 1, ["sess-a"]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void RejectedBatch_LeavesTheMarkerAndRangeUnchanged(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-a"], records: [Record("span-1", "sess-a")]));

        DashValidationException exception = Assert.Throws<DashValidationException>(
            () => store.Writer.WriteBatch(Batch("claude-code", sessions: ["sess-b", "sess-b"], records: [Record("span-2", "sess-b")])));

        Assert.Equal("STORE_CONSTRAINT_VIOLATED", exception.Code);
        Assert.Equal(1L, store.Operational.GetWriteMarker());
        AssertDirty(store.Operational, 0, 1, ["sess-a"]);
    }

    private static void AssertDirty(IOperationalStore operational, long fromSeq, long toSeq, string[]? keys)
    {
        DirtyProjectionState? state = operational.GetProjectionDirtyState();
        Assert.NotNull(state);
        Assert.Equal(fromSeq, state.FromSeq);
        Assert.Equal(toSeq, state.ToSeq);
        if (keys is null)
        {
            Assert.True(state.IsWildcard);
            Assert.Null(state.Keys);
            return;
        }

        Assert.False(state.IsWildcard);
        Assert.NotNull(state.Keys);
        Assert.Equal(keys.Order(StringComparer.Ordinal), state.Keys.Order(StringComparer.Ordinal));
    }

    private StoreUnderTest Create(string kind)
    {
        if (string.Equals(kind, "sqlite", StringComparison.Ordinal))
        {
            SqliteRelationalStore sqlite = new(
                Path.Combine(directory.Path, $"marker-{Guid.NewGuid():N}.db"),
                new SqliteCircuitBreaker());
            return new StoreUnderTest(sqlite, sqlite, sqlite);
        }

        if (string.Equals(kind, "memory", StringComparison.Ordinal))
        {
            InMemoryRelationalStore memory = new();
            return new StoreUnderTest(memory, memory, memory);
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown store kind.");
    }

    private static CollectedBatch Batch(string harnessId, string[] sessions, CollectedRecord[] records) => new(
        Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
        Sessions: [.. sessions.Select(key => new CollectedSession(key, harnessId, key, Workspace: null, Title: null, ReportedUsage: []))],
        Records: records,
        Problems: []);

    private static CollectedRecord Record(string spanId, string? sessionKey) => new(
        RecordId: spanId,
        HarnessId: "claude-code",
        Op: RecordOp.LlmInvoke,
        Kind: RecordKind.Client,
        Name: "llm.invoke",
        StartedAt: new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero),
        Status: RecordStatus.Ok,
        SessionKey: sessionKey,
        ParentRecordId: null,
        TraceId: null,
        Model: null,
        EndedAt: null,
        ReportedDurationMs: 100,
        Tokens: [new CollectedTokenCount(TokenClass.Input, 10)],
        ReportedCost: null,
        Parts: [],
        Tool: null,
        Agent: null,
        SkillsInvoked: [],
        AgentInstanceId: null,
        ToolDefinitions: [],
        Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null));

    private sealed record StoreUnderTest(
        IRelationalStoreWriter Writer,
        IRelationalStoreReader Reader,
        IOperationalStore Operational);
}
