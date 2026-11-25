using System.Text;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Claude;
using KyberDash.Data.Harnesses.Codex;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Verifies that a turn Claude Code reports through both OTLP and its transcript file is counted
/// once, joined on the ids the vendor documents: OTel <c>request_id</c> against the transcript
/// line's <c>requestId</c>, and OTel <c>message.id</c> (on <c>api_response_body</c>) against the
/// transcript's <c>message.id</c>. Claude Code does not document <c>gen_ai.response.id</c>, so it
/// is never read. A Codex session observed by both its OTLP exporter and its rollout file has no
/// id join — Codex OTel carries no per-model-call id — so a rollout model call drops only when an
/// OTLP model call pairs with it on data both sides carry: equal reported counters in the same
/// session within <see cref="CanonSessionProjectorEngine.TwinTurnMaxSkewMs"/>, one OTLP call per
/// rollout call. A paired rollout call's parts move onto its OTLP turn; an unpaired call of either
/// side stays a turn (D21). Every case writes through <see cref="InMemoryRelationalStore"/> and
/// projects from the store, so an id or counter any mapper or the store drops fails the case.
/// </summary>
public class OtlpFileIdJoinTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Hydration puts an OTLP record's ids under the OTLP keys the id join reads.</summary>
    [Fact]
    public void Hydrate_OtlpRecord_SetsMessageAndRequestIdKeys()
    {
        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(
            Record("o", "s", 0, 1, 1, "claude-code", responseId: "msg_1", requestId: "req_1", sourceName: "claude-code"));

        Assert.Equal("msg_1", hydrated.Raw!["message.id"].GetString());
        Assert.Equal("req_1", hydrated.Raw!["request_id"].GetString());
        Assert.False(hydrated.Raw!.ContainsKey("gen_ai.response.id"));
        Assert.False(hydrated.Raw!.ContainsKey("requestId"));
    }

    /// <summary>Hydration puts a file record's ids under the transcript keys the id join reads.</summary>
    [Fact]
    public void Hydrate_FileRecord_SetsNativeRecordIdAndRequestIdKeys()
    {
        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(
            Record("f", "s", 0, 1, 1, "claude-code", responseId: "msg_1", requestId: "req_1", sourceName: CanonSynth.FileSourcePrefix + "claude-code"));

        Assert.Equal("msg_1", hydrated.Raw!["provenance"].GetProperty("nativeRecordId").GetString());
        Assert.Equal("req_1", hydrated.Raw!["requestId"].GetString());
        Assert.False(hydrated.Raw!.ContainsKey("gen_ai.response.id"));
        Assert.False(hydrated.Raw!.ContainsKey("message.id"));
    }

    /// <summary>
    /// A Claude Code <c>api_request</c> OTLP log carrying <c>request_id</c> and a transcript line
    /// carrying the same line-level <c>requestId</c> project one turn, kept on the OTLP row, with
    /// the OTLP row's token totals.
    /// </summary>
    [Fact]
    public void ClaudeCode_ApiRequestAndTranscriptWithMatchingRequestIds_ProjectOnce()
    {
        InMemoryRelationalStore store = new();
        WriteClaudeOtlp(store, ClaudeApiRequestLog("cc-req", requestId: "req_01XYZ"));
        WriteClaudeTranscript(store, "cc-req",
            "{\"type\":\"assistant\",\"sessionId\":\"cc-req\",\"requestId\":\"req_01XYZ\",\"timestamp\":\"2026-11-20T09:00:00.100Z\","
            + "\"message\":{\"id\":\"msg_01ABC\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\","
            + "\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"usage\":{\"input_tokens\":650,\"output_tokens\":110}}}");

        AssertProjectsOtlpOnly(store, "cc-req", ["log-cc-req"], 700L, 120L);
    }

    /// <summary>
    /// A Claude Code <c>api_response_body</c> OTLP log carrying <c>message.id</c> and a transcript
    /// line carrying the same <c>message.id</c> project one turn, kept on the OTLP row, with the
    /// OTLP row's token totals. The transcript line carries no <c>requestId</c>, so only the
    /// message-id join can collapse the pair.
    /// </summary>
    [Fact]
    public void ClaudeCode_ResponseBodyAndTranscriptWithMatchingMessageIds_ProjectOnce()
    {
        InMemoryRelationalStore store = new();
        WriteClaudeOtlp(store, ClaudeResponseBodyLog("cc-msg", messageId: "msg_02DEF", requestId: "req_02ZYX"));
        WriteClaudeTranscript(store, "cc-msg",
            "{\"type\":\"assistant\",\"sessionId\":\"cc-msg\",\"timestamp\":\"2026-11-20T09:05:00.100Z\","
            + "\"message\":{\"id\":\"msg_02DEF\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\","
            + "\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"usage\":{\"input_tokens\":650,\"output_tokens\":110}}}");

        AssertProjectsOtlpOnly(store, "cc-msg", ["log-cc-msg"], 700L, 120L);
    }

    /// <summary>
    /// A Claude Code session whose OTLP row carries only <c>gen_ai.response.id</c> — an attribute
    /// the vendor does not document — does not join to a transcript line with the same value: both
    /// rows stay turns.
    /// </summary>
    [Fact]
    public void ClaudeCode_UndocumentedGenAiResponseId_DoesNotJoin()
    {
        InMemoryRelationalStore store = new();
        WriteClaudeOtlp(store, ClaudeApiRequestLog("cc-old", requestId: null, genAiResponseId: "msg_03GHI"));
        WriteClaudeTranscript(store, "cc-old",
            "{\"type\":\"assistant\",\"sessionId\":\"cc-old\",\"timestamp\":\"2026-11-20T09:10:00.100Z\","
            + "\"message\":{\"id\":\"msg_03GHI\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\","
            + "\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"usage\":{\"input_tokens\":650,\"output_tokens\":110}}}");

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("cc-old")!;
        Assert.Equal(2, projection.Turns.Count);
    }

    /// <summary>OTLP and file rows whose ids differ both stay turns.</summary>
    [Fact]
    public void OtlpAndFileWithDifferentIds_KeepBoth()
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "diff", "claude-code",
            [Record("otlp-1", "diff", 0, 700, 120, "claude-code", responseId: "msg_A", requestId: "req_A")]);
        WriteFile(store, "diff", "claude-code",
            [Record("synth:file-1", "diff", 0, 650, 110, "claude-code", responseId: "msg_Z", requestId: "req_Z")]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("diff")!;
        Assert.Equal(["otlp-1", "synth:file-1"], projection.Turns.Select(static t => t.SpanId));
    }

    /// <summary>
    /// A Codex session whose OTLP and rollout model calls share no data — their reported counters
    /// differ — pairs nothing: Codex OTel carries no per-model-call id, so all four model calls stay
    /// turns and the totals are the sum of all four (D21: nothing pairs, nothing is guessed). The
    /// rollout's tool call, by <c>call_id</c>, stays counted. Both harness ids a Codex record can
    /// carry are covered: <c>codex-cli</c> (OTLP spans and the rollout file) and <c>codex</c>
    /// (OTLP logs).
    /// </summary>
    [Theory]
    [InlineData("codex-cli")]
    [InlineData("codex")]
    public void Codex_SessionWithBothSourcesAndNoSharedData_KeepsEveryModelCall(string harness)
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "cx", harness,
        [
            Record("otlp-1", "cx", 0, 2_000, 300, harness),
            Record("otlp-2", "cx", 3, 2_500, 200, harness),
        ]);
        WriteFile(store, "cx", harness,
        [
            Record("file-1", "cx", 0, 1_900, 280, harness),
            Record("file-2", "cx", 3, 2_400, 190, harness),
            ToolRecord("file-tool-1", "cx", 1, "call_9f2", "shell", harness),
        ]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("cx")!;
        Assert.Equal(
            ["file-1", "file-2", "otlp-1", "otlp-2"],
            projection.Turns.Select(static t => t.SpanId).Order(StringComparer.Ordinal));
        SessionSummaryFigure summary = ((IRelationalStoreReader)store).SessionSummaryFigures("cx")!;
        Assert.Equal(4, summary.TurnCount);
        Assert.Equal(8_800L, summary.TotalInput);
        Assert.Equal(970L, summary.TotalOutput);
        Assert.Equal(1, summary.ToolCalls);
        Assert.Equal(["shell"], summary.ToolsInvoked);
    }

    /// <summary>
    /// Two OTLP and two rollout model calls with equal counters, close in time, pair one to one: the
    /// session projects the two OTLP turns with the OTLP totals, never both sources, and each kept
    /// turn carries its paired rollout call's user and reasoning parts. The rollout's tool call stays
    /// counted.
    /// </summary>
    [Theory]
    [InlineData("codex-cli")]
    [InlineData("codex")]
    public void Codex_PairedModelCalls_KeepOtlpTurnsWithRolloutParts(string harness)
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "cx-pair", harness,
        [
            Record("otlp-1", "cx-pair", 0, 2_000, 300, harness),
            Record("otlp-2", "cx-pair", 3, 2_500, 200, harness),
        ]);
        WriteFile(store, "cx-pair", harness,
        [
            Record("file-1", "cx-pair", 0, 2_000, 300, harness, parts: TurnParts("first")),
            Record("file-2", "cx-pair", 3, 2_500, 200, harness, parts: TurnParts("second")),
            ToolRecord("file-tool-1", "cx-pair", 1, "call_9f2", "shell", harness),
        ]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("cx-pair")!;
        Assert.Equal(["otlp-1", "otlp-2"], projection.Turns.Select(static t => t.SpanId));
        SessionSummaryFigure summary = ((IRelationalStoreReader)store).SessionSummaryFigures("cx-pair")!;
        Assert.Equal(2, summary.TurnCount);
        Assert.Equal(4_500L, summary.TotalInput);
        Assert.Equal(500L, summary.TotalOutput);
        Assert.Equal(1, summary.ToolCalls);

        Dictionary<string, CanonicalRecord> turns = DedupedModelCalls(store, "cx-pair");
        Assert.Equal(["otlp-1", "otlp-2"], turns.Keys.Order(StringComparer.Ordinal));
        AssertCarriesParts(turns["otlp-1"], "first");
        AssertCarriesParts(turns["otlp-2"], "second");
    }

    /// <summary>
    /// OTLP began mid-session: the first rollout call has no OTLP twin and stays a turn with its own
    /// parts, while the last two pair with the two OTLP calls on equal counters. Totals are rollout
    /// call 1 plus the OTLP sums.
    /// </summary>
    [Theory]
    [InlineData("codex-cli")]
    [InlineData("codex")]
    public void Codex_OtlpBeganMidSession_KeepsTheEarlierRolloutTurn(string harness)
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "cx-mid", harness,
        [
            Record("otlp-1", "cx-mid", 3, 2_000, 300, harness),
            Record("otlp-2", "cx-mid", 6, 2_500, 200, harness),
        ]);
        WriteFile(store, "cx-mid", harness,
        [
            Record("file-1", "cx-mid", 0, 1_000, 100, harness, parts: TurnParts("before")),
            Record("file-2", "cx-mid", 3, 2_000, 300, harness, parts: TurnParts("first")),
            Record("file-3", "cx-mid", 6, 2_500, 200, harness, parts: TurnParts("second")),
        ]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("cx-mid")!;
        Assert.Equal(["file-1", "otlp-1", "otlp-2"], projection.Turns.Select(static t => t.SpanId));
        SessionSummaryFigure summary = ((IRelationalStoreReader)store).SessionSummaryFigures("cx-mid")!;
        Assert.Equal(3, summary.TurnCount);
        Assert.Equal(5_500L, summary.TotalInput);
        Assert.Equal(600L, summary.TotalOutput);

        Dictionary<string, CanonicalRecord> turns = DedupedModelCalls(store, "cx-mid");
        AssertCarriesParts(turns["file-1"], "before");
        AssertCarriesParts(turns["otlp-1"], "first");
        AssertCarriesParts(turns["otlp-2"], "second");
    }

    /// <summary>An OTLP and a rollout call with equal counters but more than the skew apart both stay turns.</summary>
    [Theory]
    [InlineData("codex-cli")]
    [InlineData("codex")]
    public void Codex_EqualCountersBeyondSkew_KeepsBoth(string harness)
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "cx-skew", harness, [Record("otlp-1", "cx-skew", 0, 2_000, 300, harness)]);
        WriteFile(store, "cx-skew", harness, [Record("file-1", "cx-skew", 2, 2_000, 300, harness, parts: TurnParts("late"))]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("cx-skew")!;
        Assert.Equal(["otlp-1", "file-1"], projection.Turns.Select(static t => t.SpanId));
        SessionSummaryFigure summary = ((IRelationalStoreReader)store).SessionSummaryFigures("cx-skew")!;
        Assert.Equal(4_000L, summary.TotalInput);
        Assert.Equal(600L, summary.TotalOutput);
    }

    /// <summary>
    /// Pairing is one to one: one OTLP call and two rollout calls, all with equal counters and within
    /// the skew, project two turns — the OTLP turn carrying the earlier rollout call's parts, and the
    /// later rollout call as its own turn.
    /// </summary>
    [Theory]
    [InlineData("codex-cli")]
    [InlineData("codex")]
    public void Codex_OneOtlpCallAndTwoEqualRolloutCalls_PairsOnlyTheEarlier(string harness)
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "cx-one", harness, [Record("otlp-1", "cx-one", 0, 2_000, 300, harness)]);
        WriteFile(store, "cx-one", harness,
        [
            Record("file-1", "cx-one", 0, 2_000, 300, harness, parts: TurnParts("earlier")),
            Record("file-2", "cx-one", 1, 2_000, 300, harness, parts: TurnParts("later")),
        ]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("cx-one")!;
        Assert.Equal(["otlp-1", "file-2"], projection.Turns.Select(static t => t.SpanId));
        SessionSummaryFigure summary = ((IRelationalStoreReader)store).SessionSummaryFigures("cx-one")!;
        Assert.Equal(4_000L, summary.TotalInput);
        Assert.Equal(600L, summary.TotalOutput);

        Dictionary<string, CanonicalRecord> turns = DedupedModelCalls(store, "cx-one");
        AssertCarriesParts(turns["otlp-1"], "earlier");
        Assert.DoesNotContain(turns["otlp-1"].Parts!, static p => p.Text.StartsWith("later", StringComparison.Ordinal));
        AssertCarriesParts(turns["file-2"], "later");
    }

    /// <summary>A paired rollout call with no parts leaves the OTLP turn unchanged.</summary>
    [Theory]
    [InlineData("codex-cli")]
    [InlineData("codex")]
    public void Codex_PairedRolloutCallWithoutParts_LeavesOtlpTurnUnchanged(string harness)
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "cx-bare", harness, [Record("otlp-1", "cx-bare", 0, 2_000, 300, harness)]);
        WriteFile(store, "cx-bare", harness, [Record("file-1", "cx-bare", 0, 2_000, 300, harness)]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("cx-bare")!;
        Assert.Equal(["otlp-1"], projection.Turns.Select(static t => t.SpanId));
        CanonicalRecord kept = Assert.Single(DedupedModelCalls(store, "cx-bare").Values);
        Assert.Equal("otlp-1", kept.SpanId);
        Assert.Empty(kept.Parts ?? []);
        Assert.Empty(kept.Content);
    }

    /// <summary>
    /// The pairing premise: the same reported usage (input, cached, output) written once through
    /// the Codex OTLP span path and once as a rollout record reads back from the store with equal
    /// counter figures (fresh input, cache read, cache creation, output, reported input, reported
    /// output), so the two can pair.
    /// </summary>
    [Fact]
    public void Codex_SameUsageThroughOtlpAndRollout_HydratesToEqualCounters()
    {
        InMemoryRelationalStore store = new();
        store.WriteBatch(new CodexSpanMapper().MapSpans([CodexSpan("cx-hyd", input: 100, cached: 30, output: 20)]));
        string rollout = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-20T09:00:00.000Z","type":"session_meta","payload":{"session_id":"cx-hyd","model":"gpt-5.2-codex"}}""",
                """{"timestamp":"2026-11-20T09:00:00.100Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Run git status"}]}}""",
                """{"timestamp":"2026-11-20T09:00:00.500Z","type":"event_msg","payload":{"type":"token_count","info":{"model":"gpt-5.2-codex","last_token_usage":{"input_tokens":100,"cached_input_tokens":30,"output_tokens":20,"reasoning_output_tokens":4}}}}""",
                """{"timestamp":"2026-11-20T09:00:00.600Z","type":"event_msg","payload":{"type":"task_complete"}}""",
            ]);
        store.WriteBatch(new CodexRolloutMapper().Map(RolloutUnit("cx-hyd", rollout), ImportedAt));

        IReadOnlyList<CanonicalRecord> modelCalls = HydratedModelCalls(store, "cx-hyd");
        Assert.Equal(2, modelCalls.Count);
        TokenUsage otlp = modelCalls.Single(static r => !r.Source.StartsWith(CanonSynth.FileSourcePrefix, StringComparison.Ordinal)).Tokens;
        TokenUsage file = modelCalls.Single(static r => r.Source.StartsWith(CanonSynth.FileSourcePrefix, StringComparison.Ordinal)).Tokens;
        Assert.Equal(
            (70L, 30L, 0L, 20L, 100L, 20L),
            (otlp.FreshInput, otlp.CacheRead, otlp.CacheCreation, otlp.Output, otlp.ReportedInput, otlp.ReportedOutput));
        Assert.Equal(
            (otlp.FreshInput, otlp.CacheRead, otlp.CacheCreation, otlp.Output, otlp.ReportedInput, otlp.ReportedOutput),
            (file.FreshInput, file.CacheRead, file.CacheCreation, file.Output, file.ReportedInput, file.ReportedOutput));
    }

    /// <summary>
    /// A Claude Code counter twin — an OTLP row and a transcript row with equal counters and no id —
    /// still collapses through the shared counter-cluster path, kept on the OTLP row.
    /// </summary>
    [Fact]
    public void ClaudeCode_CounterTwinWithoutIds_StillCollapses()
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "cc-twin", "claude-code", [Record("otlp-cc", "cc-twin", 0, 700, 120, "claude-code")]);
        WriteFile(store, "cc-twin", "claude-code", [Record("synth:file-cc", "cc-twin", 0, 700, 120, "claude-code")]);

        AssertProjectsOtlpOnly(store, "cc-twin", ["otlp-cc"], 700L, 120L);
    }

    /// <summary>A Codex session only the rollout file observed uses the file's model calls.</summary>
    [Fact]
    public void Codex_FileOnlySession_UsesTheFile()
    {
        InMemoryRelationalStore store = new();
        WriteFile(store, "cx-file", "codex-cli",
        [
            Record("file-1", "cx-file", 0, 1_900, 280, "codex-cli"),
            Record("file-2", "cx-file", 3, 2_400, 190, "codex-cli"),
        ]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("cx-file")!;
        Assert.Equal(["file-1", "file-2"], projection.Turns.Select(static t => t.SpanId));
        SessionSummaryFigure summary = ((IRelationalStoreReader)store).SessionSummaryFigures("cx-file")!;
        Assert.Equal(2, summary.TurnCount);
        Assert.Equal(4_300L, summary.TotalInput);
        Assert.Equal(470L, summary.TotalOutput);
    }

    private static void AssertProjectsOtlpOnly(
        InMemoryRelationalStore store,
        string sessionKey,
        string[] expectedTurns,
        long expectedInput,
        long expectedOutput)
    {
        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection(sessionKey)!;
        Assert.Equal(expectedTurns, projection.Turns.Select(static t => t.SpanId));
        SessionSummaryFigure summary = ((IRelationalStoreReader)store).SessionSummaryFigures(sessionKey)!;
        Assert.Equal(expectedTurns.Length, summary.TurnCount);
        Assert.Equal(expectedInput, summary.TotalInput);
        Assert.Equal(expectedOutput, summary.TotalOutput);
    }

    private static List<CanonicalRecord> HydratedModelCalls(InMemoryRelationalStore store, string sessionKey) =>
        [.. HydratedSession(store, sessionKey).Where(static r => r.Op == "llm.invoke")];

    /// <summary>
    /// The session's model calls after twin dedupe, keyed by span id, read from the store and
    /// hydrated the way the projector rebuilds a session.
    /// </summary>
    private static Dictionary<string, CanonicalRecord> DedupedModelCalls(InMemoryRelationalStore store, string sessionKey) =>
        CanonSessionProjectorEngine
            .DedupeTwinTurns(HydratedSession(store, sessionKey), sessionKey)
            .Where(static r => r.Op == "llm.invoke")
            .ToDictionary(static r => r.SpanId, StringComparer.Ordinal);

    private static List<CanonicalRecord> HydratedSession(InMemoryRelationalStore store, string sessionKey) =>
        [.. ((IRelationalStoreReader)store).SessionRecordsForRebuild(sessionKey).Select(CanonRecordHydration.Hydrate)];

    private static void AssertCarriesParts(CanonicalRecord turn, string label)
    {
        Assert.NotNull(turn.Parts);
        Assert.Contains(turn.Parts, p => p.Part == "user_prompt" && p.Text == $"{label} prompt");
        Assert.Contains(turn.Parts, p => p.Part == "reasoning" && p.Text == $"{label} reasoning");
    }

    private static List<CollectedContentPart> TurnParts(string label) =>
    [
        new CollectedContentPart(0, ContentBucket.UserPrompt, $"{label} prompt", null, 10, null, "user", null, null),
        new CollectedContentPart(1, ContentBucket.Reasoning, $"{label} reasoning", null, 10, null, "assistant", null, null),
    ];

    private static OtlpSpan CodexSpan(string sessionKey, long input, long cached, long output) =>
        new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "otlp-hyd",
            ParentSpanId: null,
            Name: "codex.response",
            Kind: "client",
            StartTimeUnixNano: "1795165200400000000",
            EndTimeUnixNano: "1795165200900000000",
            Timestamp: "2026-11-20T09:00:00.400Z",
            DurationMs: 500.0,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["conversation.id"] = JsonSerializer.SerializeToElement(sessionKey),
                ["model"] = JsonSerializer.SerializeToElement("gpt-5.2-codex"),
                ["gen_ai.usage.input_tokens"] = JsonSerializer.SerializeToElement(input),
                ["gen_ai.usage.cached_tokens"] = JsonSerializer.SerializeToElement(cached),
                ["gen_ai.usage.output_tokens"] = JsonSerializer.SerializeToElement(output),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = JsonSerializer.SerializeToElement("codex"),
            },
            Scope: new OtlpScope("openai.codex", "1.0.0"));

    private static NativeSourceUnit RolloutUnit(string sessionKey, string jsonl) =>
        new(
            Unit: new SourceUnit(
                HarnessId: TranscriptReaderRepository.CodexHarnessId,
                SourceKey: $"/home/dev/.codex/sessions/{sessionKey}.jsonl",
                ProviderId: TranscriptReaderRepository.CodexProviderId,
                ParserId: TranscriptReaderRepository.CodexParserId,
                ParserContractVersion: TranscriptReaderRepository.ParserContractVersion,
                Format: TranscriptReaderRepository.CodexFormat,
                SourceRootLabel: "~/.codex/sessions",
                RevisionToken: "1:100",
                Kind: SourceKind.File),
            RawText: jsonl,
            SizeBytes: Encoding.UTF8.GetByteCount(jsonl),
            LastWriteUtc: ImportedAt);

    private static CanonSessionProjectorEngine NewEngine(InMemoryRelationalStore store) =>
        new(store, store, store, new FakePricingRepository(), new FakeTextTokenizer());

    /// <summary>
    /// A Claude Code <c>api_request</c> OTLP log shaped like the vendor's monitoring-usage
    /// documentation: the event carries <c>request_id</c> and no per-message id.
    /// </summary>
    private static OtlpLog ClaudeApiRequestLog(string sessionKey, string? requestId, string? genAiResponseId = null)
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["event.name"] = JsonSerializer.SerializeToElement("claude_code.api_request"),
            ["claude_code.event"] = JsonSerializer.SerializeToElement("api_request"),
            ["gen_ai.request.model"] = JsonSerializer.SerializeToElement("claude-sonnet-4-5"),
            ["gen_ai.usage.input_tokens"] = JsonSerializer.SerializeToElement(700),
            ["gen_ai.usage.output_tokens"] = JsonSerializer.SerializeToElement(120),
        };
        if (requestId is not null)
        {
            attributes["request_id"] = JsonSerializer.SerializeToElement(requestId);
        }

        if (genAiResponseId is not null)
        {
            attributes["gen_ai.response.id"] = JsonSerializer.SerializeToElement(genAiResponseId);
        }

        return new OtlpLog(
            LogId: $"log-{sessionKey}",
            TraceId: "dddddddddddddddddddddddddddddddd",
            SpanId: null,
            SessionId: sessionKey,
            Timestamp: "2026-11-20T09:00:00.000Z",
            Body: "api request",
            Attributes: attributes,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = JsonSerializer.SerializeToElement("claude-code"),
            },
            Scope: new OtlpScope("claude_code", "1.0.0"));
    }

    /// <summary>
    /// A Claude Code <c>api_response_body</c> OTLP log shaped like the vendor's monitoring-usage
    /// documentation: the event carries <c>message.id</c> (the transcript message id),
    /// <c>message.uuid</c>, and <c>request_id</c>.
    /// </summary>
    private static OtlpLog ClaudeResponseBodyLog(string sessionKey, string messageId, string? requestId)
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["event.name"] = JsonSerializer.SerializeToElement("claude_code.api_response_body"),
            ["claude_code.event"] = JsonSerializer.SerializeToElement("api_response_body"),
            ["message.id"] = JsonSerializer.SerializeToElement(messageId),
            ["message.uuid"] = JsonSerializer.SerializeToElement("9c1d3f6a-4a65-4c60-9d3e-6f0a6f4f1a72"),
            ["gen_ai.request.model"] = JsonSerializer.SerializeToElement("claude-sonnet-4-5"),
            ["gen_ai.usage.input_tokens"] = JsonSerializer.SerializeToElement(700),
            ["gen_ai.usage.output_tokens"] = JsonSerializer.SerializeToElement(120),
        };
        if (requestId is not null)
        {
            attributes["request_id"] = JsonSerializer.SerializeToElement(requestId);
        }

        return new OtlpLog(
            LogId: $"log-{sessionKey}",
            TraceId: "dddddddddddddddddddddddddddddddd",
            SpanId: null,
            SessionId: sessionKey,
            Timestamp: "2026-11-20T09:05:00.000Z",
            Body: "api response body",
            Attributes: attributes,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = JsonSerializer.SerializeToElement("claude-code"),
            },
            Scope: new OtlpScope("claude_code", "1.0.0"));
    }

    private static void WriteClaudeOtlp(InMemoryRelationalStore store, OtlpLog log)
    {
        OtlpLogMapResult result = OtlpLogMapper.Map([log]);
        Assert.Single(result.Batches);
        store.WriteBatch(result.Batches[0]);
    }

    private static void WriteClaudeTranscript(InMemoryRelationalStore store, string sessionKey, string jsonl)
    {
        CollectedBatch batch = new ClaudeTranscriptMapper().Map(
            new NativeSourceUnit(
                Unit: new SourceUnit(
                    HarnessId: TranscriptReaderRepository.ClaudeHarnessId,
                    SourceKey: $"/home/dev/.claude/projects/repo/{sessionKey}.jsonl",
                    ProviderId: TranscriptReaderRepository.ClaudeProviderId,
                    ParserId: TranscriptReaderRepository.ClaudeParserId,
                    ParserContractVersion: TranscriptReaderRepository.ParserContractVersion,
                    Format: TranscriptReaderRepository.ClaudeFormat,
                    SourceRootLabel: "~/.claude/projects",
                    RevisionToken: "1:100"),
                RawText: jsonl,
                SizeBytes: Encoding.UTF8.GetByteCount(jsonl),
                LastWriteUtc: ImportedAt),
            ImportedAt);
        Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        store.WriteBatch(batch);
    }

    private static void WriteOtlp(InMemoryRelationalStore store, string sessionKey, string harnessId, IReadOnlyList<CollectedRecord> records) =>
        Write(store, sessionKey, harnessId, SourceKind.Otlp, $"otlp:{harnessId}", records);

    private static void WriteFile(InMemoryRelationalStore store, string sessionKey, string harnessId, IReadOnlyList<CollectedRecord> records) =>
        Write(store, sessionKey, harnessId, SourceKind.File, $"/home/dev/.{harnessId}/sessions/{sessionKey}.jsonl", records);

    private static void Write(
        InMemoryRelationalStore store,
        string sessionKey,
        string harnessId,
        SourceKind kind,
        string locator,
        IReadOnlyList<CollectedRecord> records) =>
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, kind, locator),
            Sessions: [new CollectedSession(sessionKey, harnessId, sessionKey, Workspace: null, Title: null, ReportedUsage: [])],
            Records: records,
            Problems: []));

    private static CollectedRecord Record(
        string spanId,
        string sessionKey,
        int minuteOffset,
        long inputTokens,
        long outputTokens,
        string harnessId,
        string? responseId = null,
        string? requestId = null,
        string? sourceName = null,
        IReadOnlyList<CollectedContentPart>? parts = null) =>
        new(
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
            Model: harnessId.StartsWith("codex", StringComparison.Ordinal) ? "gpt-5-codex" : "claude-sonnet-4-6",
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
            ReportedOutputTokens: outputTokens,
            SourceName: sourceName,
            ResponseId: responseId,
            RequestId: requestId);

    private static CollectedRecord ToolRecord(string spanId, string sessionKey, int minuteOffset, string callId, string toolName, string harnessId) =>
        new(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Client,
            Name: $"tool:{toolName}",
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: 500,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: new CollectedToolCall(
                CallId: callId,
                ToolName: toolName,
                McpServerName: null,
                McpToolName: null,
                Input: null,
                Result: null,
                Succeeded: true,
                Error: null,
                ExitCode: 0),
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: null,
            ReportedOutputTokens: null,
            SourceName: null,
            ResponseId: null,
            RequestId: null);
}
