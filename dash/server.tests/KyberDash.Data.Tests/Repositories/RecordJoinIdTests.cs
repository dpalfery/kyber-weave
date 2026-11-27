using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Verifies that a record's response id and request id (<c>record_response_id</c>,
/// <c>record_request_id</c>) are written and read back by every SQLite record read, so the
/// projector's id join between an OTLP turn and its transcript twin sees them.
/// </summary>
public sealed class RecordJoinIdTests : IDisposable
{
    private const string SessionKey = "sess-ids";

    private readonly TempDirectory directory = new();

    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Both ids round-trip through every record read.</summary>
    [Fact]
    public void Ids_ReadBackFromEveryRecordRead()
    {
        SqliteRelationalStore store = Create();
        Write(store, Record("otlp-1", responseId: "msg_01ABC", requestId: "req_01XYZ"));

        AssertEveryRead(store, "otlp-1", "msg_01ABC", "req_01XYZ");
    }

    /// <summary>A record with no ids reads back with both ids absent.</summary>
    [Fact]
    public void AbsentIds_ReadBackAsNull()
    {
        SqliteRelationalStore store = Create();
        Write(store, Record("otlp-2", responseId: null, requestId: null));

        AssertEveryRead(store, "otlp-2", null, null);
    }

    private static void AssertEveryRead(SqliteRelationalStore store, string spanId, string? responseId, string? requestId)
    {
        List<CollectedRecord> reads =
        [
            store.RecordContentFree(spanId)!,
            Assert.Single(store.SessionRecordsForRebuild(SessionKey)),
            Assert.Single(store.AllSessionRecordsForMaintenance()),
            Assert.Single(store.SessionRecordsContentFree(SessionKey)),
            Assert.Single(store.SessionTimelineRecords(SessionKey)),
        ];

        Assert.All(reads, r =>
        {
            Assert.Equal(responseId, r.ResponseId);
            Assert.Equal(requestId, r.RequestId);
        });
    }

    private static void Write(SqliteRelationalStore store, CollectedRecord record) =>
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions: [new CollectedSession(SessionKey, "claude-code", SessionKey, Workspace: null, Title: null, ReportedUsage: [])],
            Records: [record],
            Problems: []));

    private static CollectedRecord Record(string spanId, string? responseId, string? requestId) => new(
        RecordId: spanId,
        HarnessId: "claude-code",
        Op: RecordOp.LlmInvoke,
        Kind: RecordKind.Client,
        Name: "llm.invoke",
        StartedAt: new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero),
        Status: RecordStatus.Ok,
        SessionKey: SessionKey,
        ParentRecordId: null,
        TraceId: null,
        Model: "claude-sonnet-4-6",
        EndedAt: null,
        ReportedDurationMs: 100,
        Tokens: [],
        ReportedCost: null,
        Parts: [],
        Tool: null,
        Agent: null,
        SkillsInvoked: [],
        AgentInstanceId: null,
        ToolDefinitions: [],
        Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
        ResponseId: responseId,
        RequestId: requestId);

    private SqliteRelationalStore Create() =>
        new(Path.Combine(directory.Path, $"join-ids-{Guid.NewGuid():N}.db"), new SqliteCircuitBreaker());
}
