using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CopilotSpanMapper"/> covering Copilot OTLP model, tool, and agent
/// spans under the evidenced <c>CopilotAdapter</c> keys: usage-bearing spans map to
/// <c>llm.invoke</c> with the cache-inclusive token convention, <c>gen_ai.tool.name</c> spans map
/// to <c>tool.invoke</c>, and <c>invoke_agent</c> spans map to <c>agent.invoke</c>, without
/// inventing unverified Copilot Chat shapes.
/// </summary>
public class CopilotSpanMapperTests
{
    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HarnessId_IsCopilot()
    {
        CopilotSpanMapper mapper = new();
        Assert.Equal("copilot", mapper.HarnessId);
    }

    [Fact]
    public void MapSpan_LlmSpanWithUsage_AssertsEveryCollectedRecordProperty()
    {
        OtlpSpan span = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "aabbccddeeff0011",
            ParentSpanId: "1122334455667788",
            Name: "copilot_chat.response",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550001250000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 1250.4,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Element("chat-copilot-1"),
                ["model"] = Element("copilot-test-model"),
                ["gen_ai.usage.input_tokens"] = Element(100),
                ["gen_ai.usage.cache_read.input_tokens"] = Element(20),
                ["gen_ai.usage.cache_creation.input_tokens"] = Element(10),
                ["gen_ai.usage.output_tokens"] = Element(30),
                ["gen_ai.usage.reasoning_tokens"] = Element(5),
                ["gen_ai.usage.cost"] = Element(0.0125),
                ["gen_ai.system_instructions"] = Element("You are a test assistant."),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("github.copilot"),
            },
            Scope: new OtlpScope("github.copilot", "1.0.0"));

        CopilotSpanMapper mapper = new();
        CollectedRecord record = mapper.MapSpan(span);

        Assert.Equal("aabbccddeeff0011", record.RecordId);
        Assert.Equal("copilot", record.HarnessId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal(RecordKind.Client, record.Kind);
        Assert.Equal("copilot_chat.response", record.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:00.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("chat-copilot-1", record.SessionKey);
        Assert.Equal("1122334455667788", record.ParentRecordId);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", record.TraceId);
        Assert.Equal("copilot-test-model", record.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:01.250Z", CultureInfo.InvariantCulture), record.EndedAt);
        Assert.Equal(1250L, record.ReportedDurationMs);

        Assert.Equal(5, record.Tokens.Count);
        Assert.Equal(70L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(20L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead).Count);
        Assert.Equal(10L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite).Count);
        Assert.Equal(30L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(5L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Reasoning).Count);
        Assert.Equal(100L, record.ReportedInputTokens);
        Assert.Equal(30L, record.ReportedOutputTokens);

        Assert.NotNull(record.ReportedCost);
        Assert.Equal(0.0125, record.ReportedCost.Amount, precision: 6);
        Assert.Equal("USD", record.ReportedCost.Currency);

        CollectedContentPart part = Assert.Single(record.Parts);
        Assert.Equal(0, part.Position);
        Assert.Equal(ContentBucket.SystemPrompt, part.Bucket);
        Assert.Equal("You are a test assistant.", part.PlainText);
        Assert.Null(part.Withheld);
        Assert.Null(record.Tool);
        Assert.Null(record.Agent);
        Assert.Empty(record.SkillsInvoked);
        Assert.Null(record.AgentInstanceId);
        Assert.Empty(record.ToolDefinitions);
        Assert.Null(record.Signals.ErrorMessage);
        Assert.Null(record.Signals.EditFailures);
        Assert.Null(record.Signals.CorrectionRule);
        Assert.Null(record.Signals.DeclaredContextWindow);
    }

    [Fact]
    public void MapSpan_ToolSpanWithToolName_MapsToolInvoke()
    {
        OtlpSpan span = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "bbbb000011112222",
            ParentSpanId: null,
            Name: "copilot_chat.tool",
            Kind: "internal",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000100000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 100.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.session_id"] = Element("sess-copilot-tool"),
                ["gen_ai.tool.name"] = Element("Read"),
                ["gen_ai.tool.call.id"] = Element("call-1"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("github.copilot"),
            },
            Scope: new OtlpScope("github.copilot", null));

        CollectedRecord record = new CopilotSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.ToolInvoke, record.Op);
        Assert.Equal("sess-copilot-tool", record.SessionKey);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);
        Assert.NotNull(record.Tool);
        Assert.Equal("call-1", record.Tool.CallId);
        Assert.Equal("Read", record.Tool.ToolName);
        Assert.Null(record.Tool.Succeeded);
        Assert.Null(record.Tool.Error);
        Assert.Null(record.Agent);
        Assert.Empty(record.Parts);
    }

    [Fact]
    public void MapSpan_AgentSpanWithInvokeAgentOperation_MapsAgentInvoke()
    {
        OtlpSpan span = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "cccc000011112222",
            ParentSpanId: "bbbb000011112222",
            Name: "invoke_agent",
            Kind: "internal",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000200000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 200.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.session_id"] = Element("sess-copilot-agent"),
                ["gen_ai.operation.name"] = Element("invoke_agent"),
                ["gen_ai.agent.name"] = Element("researcher"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("github.copilot"),
            },
            Scope: new OtlpScope("github.copilot", null));

        CollectedRecord record = new CopilotSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.AgentInvoke, record.Op);
        Assert.Equal("sess-copilot-agent", record.SessionKey);
        Assert.NotNull(record.Agent);
        Assert.Equal("researcher", record.Agent.AgentName);
        Assert.Equal("bbbb000011112222", record.Agent.SpawningCallId);
        Assert.Null(record.Agent.Succeeded);
        Assert.Null(record.Tool);
        Assert.Empty(record.Tokens);
    }

    [Fact]
    public void MapSpan_AgentSpanNameWithSuffix_MapsAgentInvoke()
    {
        OtlpSpan span = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "dddd000011112222",
            ParentSpanId: null,
            Name: "invoke_agent: explorer",
            Kind: "internal",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000200000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 200.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.session_id"] = Element("sess-copilot-agent-name"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("github.copilot"),
            },
            Scope: new OtlpScope("github.copilot", null));

        CollectedRecord record = new CopilotSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.AgentInvoke, record.Op);
        Assert.NotNull(record.Agent);
        Assert.Equal("explorer", record.Agent.AgentName);
    }

    [Fact]
    public void MapSpan_SpanWithoutUsage_MapsUnspecifiedOp()
    {
        OtlpSpan span = new(
            TraceId: "eeee0000111122223333444455556666",
            SpanId: "eeee000011112222",
            ParentSpanId: null,
            Name: "copilot_chat.session.start",
            Kind: "internal",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000000000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 0.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.session_id"] = Element("sess-copilot-idle"),
                ["model"] = Element("copilot-test-model"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("github.copilot"),
            },
            Scope: new OtlpScope("github.copilot", null));

        CollectedRecord record = new CopilotSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.Unspecified, record.Op);
        Assert.Equal(RecordKind.Internal, record.Kind);
        Assert.Equal("sess-copilot-idle", record.SessionKey);
        Assert.Equal("copilot-test-model", record.Model);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);
        Assert.Null(record.Tool);
        Assert.Null(record.Agent);
    }

    [Fact]
    public void MapSpans_BatchMapsSessionsAndRejectsNegativeTokensIntoProblems()
    {
        OtlpSpan first = SpanWithUsage("1111111111111111", null, "chat-batch-1", 40, 12);
        OtlpSpan second = SpanWithUsage("2222222222222222", "1111111111111111", "chat-batch-1", 10, 6);
        OtlpSpan bad = SpanWithUsage("3333333333333333", null, "chat-batch-1", 10, -3);

        CollectedBatch batch = new CopilotSpanMapper().MapSpans([first, second, bad]);

        Assert.Equal("copilot", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("chat-batch-1", session.SessionKey);
        Assert.Equal("copilot", session.HarnessId);

        Assert.Equal(2, batch.Records.Count);
        CollectedRecord firstRecord = Assert.Single(batch.Records, static r => r.RecordId == "1111111111111111");
        Assert.Equal(40L, firstRecord.ReportedInputTokens);
        Assert.Equal(12L, firstRecord.ReportedOutputTokens);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(CanonValidation.TokenNegativeClass, problem.Code);
        Assert.Equal("3333333333333333", problem.SpanId);
        Assert.Equal("copilot", problem.HarnessId);
    }

    [Fact]
    public void Map_NativeSourceUnitOtlpJson_MapsSessionAndRecord()
    {
        string otlpJson = """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "service.name", "value": { "stringValue": "github.copilot" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "scope": { "name": "github.copilot", "version": "1.0.0" },
                      "spans": [
                        {
                          "traceId": "0102030405060708090a0b0c0d0e0f10",
                          "spanId": "4444444444444444",
                          "name": "copilot_chat.response",
                          "kind": "SPAN_KIND_CLIENT",
                          "startTimeUnixNano": "1763550000000000000",
                          "endTimeUnixNano": "1763550000500000000",
                          "status": { "code": "STATUS_CODE_OK" },
                          "attributes": [
                            { "key": "copilot_chat.chat_session_id", "value": { "stringValue": "chat-json-1" } },
                            { "key": "model", "value": { "stringValue": "copilot-test-model" } },
                            { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "40" } },
                            { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "12" } }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        SourceUnit descriptor = new(
            HarnessId: "copilot",
            SourceKey: "http://127.0.0.1:4318/v1/traces",
            ProviderId: "copilot",
            ParserId: "copilot-otlp-mapper",
            ParserContractVersion: "1",
            Format: "otlp-json",
            SourceRootLabel: "otlp",
            RevisionToken: "rev-1",
            Kind: SourceKind.Otlp);
        NativeSourceUnit unit = new(descriptor, otlpJson, Encoding.UTF8.GetByteCount(otlpJson), ImportedAtUtc);

        CollectedBatch batch = new CopilotSpanMapper().Map(unit, ImportedAtUtc);

        Assert.Equal("copilot", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);
        Assert.Equal("http://127.0.0.1:4318/v1/traces", batch.Source.Locator);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("chat-json-1", session.SessionKey);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("4444444444444444", record.RecordId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal("copilot-test-model", record.Model);
        Assert.Equal(40L, record.ReportedInputTokens);
        Assert.Equal(12L, record.ReportedOutputTokens);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_MalformedOtlpPayload_EmitsMalformedOtlpProblem_AndValidBatchPersistsIntoRelationalStore()
    {
        SourceUnit descriptor = new(
            HarnessId: "copilot",
            SourceKey: "http://127.0.0.1:4318/v1/traces",
            ProviderId: "copilot",
            ParserId: "copilot-otlp-mapper",
            ParserContractVersion: "1",
            Format: "otlp-json",
            SourceRootLabel: "otlp",
            RevisionToken: "rev-bad",
            Kind: SourceKind.Otlp);
        NativeSourceUnit malformedUnit = new(descriptor, "{not-valid-json", 15, ImportedAtUtc);

        CopilotSpanMapper mapper = new();
        CollectedBatch errorBatch = mapper.Map(malformedUnit, ImportedAtUtc);
        Assert.Empty(errorBatch.Records);
        CollectedProblem problem = Assert.Single(errorBatch.Problems);
        Assert.Equal("MALFORMED_OTLP_PAYLOAD", problem.Code);
        Assert.Equal("copilot", problem.HarnessId);

        OtlpSpan modelSpan = SpanWithUsage("span-persist-model", null, "chat-persist-1", 50, 15);
        CollectedBatch validBatch = mapper.MapSpans([modelSpan]);
        InMemoryRelationalStore store = new();
        store.WriteBatch(validBatch);

        Assert.Equal(1, store.CountRecords("copilot"));
        Assert.NotNull(store.GetSession("chat-persist-1"));
        Assert.NotNull(store.FindRecord("span-persist-model"));
    }

    private static OtlpSpan SpanWithUsage(
        string spanId,
        string? parentSpanId,
        string chatSessionId,
        long inputTokens,
        long outputTokens)
    {
        return new OtlpSpan(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            Name: "copilot_chat.response",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000500000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 500.0,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Element(chatSessionId),
                ["model"] = Element("copilot-test-model"),
                ["gen_ai.usage.input_tokens"] = Element(inputTokens),
                ["gen_ai.usage.output_tokens"] = Element(outputTokens),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("github.copilot"),
            },
            Scope: new OtlpScope("github.copilot", "1.0.0"));
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
}
