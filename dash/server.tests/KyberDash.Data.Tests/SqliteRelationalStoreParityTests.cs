using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using Xunit;

namespace KyberDash.Data.Tests;

/// <summary>
/// Store-parity tests for <see cref="SqliteRelationalStore"/> over a real <c>sqlite3</c> database:
/// nested directory creation, current schema version stamping, idempotent initialization, newer-store refusal
/// without mutating or deleting any database file, the unconsumed rebuild re-ingest flag, and the
/// source-delete cascade the clean path relies on. Scripted <c>ExecuteSqliteCore</c> coverage lives
/// in <c>SqliteRelationalStoreWriteTests</c> and <c>SchemaVersionTests</c>.
/// </summary>
public sealed class SqliteRelationalStoreParityTests : IDisposable
{
    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Constructor_CreatesNestedDirectoryAndStampsTheCurrentSchemaVersion()
    {
        string databasePath = Path.Combine(directory.Path, "nested", "deeper", "canon.db");

        SqliteRelationalStore store = new(databasePath, new SqliteCircuitBreaker());

        Assert.Equal(Path.GetFullPath(databasePath), store.DatabasePath);
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "nested", "deeper")));
        Assert.Equal(
            new StoreStateView(SqliteRelationalStore.SchemaVersion, SqliteRelationalStore.DetectorVersion),
            store.GetStoreState());
        Assert.Equal(0L, store.GetWriteMarker());
        Assert.Null(store.GetLastCleanAtUtc());
    }

    [Fact]
    public void Constructor_AcceptsDashOptions()
    {
        string databasePath = Path.Combine(directory.Path, "options.db");

        SqliteRelationalStore store = new(new DashOptions { DatabasePath = databasePath }, new SqliteCircuitBreaker());

        Assert.Equal(Path.GetFullPath(databasePath), store.DatabasePath);
        Assert.Equal(SqliteRelationalStore.SchemaVersion, store.GetStoreState()?.SchemaVersion);
    }

    [Fact]
    public void EnsureInitialized_IsIdempotentOnTheRealStore()
    {
        SqliteRelationalStore store = Create();

        store.EnsureInitialized();
        store.EnsureInitialized();

        Assert.Equal(
            new StoreStateView(SqliteRelationalStore.SchemaVersion, SqliteRelationalStore.DetectorVersion),
            store.GetStoreState());
    }

    [Fact]
    public void Constructor_RefusesDatabaseWrittenByNewerSchema()
    {
        string databasePath = Path.Combine(directory.Path, "canon.db");
        SqliteRelationalStore store = new(databasePath, new SqliteCircuitBreaker());
        store.SetStoreState(new StoreStateView(SqliteRelationalStore.SchemaVersion + 1, SqliteRelationalStore.DetectorVersion));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => _ = new SqliteRelationalStore(databasePath, new SqliteCircuitBreaker()));

        Assert.Equal(
            SqliteRelationalStore.FormatSchemaVersionRefusal(Path.GetFullPath(databasePath), SqliteRelationalStore.SchemaVersion + 1),
            exception.Message);

        // The refusal happens before any PRAGMA, DDL, or deletion: the newer store keeps every file.
        Assert.True(File.Exists(databasePath));
        Assert.Equal(SqliteRelationalStore.SchemaVersion + 1, store.GetStoreState()?.SchemaVersion);
    }

    [Fact]
    public void PendingRebuildReingestFlag_StartsUnconsumed()
    {
        SqliteRelationalStore store = Create();

        Assert.False(store.HasPendingRebuildReingest);
        Assert.False(store.TryTakePendingRebuildReingest());
        Assert.False(store.HasPendingRebuildReingest);
        Assert.Null(store.LastRebuildInfo);
        Assert.False(store.RebuiltFromOlderSchema);
    }

    [Fact]
    public void DeleteSource_CascadesRecordsButKeepsSessionIdentityRows()
    {
        SqliteRelationalStore store = Create();
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions: [new CollectedSession("sess-a", "claude-code", "sess-a", Workspace: null, Title: null, ReportedUsage: [])],
            Records: [Record("span-a-1", "sess-a")],
            Problems: []));
        store.ReplaceSessionProjection(new StoredSessionProjection(
            SessionId: "sess-a",
            Label: "claude-code session",
            IsSubagent: false,
            SpanCount: 1,
            StartedAtUtc: "2026-11-14T10:00:00.000Z",
            EndedAtUtc: "2026-11-14T10:05:00.000Z",
            ParentSessionId: null,
            AgentName: null,
            IncludedInParent: false,
            RequestSpanIds: [],
            Turns: []));
        store.ReplaceSessionTurns("sess-a", [new StoredSessionTurn("sess-a", 0, "span-a-1")]);

        store.DeleteSource(store.Sources().Single(source => source.Source.HarnessId == "claude-code").SourceId);

        // Records are source-owned and cascade away; the session identity row is harness-owned and
        // stays, and the projection keyed to it stays with it. The clean path deletes projections
        // itself and reports zero sessions deleted for exactly this reason.
        Assert.Equal(0, store.CountRecords());
        Assert.Single(store.Sessions());
        Assert.NotNull(store.GetSessionProjection("sess-a"));
        Assert.Single(store.GetSessionTurns("sess-a"));
    }

    [Fact]
    public void ClearEstimatedRecordTokens_DeletesOnlyTheNamedEstimatesOfTheFileSource()
    {
        SqliteRelationalStore store = Create();
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("cursor", SourceKind.File, "/home/dev/.cursor/a.jsonl"),
            Sessions: [new CollectedSession("sess-c", "cursor", "sess-c", Workspace: null, Title: null, ReportedUsage: [])],
            Records:
            [
                Record("span-native", "sess-c", "cursor") with { ReportedInputTokens = 10 },
                Record("span-estimated", "sess-c", "cursor"),
            ],
            Problems: []));
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("warp", SourceKind.File, "/home/dev/warp.sqlite"),
            Sessions: [new CollectedSession("sess-w", "warp", "sess-w", Workspace: null, Title: null, ReportedUsage: [new CollectedModelUsage(0, "auto", TokenClass.Total, 40)])],
            Records: [Record("span-warp", "sess-w", "warp") with { ReportedInputTokens = 10 }],
            Problems: []));
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("warp", SourceKind.File, "/home/dev/other.sqlite"),
            Sessions: [],
            Records: [Record("span-warp-other", "sess-w", "warp") with { ReportedInputTokens = 10 }],
            Problems: []));

        store.ClearEstimatedRecordTokens("cursor", "/home/dev/.cursor/a.jsonl", EstimatedRecordTokens.RecordsWithoutReportedTotal);
        store.ClearEstimatedRecordTokens("warp", "/home/dev/warp.sqlite", EstimatedRecordTokens.AllRecordTokens);

        Assert.Single(store.TokenCounts("span-native"));
        Assert.Single(store.ReportedTotals("span-native"));
        Assert.Empty(store.TokenCounts("span-estimated"));
        Assert.Empty(store.TokenCounts("span-warp"));
        Assert.Empty(store.ReportedTotals("span-warp"));
        Assert.Single(store.TokenCounts("span-warp-other"));
        Assert.Single(store.ReportedTotals("span-warp-other"));
        Assert.Single(store.SessionModelUsages("sess-w"));
        Assert.Equal(4, store.CountRecords());
        Assert.Equal(2, store.Sessions().Count);
    }

    private SqliteRelationalStore Create() =>
        new(Path.Combine(directory.Path, $"parity-{Guid.NewGuid():N}.db"), new SqliteCircuitBreaker());

    private static CollectedRecord Record(string spanId, string sessionKey, string harnessId = "claude-code") => new(
        RecordId: spanId,
        HarnessId: harnessId,
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
}
