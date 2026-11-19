using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Domain;
using KyberDash.Mcp;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// Runs the real <see cref="AnalyticsService"/> and the real bridge, projector, and rollup engines
/// over an in-memory store for every operation the MCP tool catalog advertises.
/// </summary>
/// <remarks>
/// The store is seeded with records shaped like the ones a real 272k-record store holds: a
/// claude-code interaction whose <c>content_json</c> carried span attribute values verbatim, so a
/// boolean and a number sit beside the text. That shape made every operation fail before the store
/// read learned to keep such a value.
/// </remarks>
public class AnalyticsServiceOperationsTests
{
    private const string SessionId = "e8a9024c-4935-4580-9fdf-711424ed8580";

    [Fact]
    public void EveryCatalogOperationAnswersOverAStoreShapedLikeTheRealOne()
    {
        FakeCanonStoreRepository store = new();
        store.PutRecords(
        [
            InteractionRecord("322a688a4f2ac6cb", "2026-11-05T20:44:00.956Z"),
            InteractionRecord("9333aa28bc2c8095", "2026-11-05T20:44:08.498Z"),
            ToolRecord("0b6f9d1e1f2a3b4c", "2026-11-05T20:44:10.100Z"),
        ]);

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
        FakeCanonStoreRepository store = new();
        store.PutRecord(InteractionRecord("322a688a4f2ac6cb", "2026-11-05T20:44:00.956Z"));

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
        Mock<ICanonStoreRepository> store = new();
        store
            .Setup(s => s.ComputeStoreGeneration())
            .Throws(new InvalidOperationException("the store could not be read"));
        AnalyticsService analytics = BuildService(store.Object);

        AnalyticsExecutionResult result = analytics.Execute("status", null);

        Assert.Equal(500, result.StatusCode);
        JsonElement envelope = result.Envelope;
        Assert.Equal(1, envelope.GetProperty("contractVersion").GetInt32());
        Assert.Equal("internal_error", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("the store could not be read", envelope.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private static AnalyticsService BuildService(ICanonStoreRepository store)
    {
        FakePricingRepository pricing = new();
        KyberBridgeEngine bridge = new(
            store,
            new FakeSettingsRepository(),
            pricing,
            new CanonSessionProjectorEngine(store, pricing),
            new CanonRollupsEngine(store, pricing),
            clock: () => DateTimeOffset.Parse("2026-11-19T00:00:00.000Z", CultureInfo.InvariantCulture),
            instanceId: "test-engine");

        return new AnalyticsService(bridge);
    }

    /// <summary>
    /// A claude-code interaction record. Its content carries the exact keys and value types the
    /// failing row in the real store held after the column was decoded: a boolean reads "false" and
    /// a number reads as its digits.
    /// </summary>
    private static CanonicalRecord InteractionRecord(string spanId, string timestamp) => new()
    {
        SpanId = spanId,
        TraceId = $"trace-{SessionId}",
        Source = "claude-code-desktop",
        Harness = "claude-code",
        SessionId = SessionId,
        Name = "claude_code.llm_request",
        Op = "llm.invoke",
        Kind = "client",
        Timestamp = timestamp,
        DurationMs = 3,
        Status = "ok",
        Tokens = new TokenUsage(FreshInput: 1_200, CacheRead: 400, CacheCreation: 0, Output: 180, ReportedInput: 1_200, ReportedOutput: 180),
        Content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["log_body"] = "claude_code.api_request_body",
            ["service.name"] = "claude-code-desktop",
            ["anthropic.custom_base_url"] = "false",
            ["anthropic.plan"] = "pro",
            ["event.sequence"] = "46",
            ["is_async"] = "true",
            ["model_swapped"] = "false",
            ["cost_usd"] = "0.0125",
            ["cache_creation_1h_tokens"] = "0",
            ["system_prompt"] = "You are Claude Code, Anthropic's official CLI for Claude.",
            ["conversation_history"] = "user: read the file\nassistant: here it is",
        },
        Cost = new CostBlock(CostBasis.Published, CostStatus.Priced),
        Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["gen_ai.response.model"] = Json("claude-sonnet-4-5"),
            ["anthropic.custom_base_url"] = Json(false),
            ["event.sequence"] = Json(46),
        },
    };

    private static CanonicalRecord ToolRecord(string spanId, string timestamp) => new()
    {
        SpanId = spanId,
        TraceId = $"trace-{SessionId}",
        ParentSpanId = "322a688a4f2ac6cb",
        Source = "claude-code-desktop",
        Harness = "claude-code",
        SessionId = SessionId,
        Name = "Read",
        Op = "tool.invoke",
        Kind = "internal",
        Timestamp = timestamp,
        DurationMs = 12,
        Status = "ok",
        Tokens = new TokenUsage(0, 0, 0, 0, 0, 0),
        Content = new Dictionary<string, string>(StringComparer.Ordinal),
        Parts = [new ContentPart("tool_result_content", new string('z', 400), Tokens: 100)],
        Cost = new CostBlock(CostBasis.Published, CostStatus.OutOfScope),
        Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["tool_name"] = Json("Read"),
            ["success"] = Json(true),
        },
    };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
