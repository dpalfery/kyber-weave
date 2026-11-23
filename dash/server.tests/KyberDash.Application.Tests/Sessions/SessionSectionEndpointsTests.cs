using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Sessions;

/// <summary>
/// The section endpoints answer their slice of the session view from purpose-named content-free
/// reads: <see cref="KyberBridgeEngine.GetSessionSummary"/> carries every field the summary
/// section renders with parity to the detail payload it widens, the charts section equals the
/// detail payload's turn list and context object without paying for any other section's reads,
/// the turn section carries that turn's stored context buckets, and the run page's turn list
/// comes from the same per-session turn figures.
/// </summary>
public sealed class SessionSectionEndpointsTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Reader methods that materialize conversation text, so a section may not issue them.</summary>
    private static readonly string[] ContentBearingReads =
    [
        nameof(InMemoryRelationalStore.SessionRecordsForRebuild),
        nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance),
        nameof(InMemoryRelationalStore.ToolCalls),
        nameof(InMemoryRelationalStore.ToolDefinitions),
        nameof(InMemoryRelationalStore.AgentInvocations),
        nameof(InMemoryRelationalStore.SkillsInvoked),
        nameof(InMemoryRelationalStore.TurnContentParts),
    ];

    /// <summary>
    /// The widened summary section carries the summary, notes, cost-basis mismatch, span count,
    /// reconciliation totals, and unused-tool count with parity to the detail payload it replaced,
    /// from the content-free summary figures alone.
    /// </summary>
    [Fact]
    public void TheSummarySectionCarriesTheContractFields()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-sections";
        WriteSectionSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        JsonElement detail = bridge.GetSessionDetail(sessionKey)!.Value;
        store.ClearReadLog();
        JsonElement summary = bridge.GetSessionSummary(sessionKey)!.Value;

        Assert.Equal(detail.GetProperty("summary").GetRawText(), summary.GetProperty("summary").GetRawText());
        Assert.Equal(detail.GetProperty("notes").GetRawText(), summary.GetProperty("notes").GetRawText());
        Assert.Equal(
            detail.GetProperty("costBasisMismatch").GetRawText(),
            summary.GetProperty("costBasisMismatch").GetRawText());
        Assert.Equal(detail.GetProperty("span_count").GetInt32(), summary.GetProperty("span_count").GetInt32());

        // The reconciliation totals count the same rows the detail payload lists.
        int total = 0;
        int mismatched = 0;
        foreach (JsonElement row in detail.GetProperty("reconciliation").EnumerateArray())
        {
            total++;
            if (!row.GetProperty("input_match").GetBoolean() || !row.GetProperty("output_match").GetBoolean())
            {
                mismatched++;
            }
        }

        Assert.Equal(128, total);
        Assert.Equal(1, mismatched);
        Assert.Equal(total, summary.GetProperty("reconciliation").GetProperty("total").GetInt32());
        Assert.Equal(mismatched, summary.GetProperty("reconciliation").GetProperty("mismatched").GetInt32());

        // The unused-tool count is offered minus invoked by name, computed on the server.
        HashSet<string> invoked =
        [
            .. detail.GetProperty("summary").GetProperty("tools_invoked").EnumerateArray()
                .Select(static name => name.GetString() ?? string.Empty),
        ];
        int unusedFromDetail = detail.GetProperty("summary").GetProperty("tools_offered").EnumerateArray()
            .Count(name => !invoked.Contains(name.GetString() ?? string.Empty));
        Assert.Equal(2, summary.GetProperty("tools_offered_count").GetInt32());
        Assert.Equal(1, summary.GetProperty("tools_invoked_count").GetInt32());
        Assert.Equal(1, unusedFromDetail);
        Assert.Equal(unusedFromDetail, summary.GetProperty("unused_tools_count").GetInt32());
        Assert.Equal(1, summary.GetProperty("skills_count").GetInt32());
        Assert.Equal(0, summary.GetProperty("problems_count").GetInt32());

        // The section answers from the content-free summary read alone.
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));
        Assert.Contains(nameof(InMemoryRelationalStore.SessionSummaryFigures), store.ReadLog);
        Assert.Contains(nameof(InMemoryRelationalStore.SessionHeadersByKeys), store.SessionProjectionReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.GetSessionTurns), store.ReadLog);
    }

    /// <summary>
    /// The charts section equals the detail payload's turn list and context object on a 128-turn
    /// session — including the first and last context edges, which price the records' reported
    /// input rather than the stored projection's measured input — and issues no tool, skill,
    /// request, timeline, or content read.
    /// </summary>
    [Fact]
    public void TheChartsSectionEqualsTheDetailTurnsAndContext()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-sections";
        WriteSectionSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        JsonElement detail = bridge.GetSessionDetail(sessionKey)!.Value;
        store.ClearReadLog();
        JsonElement charts = bridge.GetSessionCharts(sessionKey)!.Value;

        Assert.Equal(sessionKey, charts.GetProperty("sessionId").GetString());
        Assert.Equal(detail.GetProperty("turns").GetRawText(), charts.GetProperty("turns").GetRawText());
        Assert.Equal(detail.GetProperty("context").GetRawText(), charts.GetProperty("context").GetRawText());

        // The charts read is the turn figures pass alone; the projection reads are the 404 header
        // check and the stored context projection. The filtered names are EnsureProjected's
        // session-agnostic steady-state guards, which every route pays before its section reads.
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionTurnFigures)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
        Assert.Equal(
            (IReadOnlyList<string>)
            [
                nameof(InMemoryRelationalStore.SessionHeadersByKeys),
                nameof(InMemoryRelationalStore.GetSessionProjection),
            ],
            store.SessionProjectionReadLog
                .Where(static read => read is not ("ListSessionProjections" or "ListHarnessRollups"))
                .ToList());
    }

    /// <summary>
    /// The widened turn section carries that turn's stored context buckets — the same per-turn
    /// entry the charts context object lists — beside today's turn figures.
    /// </summary>
    [Fact]
    public void TheTurnSectionCarriesThatTurnsContextBuckets()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-sections";
        WriteSectionSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        JsonElement detail = bridge.GetSessionDetail(sessionKey)!.Value;
        store.ClearReadLog();
        JsonElement figures = bridge.GetTurnFigures(sessionKey, 1)!.Value;

        Assert.Equal("turn-sec-001", figures.GetProperty("turn").GetProperty("spanId").GetString());
        Assert.Equal(
            detail.GetProperty("context").GetProperty("turns")[1].GetRawText(),
            figures.GetProperty("context").GetRawText());
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.TurnFigures)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
        Assert.Null(bridge.GetTurnFigures(sessionKey, 128));
    }

    /// <summary>
    /// The run page's turn list answers the same turns the per-session turn figures read carries,
    /// with the execution and session attribution the run view adds.
    /// </summary>
    [Fact]
    public void RunTurnsAnswerFromSessionTurnFigures()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        List<CollectedRecord> runTurns =
        [
            Turn("span-run-a1", "sess-run-a", 0, inputTokens: 400, cacheRead: 100, outputTokens: 60),
        ];
        runTurns.Add(Turn("span-run-a2", "sess-run-a", 1, inputTokens: 1_200, outputTokens: 80));
        WriteSession(store, "sess-run-a", runTurns, repo: "kyber-weave", branch: "kw-task/T4");
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();
        IModelPricingLookup lookup = pricing.LoadPricingTables();

        store.ClearReadLog();
        JsonElement run = bridge.GetRunById("derived:claude-code:sess-run-a")!.Value;

        SessionTurnFigureSet figures = store.SessionTurnFigures("sess-run-a", lookup)!;
        Assert.Equal(
            figures.Turns.Select(static turn => turn.SpanId).ToList(),
            run.GetProperty("turns").EnumerateArray()
                .Select(static turn => turn.GetProperty("spanId").GetString() ?? string.Empty)
                .ToList());
        JsonElement first = run.GetProperty("turns").EnumerateArray().First();
        Assert.Equal("sess-run-a", first.GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.String, first.GetProperty("executionId").ValueKind);

        // The run page pays the turn figures read, not the detail figure set.
        Assert.Contains(nameof(InMemoryRelationalStore.SessionTurnFigures), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.SessionDetailFigures), store.ReadLog);
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));
    }

    /// <summary>
    /// An unknown session's section routes answer nothing through the header read: the 404 check
    /// is <see cref="InMemoryRelationalStore.SessionHeadersByKeys"/>, never the full projection
    /// read, and no content-free read runs either.
    /// </summary>
    [Fact]
    public void SectionRoutesAnswer404ThroughTheHeaderRead()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        Assert.Null(bridge.GetSessionSummary("sess-absent"));
        Assert.Null(bridge.GetSessionCharts("sess-absent"));
        Assert.Null(bridge.GetTurnFigures("sess-absent", 0));

        Assert.Contains(nameof(InMemoryRelationalStore.SessionHeadersByKeys), store.SessionProjectionReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.GetSessionProjection), store.SessionProjectionReadLog);
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));
    }

    /// <summary>Seeds the 128-turn section session: measurable context, tool figures, and one skill invocation.</summary>
    private static void WriteSectionSession(InMemoryRelationalStore store, string sessionKey)
    {
        List<CollectedRecord> turns =
        [
            .. Enumerable.Range(0, 128).Select(i => Turn(
                $"turn-sec-{i:D3}",
                sessionKey,
                i,
                inputTokens: 1_000 + i,
                cacheRead: 200 + i,
                outputTokens: 100 + i,
                model: i % 2 == 0 ? "claude-sonnet-4-6" : "gpt-5.1-codex",
                reportedCostUsd: 0.01,
                parts: [TextPart(0, "user_prompt", new string('x', 4_096))],
                declaredContextWindow: 2_000)),
        ];
        turns[0] = WithToolFigures(turns[0]);
        turns[5] = turns[5] with { SkillsInvoked = ["commit"] };

        // The last turn's reported input disagrees with its tokenized input, so the first and
        // last context edges price the records' reported input, not the stored measured input.
        turns[127] = turns[127] with { ReportedInputTokens = 900 };
        WriteSession(store, sessionKey, turns, repo: "kyber-weave", branch: "kw-task/T4", title: "section session");
    }

    /// <summary>Attaches one tool invocation ("read") and two tool definitions ("read", "write").</summary>
    private static CollectedRecord WithToolFigures(CollectedRecord record) =>
        record with
        {
            Tool = new CollectedToolCall(
                CallId: "call-sections",
                ToolName: "read",
                McpServerName: null,
                McpToolName: null,
                Input: new CollectedToolContent("tool input text", 15, 15, null, null),
                Result: new CollectedToolContent("tool result text", 16, 16, null, null),
                Succeeded: true,
                Error: null,
                ExitCode: 0),
            ToolDefinitions =
            [
                new CollectedToolDefinition(0, "read", null, 120),
                new CollectedToolDefinition(1, "write", null, 80),
            ],
        };

    private static KyberBridgeEngine CreateBridge(InMemoryRelationalStore store, FakePricingRepository pricing) =>
        new(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing),
            new CanonRollupsEngine(store, store, pricing),
            clock: () => TurnBase.AddHours(2),
            instanceId: "test-instance");

    private static CanonProjectionService CreateProjection(InMemoryRelationalStore store, FakePricingRepository pricing) =>
        new(
            store,
            store,
            store,
            new CanonSessionProjectorEngine(store, store, store, pricing),
            new CanonRollupsEngine(store, store, pricing));

    private static void WriteSession(
        InMemoryRelationalStore store,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records,
        string harnessId = "claude-code",
        string? repo = null,
        string? branch = null,
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
                    Workspace: repo is null && branch is null
                        ? null
                        : new CollectedWorkspace("/tmp/working-directory", repo, branch),
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
        string? model = "claude-sonnet-4-6",
        double? reportedCostUsd = null,
        IReadOnlyList<CollectedContentPart>? parts = null,
        long? declaredContextWindow = null,
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
            ReportedCost: reportedCostUsd.HasValue
                ? new CollectedCost(reportedCostUsd.Value, "USD")
                : null,
            Parts: parts ?? [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: declaredContextWindow),
            ReportedInputTokens: inputTokens + cacheRead,
            ReportedOutputTokens: outputTokens);
    }

    private static CollectedContentPart TextPart(int position, string bucket, string text) =>
        new(
            Position: position,
            Bucket: PromptBucket(bucket),
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static ContentBucket PromptBucket(string bucket) =>
        bucket switch
        {
            "user_prompt" => ContentBucket.UserPrompt,
            "user_message" => ContentBucket.UserMessage,
            "conversation_history" => ContentBucket.ConversationHistory,
            "tool_result_content" => ContentBucket.ToolResultContent,
            _ => throw new ArgumentException($"Unexpected bucket '{bucket}'.", nameof(bucket)),
        };
}
