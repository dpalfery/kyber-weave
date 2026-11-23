using System.Globalization;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;
using KyberDash.Mcp;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// Runs the real <see cref="AnalyticsService"/> and the real bridge, projector, and rollup engines
/// over an in-memory relational store for every operation the MCP tool catalog advertises.
/// </summary>
/// <remarks>
/// The store is seeded with records shaped like the ones a real 272k-record store holds: a
/// claude-code interaction whose span attributes once arrived as a <c>content_json</c> column
/// carrying a boolean and a number beside the text. That shape made every operation fail before the
/// legacy store read learned to keep such a value; on the relational store the mapper renders those
/// values to <c>content_part</c> text rows ("false", "46", "0.0125"), so the same shape must answer
/// every operation without throwing.
/// </remarks>
public class AnalyticsServiceOperationsTests
{
    private const string SessionId = "e8a9024c-4935-4580-9fdf-711424ed8580";

    private static readonly DateTimeOffset FirstTurnAt = new(2026, 11, 5, 20, 44, 0, TimeSpan.Zero);

    [Fact]
    public void EveryCatalogOperationAnswersOverAStoreShapedLikeTheRealOne()
    {
        InMemoryRelationalStore store = new();
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: SessionId,
                    HarnessId: "claude-code",
                    NativeSessionId: SessionId,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records:
            [
                InteractionRecord("322a688a4f2ac6cb", FirstTurnAt, parentSpanId: null),
                InteractionRecord("9333a28bc2c8095", FirstTurnAt.AddSeconds(8), parentSpanId: "322a688a4f2ac6cb"),
                ToolRecord("0b6f9d1e1f2a3b4c", FirstTurnAt.AddSeconds(10)),
            ],
            Problems: []));

        AnalyticsService analytics = BuildService(store);

        Assert.NotEmpty(DashToolCatalog.Tools);
        foreach (DashToolCatalog.DashTool tool in DashToolCatalog.Tools)
        {
            JsonElement? request = string.Equals(tool.Method, "POST", StringComparison.Ordinal)
                ? JsonSerializer.SerializeToElement(new { sessionId = SessionId })
                : null;

            AnalyticsExecutionResult result = analytics.Execute(tool.Operation, request);

            Assert.Equal(200, result.StatusCode);
            JsonElement envelope = result.Envelope;
            Assert.Equal(1, envelope.GetProperty("contractVersion").GetInt32());
            Assert.Equal(JsonValueKind.Null, envelope.GetProperty("error").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);
        }
    }

    [Fact]
    public void TheStoredContentShapeSurvivesProjectionIntoASessionSummary()
    {
        InMemoryRelationalStore store = new();
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: SessionId,
                    HarnessId: "claude-code",
                    NativeSessionId: SessionId,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records: [InteractionRecord("322a688a4f2ac6cb", FirstTurnAt, parentSpanId: null)],
            Problems: []));

        AnalyticsService analytics = BuildService(store);

        AnalyticsExecutionResult result = analytics.Execute(
            "inspect",
            JsonSerializer.SerializeToElement(new { sessionId = SessionId }));

        Assert.Equal(200, result.StatusCode);
        Assert.Contains(SessionId, result.Envelope.GetProperty("data").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailingOperationIsAnsweredAsAContractV1ErrorEnvelope()
    {
        (Mock<IRelationalStoreReader> reader, InMemoryRelationalStore projections, InMemoryRelationalStore operational) = CreateFailingReader();
        AnalyticsService analytics = BuildService(reader.Object, projections, operational);

        AnalyticsExecutionResult result = analytics.Execute("query", null);

        Assert.Equal(500, result.StatusCode);
        JsonElement envelope = result.Envelope;
        Assert.Equal(1, envelope.GetProperty("contractVersion").GetInt32());
        // Dropped legacy-only assertion: the legacy store answered error code "internal_error" from
        // CanonStoreRepository.ComputeStoreGeneration; the relational bridge types the same fault as
        // ANALYTICS_INTERNAL.
        Assert.Equal("ANALYTICS_INTERNAL", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("the store could not be read", envelope.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private static AnalyticsService BuildService(InMemoryRelationalStore store) =>
        BuildService(store, store, store);

    private static AnalyticsService BuildService(
        IRelationalStoreReader reader,
        IProjectionStore projections,
        IOperationalStore operational)
    {
        FakePricingRepository pricing = new();
        KyberBridgeEngine bridge = new(
            reader,
            projections,
            operational,
            pricing,
            new CanonSessionProjectorEngine(reader, projections, operational, pricing),
            new CanonRollupsEngine(reader, projections, pricing),
            clock: () => DateTimeOffset.Parse("2026-11-19T00:00:00.000Z", CultureInfo.InvariantCulture),
            instanceId: "test-engine");

        return new AnalyticsService(bridge);
    }

    private static (Mock<IRelationalStoreReader> Reader, InMemoryRelationalStore Projections, InMemoryRelationalStore Operational) CreateFailingReader()
    {
        Mock<IRelationalStoreReader> reader = new();
        reader.Setup(store => store.Sessions(It.IsAny<string?>())).Returns(Array.Empty<CollectedSession>());
        reader.Setup(store => store.Records(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Array.Empty<CollectedRecord>());
        reader.Setup(store => store.CountRecords(It.IsAny<string?>())).Returns(0);
        reader.Setup(store => store.Harnesses()).Returns(Array.Empty<string>());
        reader.Setup(store => store.SessionAttributions())
            .Throws(new InvalidOperationException("the store could not be read"));

        return (reader, new InMemoryRelationalStore(), new InMemoryRelationalStore());
    }

    /// <summary>
    /// A claude-code interaction record. Its parts carry the exact values the failing row in the
    /// real store held after the legacy column was decoded: a boolean reads "false" and a number
    /// reads as its digits, rendered to text rows by the mapper.
    /// </summary>
    private static CollectedRecord InteractionRecord(string spanId, DateTimeOffset startedAt, string? parentSpanId) => new(
        RecordId: spanId,
        HarnessId: "claude-code",
        Op: RecordOp.LlmInvoke,
        Kind: RecordKind.Client,
        Name: "claude_code.llm_request",
        StartedAt: startedAt,
        Status: RecordStatus.Ok,
        SessionKey: SessionId,
        ParentRecordId: parentSpanId,
        TraceId: $"trace-{SessionId}",
        Model: "claude-sonnet-4-5",
        EndedAt: startedAt.AddSeconds(3),
        ReportedDurationMs: 3_000,
        Tokens:
        [
            new CollectedTokenCount(TokenClass.Input, 1_200),
            new CollectedTokenCount(TokenClass.CacheRead, 400),
            new CollectedTokenCount(TokenClass.Output, 180),
        ],
        ReportedCost: null,
        Parts:
        [
            TextPart(0, ContentBucket.SystemPrompt, "You are Claude Code, Anthropic's official CLI for Claude."),
            TextPart(1, ContentBucket.ConversationHistory, "user: read the file\nassistant: here it is"),
            TextPart(2, ContentBucket.ConversationHistory, "anthropic.custom_base_url=false"),
            TextPart(3, ContentBucket.ConversationHistory, "false"),
            TextPart(4, ContentBucket.ConversationHistory, "46"),
            TextPart(5, ContentBucket.ConversationHistory, "0.0125"),
        ],
        Tool: null,
        Agent: null,
        SkillsInvoked: [],
        AgentInstanceId: null,
        ToolDefinitions: [],
        Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
        ReportedInputTokens: 1_600,
        ReportedOutputTokens: 180);

    private static CollectedRecord ToolRecord(string spanId, DateTimeOffset startedAt) => new(
        RecordId: spanId,
        HarnessId: "claude-code",
        Op: RecordOp.ToolInvoke,
        Kind: RecordKind.Internal,
        Name: "Read",
        StartedAt: startedAt,
        Status: RecordStatus.Ok,
        SessionKey: SessionId,
        ParentRecordId: "322a688a4f2ac6cb",
        TraceId: $"trace-{SessionId}",
        Model: null,
        EndedAt: startedAt.AddMilliseconds(12),
        ReportedDurationMs: 12,
        Tokens: [],
        ReportedCost: null,
        Parts: [new CollectedContentPart(0, ContentBucket.ToolResultContent, new string('z', 400), null, 100, null, null, null, null)],
        Tool: new CollectedToolCall(
            CallId: "call-read-1",
            ToolName: "Read",
            McpServerName: null,
            McpToolName: null,
            Input: null,
            Result: new CollectedToolContent(new string('z', 400), 400, 400, null, null),
            Succeeded: true,
            Error: null,
            ExitCode: null),
        Agent: null,
        SkillsInvoked: [],
        AgentInstanceId: null,
        ToolDefinitions: [],
        Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
        ReportedInputTokens: null,
        ReportedOutputTokens: null);

    private static CollectedContentPart TextPart(int position, ContentBucket bucket, string text) =>
        new(
            Position: position,
            Bucket: bucket,
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);
}
