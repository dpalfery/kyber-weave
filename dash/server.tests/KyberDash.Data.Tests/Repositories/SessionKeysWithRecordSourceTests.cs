using KyberDash.Contracts.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Verifies <see cref="IRelationalStoreReader.SessionKeysWithRecordSource"/> over a real
/// <c>sqlite3</c> database: only sessions of the named harness holding a record from the named
/// source, newest record timestamp first then session key, distinct, capped by the limit, and a
/// limit below one refused.
/// </summary>
public class SessionKeysWithRecordSourceTests : IDisposable
{
    private const string PluginLocator = "kyberdash-factory-plugin";
    private const string FileLocator = "/home/dev/.factory/sessions/repo/sess-file.jsonl";

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Only sessions whose records come from the named harness and source are returned.</summary>
    [Fact]
    public void ReturnsOnlySessionsOfTheNamedHarnessWithTheNamedSource()
    {
        SqliteRelationalStore store = Create();
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-plug", "plug-1", minuteOffset: 0);
        Write(store, "droid", SourceKind.Otlp, "other-plugin", "sess-other-source", "other-1", minuteOffset: 1);
        Write(store, "droid", SourceKind.File, FileLocator, "sess-file", "synth:droid:sess-file:a-1", minuteOffset: 2);
        Write(store, "claude-code", SourceKind.Otlp, PluginLocator, "sess-other-harness", "claude-1", minuteOffset: 3);

        Assert.Equal(["sess-plug"], store.SessionKeysWithRecordSource("droid", PluginLocator, limit: 10));
    }

    /// <summary>The newest record timestamp orders first; an exact tie breaks by session key ordinal.</summary>
    [Fact]
    public void OrdersNewestRecordFirstThenSessionKey()
    {
        SqliteRelationalStore store = Create();
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-older", "older-1", minuteOffset: 0);
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-b-tie", "tie-b-1", minuteOffset: 10);
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-a-tie", "tie-a-1", minuteOffset: 10);
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-newest", "newest-1", minuteOffset: 20);

        Assert.Equal(
            ["sess-newest", "sess-a-tie", "sess-b-tie", "sess-older"],
            store.SessionKeysWithRecordSource("droid", PluginLocator, limit: 10));
    }

    /// <summary>A session holding several records from the source appears once.</summary>
    [Fact]
    public void ReturnsDistinctSessionKeys()
    {
        SqliteRelationalStore store = Create();
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-multi", "multi-1", minuteOffset: 0);
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-multi", "multi-2", minuteOffset: 1);
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-multi", "multi-3", minuteOffset: 2);

        Assert.Equal(["sess-multi"], store.SessionKeysWithRecordSource("droid", PluginLocator, limit: 10));
    }

    /// <summary>The limit caps the rows, keeping the newest sessions.</summary>
    [Fact]
    public void LimitCapsTheRowsKeepingTheNewest()
    {
        SqliteRelationalStore store = Create();
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-old", "old-1", minuteOffset: 0);
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-mid", "mid-1", minuteOffset: 5);
        Write(store, "droid", SourceKind.Otlp, PluginLocator, "sess-new", "new-1", minuteOffset: 10);

        Assert.Equal(
            ["sess-new", "sess-mid"],
            store.SessionKeysWithRecordSource("droid", PluginLocator, limit: 2));
    }

    /// <summary>A limit below 1 is refused.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void LimitBelowOneIsRefused(int limit)
    {
        SqliteRelationalStore store = Create();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.SessionKeysWithRecordSource("droid", PluginLocator, limit));
    }

    private SqliteRelationalStore Create() =>
        new(Path.Combine(directory.Path, $"session-keys-{Guid.NewGuid():N}.db"), new SqliteCircuitBreaker());

    private static void Write(
        SqliteRelationalStore store,
        string harnessId,
        SourceKind kind,
        string locator,
        string sessionKey,
        string spanId,
        int minuteOffset) =>
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, kind, locator),
            Sessions: [new CollectedSession(sessionKey, harnessId, sessionKey, Workspace: null, Title: null, ReportedUsage: [])],
            Records: [Record(harnessId, sessionKey, spanId, minuteOffset)],
            Problems: []));

    private static CollectedRecord Record(string harnessId, string sessionKey, string spanId, int minuteOffset) => new(
        RecordId: spanId,
        HarnessId: harnessId,
        Op: RecordOp.LlmInvoke,
        Kind: RecordKind.Client,
        Name: "llm.invoke",
        StartedAt: new DateTimeOffset(2026, 11, 14, 10, minuteOffset, 0, TimeSpan.Zero),
        Status: RecordStatus.Ok,
        SessionKey: sessionKey,
        ParentRecordId: null,
        TraceId: null,
        Model: null,
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
}
