using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Verifies that a turn a harness reports through both OTLP and a transcript file is counted once
/// when the two records carry the same response id or request id: the ids are stored, read back
/// and hydrated where the projector's id join reads them. Every case writes through
/// <see cref="InMemoryRelationalStore"/> and projects from the store, so an id the store drops
/// fails the case.
/// </summary>
public class OtlpFileIdJoinTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Hydration puts an OTLP record's ids under the OTLP keys the id join reads.</summary>
    [Fact]
    public void Hydrate_OtlpRecord_SetsResponseAndRequestIdKeys()
    {
        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(
            Record("o", "s", 0, 1, 1, "claude-code", responseId: "msg_1", requestId: "req_1", sourceName: "otlp:claude-code"));

        Assert.Equal("msg_1", hydrated.Raw!["gen_ai.response.id"].GetString());
        Assert.Equal("req_1", hydrated.Raw!["request_id"].GetString());
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
    }

    /// <summary>
    /// A Claude Code session whose OTLP turns and transcript turns share message ids projects each
    /// turn once, kept on the OTLP row, with the OTLP rows' token totals.
    /// </summary>
    [Fact]
    public void ClaudeCode_OtlpAndFileWithMatchingMessageIds_ProjectOnce()
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "cc", "claude-code",
        [
            Record("otlp-1", "cc", 0, 700, 120, "claude-code", responseId: "msg_A"),
            Record("otlp-2", "cc", 2, 900, 90, "claude-code", responseId: "msg_B"),
        ]);
        WriteFile(store, "cc", "claude-code",
        [
            Record("synth:claude-code:cc:msg_A", "cc", 0, 650, 110, "claude-code", responseId: "msg_A"),
            Record("synth:claude-code:cc:msg_B", "cc", 2, 870, 80, "claude-code", responseId: "msg_B"),
        ]);

        AssertProjectsOtlpOnly(store, "cc", ["otlp-1", "otlp-2"], 700L + 900L, 120L + 90L);
    }

    /// <summary>A Claude Code session whose two sides share only request ids also projects once.</summary>
    [Fact]
    public void ClaudeCode_OtlpAndFileWithMatchingRequestIds_ProjectOnce()
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "cc-req", "claude-code",
            [Record("otlp-1", "cc-req", 0, 700, 120, "claude-code", requestId: "req_A")]);
        WriteFile(store, "cc-req", "claude-code",
            [Record("synth:claude-code:cc-req:x", "cc-req", 0, 650, 110, "claude-code", requestId: "req_A")]);

        AssertProjectsOtlpOnly(store, "cc-req", ["otlp-1"], 700L, 120L);
    }

    /// <summary>
    /// A Codex session whose OTLP turns and rollout turns share request ids projects each turn once
    /// with the OTLP rows' token totals.
    /// </summary>
    [Fact]
    public void Codex_OtlpAndFileWithMatchingRequestIds_ProjectOnce()
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "cx", "codex",
        [
            Record("otlp-1", "cx", 0, 2_000, 300, "codex", requestId: "req_1"),
            Record("otlp-2", "cx", 3, 2_500, 200, "codex", requestId: "req_2"),
        ]);
        WriteFile(store, "cx", "codex",
        [
            Record("synth:codex:cx:turn-0", "cx", 0, 1_900, 280, "codex", requestId: "req_1"),
            Record("synth:codex:cx:turn-1", "cx", 3, 2_400, 190, "codex", requestId: "req_2"),
        ]);

        AssertProjectsOtlpOnly(store, "cx", ["otlp-1", "otlp-2"], 2_000L + 2_500L, 300L + 200L);
    }

    /// <summary>OTLP and file rows whose ids differ both stay turns.</summary>
    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex")]
    public void OtlpAndFileWithDifferentIds_KeepBoth(string harness)
    {
        InMemoryRelationalStore store = new();
        WriteOtlp(store, "diff", harness,
            [Record("otlp-1", "diff", 0, 700, 120, harness, responseId: "msg_A", requestId: "req_A")]);
        WriteFile(store, "diff", harness,
            [Record("synth:file-1", "diff", 0, 650, 110, harness, responseId: "msg_Z", requestId: "req_Z")]);

        NewEngine(store).ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("diff")!;
        Assert.Equal(["otlp-1", "synth:file-1"], projection.Turns.Select(static t => t.SpanId));
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

    private static CanonSessionProjectorEngine NewEngine(InMemoryRelationalStore store) =>
        new(store, store, store, new FakePricingRepository());

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
        string? sourceName = null) =>
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
            Model: harnessId == "codex" ? "gpt-5-codex" : "claude-sonnet-4-6",
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
            Parts: [],
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
}
