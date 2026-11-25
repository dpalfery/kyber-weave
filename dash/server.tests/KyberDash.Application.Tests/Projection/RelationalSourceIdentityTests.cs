using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Verifies that records read back from the relational store keep their source
/// (<see cref="CollectedRecord.SourceName"/>), that <see cref="CanonRecordHydration.Hydrate"/>
/// carries it into <see cref="CanonicalRecord.Source"/>, and that the source rules the projector
/// reads (the Droid plugin/file split and the file-source twin collapses) see plugin and file rows
/// again. Each mixed-source case pins its documented outcome, so a wrong source mapping fails a
/// named case.
/// </summary>
/// <remarks>
/// Every case runs against <see cref="InMemoryRelationalStore"/>, which maps the source from the
/// stored <c>source</c> row exactly as the SQLite reader does.
/// </remarks>
public class RelationalSourceIdentityTests
{
    private const string DroidPluginLocator = "kyberdash-factory-plugin";
    private const string DroidModel = "gpt-5-codex";
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Hydration copies the stored source name into the canonical record's source.</summary>
    [Fact]
    public void Hydrate_CopiesSourceNameIntoCanonicalSource()
    {
        CollectedRecord record = Record("span-1", "sess", 0, sourceName: "codeburn/droid");

        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(record);

        Assert.Equal("codeburn/droid", hydrated.Source);
    }

    /// <summary>A record built in memory with no source name hydrates with the <c>relational</c> label.</summary>
    [Fact]
    public void Hydrate_KeepsRelationalLabel_WhenSourceNameIsNull()
    {
        CollectedRecord record = Record("span-1", "sess", 0);

        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(record);

        Assert.Equal("relational", hydrated.Source);
    }

    /// <summary>
    /// The in-memory store fills the source name from the stored source row on every record read:
    /// the OTLP locator for an OTLP batch and <c>codeburn/&lt;harness&gt;</c> for a file batch.
    /// </summary>
    [Fact]
    public void InMemoryStore_FillsSourceNameFromStoredSourceRow()
    {
        InMemoryRelationalStore store = new();
        Write(store, "sess-src", "droid", SourceKind.Otlp, DroidPluginLocator, [Record("plug-1", "sess-src", 0, harnessId: "droid")]);
        Write(store, "sess-src", "droid", SourceKind.File, "/home/dev/.factory/sessions/repo/sess-src.jsonl", [Record("file-1", "sess-src", 1, harnessId: "droid")]);

        IRelationalStoreReader reader = store;
        Assert.Equal(DroidPluginLocator, reader.RecordContentFree("plug-1")!.SourceName);
        Assert.Equal("codeburn/droid", reader.RecordContentFree("file-1")!.SourceName);
        Assert.All(reader.SessionRecordsForRebuild("sess-src"), r => Assert.Equal(ExpectedSource(r), r.SourceName));
        Assert.All(reader.AllSessionRecordsForMaintenance(), r => Assert.Equal(ExpectedSource(r), r.SourceName));
        Assert.All(reader.SessionRecordsContentFree("sess-src"), r => Assert.Equal(ExpectedSource(r), r.SourceName));
        Assert.All(reader.SessionTimelineRecords("sess-src"), r => Assert.Equal(ExpectedSource(r), r.SourceName));

        static string ExpectedSource(CollectedRecord record) =>
            record.RecordId.StartsWith("plug-", StringComparison.Ordinal) ? DroidPluginLocator : "codeburn/droid";
    }

    /// <summary>
    /// A Droid session holding the plugin's turn records and the session file's rows and companion
    /// usage projects the plugin turns only; its totals are the companion's and no per-turn token
    /// figure is invented.
    /// </summary>
    [Fact]
    public void DroidPluginAndFileSession_ProjectsPluginTurnsWithCompanionTotals()
    {
        InMemoryRelationalStore store = new();
        WriteDroidPluginTurns(store, "droid-sess", 10);
        WriteDroidFileRows(store, "droid-sess", 10);

        ProjectionPassReport report = NewEngine(store).ProjectIncremental();

        Assert.Equal(1, report.Built);
        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("droid-sess")!;
        Assert.Equal(10, projection.Turns.Count);
        Assert.All(projection.Turns, t => Assert.StartsWith("plug-", t.SpanId, StringComparison.Ordinal));

        SessionSummaryFigure summary = ((IRelationalStoreReader)store).SessionSummaryFigures("droid-sess")!;
        Assert.Equal(25829L + 283904L + 1200L, summary.TotalInput);
        Assert.Equal(6811L, summary.TotalOutput);
        Assert.Equal(283904L, summary.TotalCacheRead);
        Assert.Equal(1200L, summary.TotalCacheCreation);
        Assert.Equal([DroidModel], summary.Models);

        ReadTimeTurnSummary turn = ((IRelationalStoreReader)store).TurnFigures("droid-sess", 0)!;
        Assert.StartsWith("plug-", turn.SpanId, StringComparison.Ordinal);
        Assert.Null(turn.InputTokens);
        Assert.Null(turn.OutputTokens);
        Assert.Null(turn.FreshInputTokens);
        Assert.Null(turn.CacheReadTokens);
        Assert.Null(turn.CacheCreationTokens);
    }

    /// <summary>The plugin's turn records alone project their turns with totals not measured.</summary>
    [Fact]
    public void DroidPluginOnlySession_ProjectsTurnsWithTotalsNotMeasured()
    {
        InMemoryRelationalStore store = new();
        WriteDroidPluginTurns(store, "droid-plugin-only", 10);

        ProjectionPassReport report = NewEngine(store).ProjectIncremental();

        Assert.Equal(1, report.Built);
        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("droid-plugin-only")!;
        Assert.Equal(10, projection.Turns.Count);

        SessionSummaryFigure summary = ((IRelationalStoreReader)store).SessionSummaryFigures("droid-plugin-only")!;
        Assert.Null(summary.TotalInput);
        Assert.Null(summary.TotalOutput);
    }

    /// <summary>
    /// A Claude session of OTLP records only projects exactly as before source names were read:
    /// same turns, same totals, same published cost.
    /// </summary>
    [Fact]
    public void ClaudeOtlpOnlySession_ProjectsUnchanged()
    {
        InMemoryRelationalStore store = new();
        Write(store, "claude-otlp", "claude-code", SourceKind.Otlp, "otlp:claude-code",
        [
            Record("c-1", "claude-otlp", 0, inputTokens: 700, cacheRead: 200, outputTokens: 120, model: "claude-sonnet-4-6"),
            Record("c-2", "claude-otlp", 1, inputTokens: 900, cacheRead: 300, outputTokens: 90, model: "claude-sonnet-4-6"),
            Record("c-3", "claude-otlp", 2, inputTokens: 1_100, cacheRead: 400, outputTokens: 60, model: "claude-sonnet-4-6"),
        ]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("claude-otlp")!;
        Assert.Equal(["c-1", "c-2", "c-3"], projection.Turns.Select(static t => t.SpanId));
        SessionSummaryFigure summary = ((IRelationalStoreReader)store).SessionSummaryFigures("claude-otlp")!;
        Assert.Equal(3, summary.TurnCount);
        Assert.Equal(700L + 200L + 900L + 300L + 1_100L + 400L, summary.TotalInput);
        Assert.Equal(120L + 90L + 60L, summary.TotalOutput);
        Assert.Equal(CostBasis.Published, summary.PublishedCost.Basis);
        Assert.Equal(CostStatus.Priced, summary.PublishedCost.Status);
        Assert.Equal(ClaudeOtlpOnlyPublishedCost, summary.PublishedCost.Value!.Value, precision: 9);
    }

    /// <summary>
    /// An OTLP row and a file row reporting equal counters within the twin skew collapse to one
    /// turn kept on the OTLP row, with the file row's content parts moved onto it.
    /// </summary>
    [Fact]
    public void CounterTwins_CollapseToTheOtlpTurnWithFileContent()
    {
        InMemoryRelationalStore store = new();
        Write(store, "twin", "claude-code", SourceKind.Otlp, "otlp:claude-code",
            [Record("otlp-1", "twin", 0, inputTokens: 500, cacheRead: 100, outputTokens: 40)]);
        Write(store, "twin", "claude-code", SourceKind.File, "/home/dev/.claude/projects/repo/twin.jsonl",
            [Record("synth:file-1", "twin", 0, inputTokens: 500, cacheRead: 100, outputTokens: 40, parts: [TextPart(0, "conversation history from the transcript")])]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("twin")!;
        StoredSessionTurn turn = Assert.Single(projection.Turns);
        Assert.Equal("otlp-1", turn.SpanId);
        Assert.NotNull(projection.Context);
        Assert.Contains(projection.Context!.Turns, static t => t.Buckets.Any(static b => b.Bucket == "conversation_history" && b.Tokens > 0));
    }

    /// <summary>
    /// Overlapping Cursor IDE and Cursor agent file rows that share one turn's counters collapse to
    /// the agent's complete observation.
    /// </summary>
    [Fact]
    public void CursorOverlapFileRows_CollapseToOneTurn()
    {
        InMemoryRelationalStore store = new();
        Write(store, "cursor-sess", "cursor-agent", SourceKind.File, "/home/dev/.cursor/agent/cursor-sess.jsonl",
            [Record("synth:agent-1", "cursor-sess", 0, inputTokens: 800, outputTokens: 50, harnessId: "cursor-agent")]);
        Write(store, "cursor-sess", "cursor", SourceKind.File, "/home/dev/.cursor/state.vscdb",
            [Record("synth:ide-1", "cursor-sess", 0, inputTokens: 800, outputTokens: 0, harnessId: "cursor")]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("cursor-sess")!;
        StoredSessionTurn turn = Assert.Single(projection.Turns);
        Assert.Equal("synth:agent-1", turn.SpanId);
    }

    /// <summary>An OTLP row and a file row with different counters and no join key both stay turns.</summary>
    [Fact]
    public void MixedRowsWithNoJoinKey_KeepBothTurns()
    {
        InMemoryRelationalStore store = new();
        Write(store, "no-join", "claude-code", SourceKind.Otlp, "otlp:claude-code",
            [Record("otlp-1", "no-join", 0, inputTokens: 500, cacheRead: 100, outputTokens: 40)]);
        Write(store, "no-join", "claude-code", SourceKind.File, "/home/dev/.claude/projects/repo/no-join.jsonl",
            [Record("synth:file-1", "no-join", 0, inputTokens: 650, cacheRead: 20, outputTokens: 75)]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("no-join")!;
        Assert.Equal(["otlp-1", "synth:file-1"], projection.Turns.Select(static t => t.SpanId));
    }

    /// <summary>Published cost of the Claude OTLP-only session, recorded before source names were read.</summary>
    private const double ClaudeOtlpOnlyPublishedCost = 0.01242;

    private static CanonSessionProjectorEngine NewEngine(InMemoryRelationalStore store) =>
        new(store, store, store, new FakePricingRepository(), new FakeTextTokenizer());

    private static void WriteDroidPluginTurns(InMemoryRelationalStore store, string sessionKey, int count)
    {
        List<CollectedRecord> turns = [];
        for (int i = 0; i < count; i++)
        {
            turns.Add(Record($"plug-{i:D2}", sessionKey, i, harnessId: "droid", model: DroidModel, measured: false));
        }

        Write(store, sessionKey, "droid", SourceKind.Otlp, DroidPluginLocator, turns);
    }

    /// <summary>
    /// Writes the shape <c>DroidSessionMapper</c> produces for a session file with a companion:
    /// <c>synth:</c> assistant-call records with no token counters, and the companion's
    /// <c>tokenUsage</c> as the session's reported usage at snapshot position 0.
    /// </summary>
    private static void WriteDroidFileRows(InMemoryRelationalStore store, string sessionKey, int count)
    {
        List<CollectedRecord> calls = [];
        for (int i = 0; i < count; i++)
        {
            calls.Add(Record($"synth:droid:{sessionKey}:a-{i}", sessionKey, i, harnessId: "droid", model: DroidModel, measured: false,
                parts: [TextPart(0, $"assistant reply {i.ToString(System.Globalization.CultureInfo.InvariantCulture)}")]));
        }

        Write(store, sessionKey, "droid", SourceKind.File, $"/home/dev/.factory/sessions/repo/{sessionKey}.jsonl", calls,
        [
            new CollectedModelUsage(0, DroidModel, TokenClass.Input, 25829),
            new CollectedModelUsage(0, DroidModel, TokenClass.Output, 6811),
            new CollectedModelUsage(0, DroidModel, TokenClass.CacheRead, 283904),
            new CollectedModelUsage(0, DroidModel, TokenClass.CacheWrite, 1200),
        ]);
    }

    private static void Write(
        InMemoryRelationalStore store,
        string sessionKey,
        string harnessId,
        SourceKind kind,
        string locator,
        IReadOnlyList<CollectedRecord> records,
        IReadOnlyList<CollectedModelUsage>? usage = null)
    {
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, kind, locator),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: sessionKey,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: usage ?? []),
            ],
            Records: records,
            Problems: []));
    }

    private static CollectedRecord Record(
        string spanId,
        string sessionKey,
        int minuteOffset,
        long inputTokens = 0,
        long cacheRead = 0,
        long outputTokens = 0,
        string harnessId = "claude-code",
        string? model = "claude-sonnet-4-6",
        bool measured = true,
        IReadOnlyList<CollectedContentPart>? parts = null,
        string? sourceName = null)
    {
        List<CollectedTokenCount> tokens = measured
            ?
            [
                new CollectedTokenCount(TokenClass.Input, inputTokens),
                new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
                new CollectedTokenCount(TokenClass.CacheWrite, 0),
                new CollectedTokenCount(TokenClass.Output, outputTokens),
            ]
            : [];

        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens: tokens,
            ReportedCost: null,
            Parts: parts ?? [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: measured ? inputTokens + cacheRead : null,
            ReportedOutputTokens: measured ? outputTokens : null,
            SourceName: sourceName);
    }

    private static CollectedContentPart TextPart(int position, string text) =>
        new(
            Position: position,
            Bucket: ContentBucket.ConversationHistory,
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);
}
