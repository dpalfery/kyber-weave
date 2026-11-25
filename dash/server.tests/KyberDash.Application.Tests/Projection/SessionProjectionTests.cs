using KyberDash.Application.Tests;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Verifies the relational <see cref="CanonSessionProjectorEngine"/> session projection: it reads
/// sessions and records only through <see cref="IRelationalStoreReader"/>, counts tokens through
/// <see cref="IOperationalStore"/>, and writes typed <see cref="IProjectionStore"/> rows
/// (<c>session_context</c>/<c>session_context_unmeasurable</c>, <c>session_schema</c>/
/// <c>session_schema_unmeasurable</c>, tool-definition schema tokens, and timeline marks) with no
/// JSON payload columns anywhere.
/// </summary>
/// <remarks>
/// Every test runs against <see cref="InMemoryRelationalStore"/>, which enforces the relational
/// catalog's tables, keys, and exclusive-pair constraints, so any write outside the typed catalog
/// (a legacy JSON session row, for instance) fails rather than passing silently.
/// </remarks>
public class SessionProjectionTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A full pass over ingested relational records projects one typed session row whose context,
    /// schema, tool-definition, request, and turn children are all typed rows.
    /// </summary>
    [Fact]
    public void ProjectIncremental_ReadsRelationalRecordsAndWritesTypedProjectionRows()
    {
        InMemoryRelationalStore store = new();
        string systemPrompt = LongText(600);
        CollectedRecord first = Turn(
            "turn-1",
            sessionKey: "sess-proj",
            minuteOffset: 0,
            inputTokens: 700,
            cacheRead: 200,
            outputTokens: 120,
            parts: [TextPart(0, "system_prompt", systemPrompt), TextPart(1, "conversation_history", "Fix the login bug")],
            definitions: [new CollectedToolDefinition(0, "read_file", "fs", 120), new CollectedToolDefinition(1, "web_search", "fs", 80)],
            declaredContextWindow: 200_000);
        CollectedRecord second = Turn(
            "turn-2",
            sessionKey: "sess-proj",
            minuteOffset: 1,
            inputTokens: 900,
            cacheRead: 300,
            outputTokens: 90,
            parts: [TextPart(0, "conversation_history", "Second turn")]);
        CollectedRecord toolCall = Turn(
            "tool-1",
            sessionKey: "sess-proj",
            minuteOffset: 1,
            name: "read_file",
            op: RecordOp.ToolInvoke,
            parentSpanId: "turn-2");
        WriteSession(store, "sess-proj", [first, second, toolCall], title: "Fix the login bug");

        CanonSessionProjectorEngine engine = NewEngine(store);
        ProjectionPassReport report = engine.ProjectIncremental();

        Assert.Equal(1, report.Built);
        StoredSessionProjection? projection = ((IProjectionStore)store).GetSessionProjection("sess-proj");
        Assert.NotNull(projection);
        Assert.Equal("Fix the login bug", projection!.Label);
        Assert.False(projection.IsSubagent);
        Assert.Equal(3, projection.SpanCount);
        Assert.Equal([new StoredSessionTurn("sess-proj", 0, "turn-1"), new StoredSessionTurn("sess-proj", 1, "turn-2")], projection.Turns);
        Assert.Equal(["turn-1", "turn-2"], projection.RequestSpanIds);

        Assert.NotNull(projection.Context);
        Assert.Null(projection.ContextUnmeasurableReason);
        Assert.Equal(200_000, projection.Context!.ContextLimit);
        Assert.Equal("declared", projection.Context.ContextLimitSource);
        StoredSessionContextTurn contextTurn = Assert.Single(projection.Context.Turns, t => t.Position == 0);
        Assert.Equal(900, contextTurn.InputTokens);
        Assert.Equal(200, contextTurn.ServerDefinitions.Single(s => s.Server == "fs").Tokens);
        Assert.True(contextTurn.Buckets.Single(b => b.Bucket == "tool_definitions").Tokens >= 200);
        Assert.True(contextTurn.Pressure.HasValue && contextTurn.Pressure.Value > 0);

        Assert.NotNull(projection.Schema);
        Assert.Null(projection.SchemaUnmeasurableReason);
        Assert.Equal(1, projection.Schema!.InvocationCount);
        Assert.Equal(2, projection.Schema.TurnCount);
        StoredSchemaRankedTool top = projection.Schema.RankedTools[0];
        Assert.Equal("read_file", top.ToolName);
        Assert.True(top.Invoked);
        Assert.Equal(120, top.CostTokens);
        StoredSchemaRankedTool unused = projection.Schema.RankedTools.Single(t => t.ToolName == "web_search");
        Assert.False(unused.Invoked);
        Assert.Equal(80, unused.CostTokens);
        Assert.Equal(200, projection.Schema.Servers.Single(s => s.ServerName == "fs").Tokens);

        // Both IProjectionStore implementations materialize the timeline mark lists as null
        // when no mark rows exist, so a session with no subagent or auxiliary involvement reads
        // back with null fields and zero typed timeline rows.
        Assert.Null(projection.TimelineSubagentSpanIds);
        Assert.Null(projection.TimelineAuxiliarySpanIds);
        Assert.Equal(0, store.CountRows("session_timeline_subagent"));
        Assert.Equal(0, store.CountRows("session_timeline_auxiliary"));
    }

    /// <summary>
    /// A session with no message structure and tool invocations but no tool definitions gets
    /// exclusive <c>_unmeasurable</c> rows instead of zero-valued context or schema rows.
    /// </summary>
    [Fact]
    public void ProjectIncremental_WritesExclusiveUnmeasurableRowsWhenFiguresAreAbsent()
    {
        InMemoryRelationalStore store = new();
        CollectedRecord turn = Turn("turn-1", sessionKey: "sess-plain", minuteOffset: 0, inputTokens: 500, outputTokens: 40);
        CollectedRecord toolCall = Turn("tool-1", sessionKey: "sess-plain", minuteOffset: 1, name: "grep", op: RecordOp.ToolInvoke);
        WriteSession(store, "sess-plain", [turn, toolCall]);

        CanonSessionProjectorEngine engine = NewEngine(store);
        ProjectionPassReport report = engine.ProjectIncremental();

        Assert.Equal(1, report.Built);
        StoredSessionProjection? projection = ((IProjectionStore)store).GetSessionProjection("sess-plain");
        Assert.NotNull(projection);
        Assert.Null(projection!.Context);
        Assert.Equal("no_message_structure", projection.ContextUnmeasurableReason);
        Assert.Null(projection.Schema);
        Assert.NotNull(projection.SchemaUnmeasurableReason);

        Assert.Equal(0, store.CountRows("session_context"));
        Assert.Equal(1, store.CountRows("session_context_unmeasurable"));
        Assert.Equal(0, store.CountRows("session_schema"));
        Assert.Equal(1, store.CountRows("session_schema_unmeasurable"));
        Assert.Equal(1, store.CountRows("session_turn"));
    }

    /// <summary>
    /// Token counting during the pass persists its cache entries through
    /// <see cref="IOperationalStore"/> (<c>token_cache</c>).
    /// </summary>
    [Fact]
    public void ProjectIncremental_CountsTokensThroughTheOperationalStore()
    {
        InMemoryRelationalStore store = new();
        string prompt = LongText(700);
        CollectedRecord turn = Turn(
            "turn-1",
            sessionKey: "sess-cache",
            minuteOffset: 0,
            inputTokens: 100,
            outputTokens: 20,
            parts: [TextPart(0, "system_prompt", prompt)]);
        WriteSession(store, "sess-cache", [turn]);

        CanonSessionProjectorEngine engine = NewEngine(store);
        _ = engine.ProjectIncremental();

        long expected = CanonTokenCache.ApproximateO200kBase(prompt);
        Assert.Equal(expected, store.GetCachedTokenCount(CanonTokenCache.CacheKey(prompt, CanonTokenCache.ApproximateTokenizerName)));
        Assert.True(store.CountRows("token_cache") > 0, "the pass must persist token cache rows through IOperationalStore");
    }

    /// <summary>
    /// Timeline subagent and auxiliary span classifications land as timeline mark rows on the
    /// projected session.
    /// </summary>
    [Fact]
    public void ProjectIncremental_WritesTimelineSubagentAndAuxiliaryMarks()
    {
        InMemoryRelationalStore store = new();
        CollectedRecord rootTurn = Turn("turn-1", sessionKey: "sess-timeline", minuteOffset: 0, inputTokens: 400, outputTokens: 60);
        CollectedRecord subagent = Turn(
            "agent-1",
            sessionKey: "sess-timeline",
            minuteOffset: 0,
            name: "invoke_agent subagent-research",
            op: RecordOp.AgentInvoke,
            parentSpanId: "turn-1",
            agent: new CollectedAgentInvocation("research-subagent", SpawningCallId: null, Succeeded: true, Error: null));
        CollectedRecord subagentTurn = Turn(
            "turn-child",
            sessionKey: "sess-timeline",
            minuteOffset: 1,
            inputTokens: 200,
            outputTokens: 30,
            parentSpanId: "agent-1");
        CollectedRecord auxiliary = Turn(
            "aux-1",
            sessionKey: "sess-timeline",
            minuteOffset: 2,
            name: "title generation",
            op: RecordOp.Unspecified);
        WriteSession(store, "sess-timeline", [rootTurn, subagent, subagentTurn, auxiliary]);

        CanonSessionProjectorEngine engine = NewEngine(store);
        ProjectionPassReport report = engine.ProjectIncremental();

        Assert.Equal(1, report.Built);
        StoredSessionProjection? projection = ((IProjectionStore)store).GetSessionProjection("sess-timeline");
        Assert.NotNull(projection);
        Assert.Equal(["agent-1"], projection!.TimelineSubagentSpanIds);
        Assert.Equal(["aux-1"], projection.TimelineAuxiliarySpanIds);
        Assert.Equal(1, store.CountRows("session_timeline_subagent"));
        Assert.Equal(1, store.CountRows("session_timeline_auxiliary"));
    }

    /// <summary>
    /// After a pass the projection is current: the service short-circuits without rebuilding, and
    /// the stored watermark tracks the write marker so later writes invalidate the projection.
    /// </summary>
    [Fact]
    public void ProjectIncremental_ShortCircuitsWhileCurrentAndInvalidatesOnWriteMarkerAdvance()
    {
        InMemoryRelationalStore store = new();
        CollectedRecord turn = Turn("turn-1", sessionKey: "sess-current", minuteOffset: 0, inputTokens: 300, outputTokens: 30);
        WriteSession(store, "sess-current", [turn]);

        CanonSessionProjectorEngine engine = NewEngine(store);
        CanonProjectionService service = new CanonProjectionService(
            store,
            store,
            store,
            engine,
            new CanonRollupsEngine(store, store, new FakePricingRepository()));

        ProjectionPassReport first = service.ProjectIncremental();
        Assert.Equal(1, first.Built);
        Assert.True(engine.IsProjectionCurrent());

        ProjectionPassReport second = service.ProjectIncremental();
        Assert.Equal(0, second.Built);
        Assert.Equal(1, second.Skipped);

        WriteSession(store, "sess-current", [Turn("turn-2", sessionKey: "sess-current", minuteOffset: 1, inputTokens: 300, outputTokens: 30)]);
        Assert.False(engine.IsProjectionCurrent());
        ProjectionPassReport third = service.ProjectIncremental();
        Assert.Equal(1, third.Built);
    }

    /// <summary>
    /// A later pass prunes typed projections whose records disappeared from the relational store.
    /// </summary>
    [Fact]
    public void ProjectIncremental_PrunesProjectionsForRemovedRecords()
    {
        InMemoryRelationalStore store = new();
        CollectedRecord turnA = Turn("turn-a", sessionKey: "sess-keep", minuteOffset: 0, inputTokens: 300, outputTokens: 30, harnessId: "claude-code");
        CollectedRecord turnB = Turn("turn-b", sessionKey: "sess-gone", minuteOffset: 0, inputTokens: 500, outputTokens: 50, harnessId: "codex");
        WriteSession(store, "sess-keep", [turnA], harnessId: "claude-code");
        WriteSession(store, "sess-gone", [turnB], harnessId: "codex");

        CanonSessionProjectorEngine engine = NewEngine(store);
        ProjectionPassReport first = engine.ProjectIncremental();
        Assert.Equal(2, first.Built);
        Assert.NotNull(((IProjectionStore)store).GetSessionProjection("sess-gone"));

        StoredSource codexSource = store.Sources("codex").Single();
        ((IRelationalStoreWriter)store).DeleteSource(codexSource.SourceId);

        ProjectionPassReport second = engine.ProjectIncremental();
        Assert.Equal(1, second.Built);
        Assert.Equal(1, second.Pruned);
        Assert.NotNull(((IProjectionStore)store).GetSessionProjection("sess-keep"));
        Assert.Null(((IProjectionStore)store).GetSessionProjection("sess-gone"));
    }

    private static CanonSessionProjectorEngine NewEngine(InMemoryRelationalStore store) =>
        new(store, store, store, new FakePricingRepository());

    private static void WriteSession(
        InMemoryRelationalStore store,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records,
        string harnessId = "claude-code",
        string? title = null)
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
                    Title: title,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));
    }

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        int minuteOffset,
        long inputTokens = 0,
        long cacheRead = 0,
        long outputTokens = 0,
        string? parentSpanId = null,
        string name = "chat.turn",
        RecordOp op = RecordOp.LlmInvoke,
        RecordStatus status = RecordStatus.Ok,
        string? model = "claude-sonnet-4-6",
        IReadOnlyList<CollectedContentPart>? parts = null,
        IReadOnlyList<CollectedToolDefinition>? definitions = null,
        long? declaredContextWindow = null,
        CollectedAgentInvocation? agent = null,
        string harnessId = "claude-code")
    {
        List<CollectedTokenCount> tokens =
        [
            new CollectedTokenCount(TokenClass.Input, inputTokens),
            new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
            new CollectedTokenCount(TokenClass.CacheWrite, 0),
            new CollectedTokenCount(TokenClass.Output, outputTokens),
        ];

        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: op,
            Kind: RecordKind.Client,
            Name: name,
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: status,
            SessionKey: sessionKey,
            ParentRecordId: parentSpanId,
            TraceId: null,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens: tokens,
            ReportedCost: null,
            Parts: parts ?? [],
            Tool: null,
            Agent: agent,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: definitions ?? [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: declaredContextWindow),
            ReportedInputTokens: inputTokens + cacheRead,
            ReportedOutputTokens: outputTokens);
    }

    private static CollectedContentPart TextPart(int position, string bucket, string text) =>
        new(
            Position: position,
            Bucket: BucketOf(bucket),
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static ContentBucket BucketOf(string bucket) => bucket switch
    {
        "system_prompt" => ContentBucket.SystemPrompt,
        "conversation_history" => ContentBucket.ConversationHistory,
        "user_prompt" => ContentBucket.UserPrompt,
        "tool_result_content" => ContentBucket.ToolResultContent,
        _ => throw new ArgumentException($"Unexpected bucket '{bucket}'.", nameof(bucket)),
    };

    private static string LongText(int length) => new('x', length);
}
