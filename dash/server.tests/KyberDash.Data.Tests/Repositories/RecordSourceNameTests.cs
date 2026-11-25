using KyberDash.Contracts.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Verifies that every <see cref="SqliteRelationalStore"/> read that returns records for
/// projection fills <see cref="CollectedRecord.SourceName"/> from the record's stored
/// <c>source</c> row: the OTLP locator for an OTLP batch, <c>codeburn/&lt;harness&gt;</c> for a
/// file batch. Runs over a real <c>sqlite3</c> database, because the join itself is what is proved.
/// </summary>
public class RecordSourceNameTests : IDisposable
{
    private const string SessionKey = "sess-src";
    private const string PluginLocator = "kyberdash-factory-plugin";
    private const string FileLocator = "/home/dev/.factory/sessions/repo/sess-src.jsonl";

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Every record read returns the OTLP locator for a record from an OTLP batch.</summary>
    [Fact]
    public void OtlpRecord_ReadsBackWithBatchLocator()
    {
        SqliteRelationalStore store = Create();
        Write(store, SourceKind.Otlp, PluginLocator, "plug-1", minuteOffset: 0);

        AssertEveryReadNames(store, "plug-1", PluginLocator);
    }

    /// <summary>Every record read returns <c>codeburn/&lt;harness&gt;</c> for a record from a file batch.</summary>
    [Fact]
    public void FileRecord_ReadsBackWithFileSourcePrefixAndHarness()
    {
        SqliteRelationalStore store = Create();
        Write(store, SourceKind.File, FileLocator, "synth:droid:sess-src:a-1", minuteOffset: 0);

        AssertEveryReadNames(store, "synth:droid:sess-src:a-1", "codeburn/droid");
    }

    /// <summary>Records from both kinds in one session each keep their own source.</summary>
    [Fact]
    public void MixedSession_EachRecordKeepsItsOwnSource()
    {
        SqliteRelationalStore store = Create();
        Write(store, SourceKind.Otlp, PluginLocator, "plug-1", minuteOffset: 0);
        Write(store, SourceKind.File, FileLocator, "synth:droid:sess-src:a-1", minuteOffset: 1);

        Dictionary<string, string?> bySpan = store.SessionRecordsForRebuild(SessionKey)
            .ToDictionary(static r => r.RecordId, static r => r.SourceName, StringComparer.Ordinal);

        Assert.Equal(PluginLocator, bySpan["plug-1"]);
        Assert.Equal("codeburn/droid", bySpan["synth:droid:sess-src:a-1"]);
    }

    /// <summary>A record whose source was deleted is gone from every read: the cascade is unchanged.</summary>
    [Fact]
    public void RecordOfDeletedSource_IsGone()
    {
        SqliteRelationalStore store = Create();
        Write(store, SourceKind.Otlp, PluginLocator, "plug-1", minuteOffset: 0);

        store.DeleteSource(store.Sources().Single().SourceId);

        Assert.Null(store.RecordContentFree("plug-1"));
        Assert.Empty(store.SessionRecordsForRebuild(SessionKey));
        Assert.Empty(store.AllSessionRecordsForMaintenance());
        Assert.Empty(store.SessionRecordsContentFree(SessionKey));
        Assert.Empty(store.SessionTimelineRecords(SessionKey));
    }

    /// <summary>The detector generation moves to 6 so no projection built under the old hydration survives.</summary>
    [Fact]
    public void DetectorVersion_IsSix() => Assert.Equal(6, SqliteRelationalStore.DetectorVersion);

    private static void AssertEveryReadNames(SqliteRelationalStore store, string spanId, string expected)
    {
        Assert.Equal(expected, store.RecordContentFree(spanId)!.SourceName);
        Assert.Equal(expected, Assert.Single(store.SessionRecordsForRebuild(SessionKey)).SourceName);
        Assert.Equal(expected, Assert.Single(store.AllSessionRecordsForMaintenance()).SourceName);
        Assert.Equal(expected, Assert.Single(store.SessionRecordsContentFree(SessionKey)).SourceName);
        Assert.Equal(expected, Assert.Single(store.SessionTimelineRecords(SessionKey)).SourceName);
    }

    private static void Write(SqliteRelationalStore store, SourceKind kind, string locator, string spanId, int minuteOffset) =>
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("droid", kind, locator),
            Sessions: [new CollectedSession(SessionKey, "droid", SessionKey, Workspace: null, Title: null, ReportedUsage: [])],
            Records: [Record(spanId, minuteOffset)],
            Problems: []));

    private static CollectedRecord Record(string spanId, int minuteOffset) => new(
        RecordId: spanId,
        HarnessId: "droid",
        Op: RecordOp.LlmInvoke,
        Kind: RecordKind.Client,
        Name: "llm.invoke",
        StartedAt: new DateTimeOffset(2026, 11, 14, 10, minuteOffset, 0, TimeSpan.Zero),
        Status: RecordStatus.Ok,
        SessionKey: SessionKey,
        ParentRecordId: null,
        TraceId: null,
        Model: "gpt-5-codex",
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
        Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null));

    private SqliteRelationalStore Create() =>
        new(Path.Combine(directory.Path, $"source-name-{Guid.NewGuid():N}.db"), new SqliteCircuitBreaker());
}
