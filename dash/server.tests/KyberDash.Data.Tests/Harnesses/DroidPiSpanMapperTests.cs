using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="DroidSpanMapper"/> and <see cref="PiSpanMapper"/> covering Droid
/// <c>factory.turn</c>/<c>factory.tool</c>/<c>factory.session</c> spans and Pi OTLP spans with
/// their evidenced attribute keys, plus the shared <c>gemini</c> exclusion: gemini-attributed
/// spans yield zero stored records.
/// </summary>
public class DroidPiSpanMapperTests
{
    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DroidHarnessId_IsDroid()
    {
        DroidSpanMapper mapper = new();
        Assert.Equal("droid", mapper.HarnessId);
    }

    [Fact]
    public void PiHarnessId_IsPi()
    {
        PiSpanMapper mapper = new();
        Assert.Equal("pi", mapper.HarnessId);
    }

    [Fact]
    public void DroidMapSpan_TurnSpan_AssertsEveryCollectedRecordProperty()
    {
        OtlpSpan span = MakeSpan(
            "aabbccddeeff0011",
            "factory.turn",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["factory.session.id"] = Element("sess-droid-1"),
                ["gen_ai.session.id"] = Element("sess-canonical-ignored"),
                ["factory.model.id"] = Element("droid-model-x"),
                ["gen_ai.request.model"] = Element("canonical-model-ignored"),
                ["gen_ai.usage.input_tokens"] = Element(120),
                ["gen_ai.usage.cache_read.input_tokens"] = Element(15),
                ["gen_ai.usage.cache_creation.input_tokens"] = Element(8),
                ["gen_ai.usage.output_tokens"] = Element(40),
                ["factory.turn.prompt.text"] = Element("Summarize the changes"),
            },
            kind: "client",
            traceId: "0102030405060708090a0b0c0d0e0f10",
            parentSpanId: "1122334455667788",
            serviceName: "factory");

        CollectedRecord record = new DroidSpanMapper().MapSpan(span);

        Assert.Equal("aabbccddeeff0011", record.RecordId);
        Assert.Equal("droid", record.HarnessId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal(RecordKind.Client, record.Kind);
        Assert.Equal("factory.turn", record.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:00.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("sess-droid-1", record.SessionKey);
        Assert.Equal("1122334455667788", record.ParentRecordId);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", record.TraceId);
        Assert.Equal("droid-model-x", record.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:01.250Z", CultureInfo.InvariantCulture), record.EndedAt);
        Assert.Equal(1250L, record.ReportedDurationMs);

        Assert.Equal(4, record.Tokens.Count);
        Assert.Equal(97L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(15L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead).Count);
        Assert.Equal(8L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite).Count);
        Assert.Equal(40L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(120L, record.ReportedInputTokens);
        Assert.Equal(40L, record.ReportedOutputTokens);

        Assert.Null(record.ReportedCost);

        CollectedContentPart part = Assert.Single(record.Parts);
        Assert.Equal(ContentBucket.ConversationHistory, part.Bucket);
        Assert.Equal("Summarize the changes", part.PlainText);
        Assert.Equal("user", part.Role);

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
    public void DroidMapSpan_ToolSpan_MapsToolCallWithErrorOutcome()
    {
        OtlpSpan span = MakeSpan(
            "bbccddeeff001122",
            "factory.tool",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["factory.session.id"] = Element("sess-droid-2"),
                ["factory.tool.name"] = Element("Read"),
                ["factory.tool.call_id"] = Element("call-droid-1"),
                ["factory.tool.status"] = Element("error"),
                ["factory.tool.error_type"] = Element("FILE_NOT_FOUND"),
                ["error.message"] = Element("Read failed: file not found."),
                ["input"] = Element("{\"path\":\"/tmp/notes.txt\"}"),
            },
            kind: "client",
            statusCode: "unset");

        CollectedRecord record = new DroidSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.ToolInvoke, record.Op);
        Assert.Equal(RecordStatus.Error, record.Status);
        Assert.Equal("sess-droid-2", record.SessionKey);
        Assert.NotNull(record.Tool);
        Assert.Equal("call-droid-1", record.Tool.CallId);
        Assert.Equal("Read", record.Tool.ToolName);
        Assert.False(record.Tool.Succeeded);
        Assert.NotNull(record.Tool.Error);
        Assert.Equal("Read failed: file not found.", record.Tool.Error.Message);
        Assert.Equal("FILE_NOT_FOUND", record.Tool.Error.Code);
        Assert.NotNull(record.Tool.Input);
        Assert.Null(record.Agent);
        Assert.Equal("Read failed: file not found.", record.Signals.ErrorMessage);
    }

    [Fact]
    public void DroidMapSpan_SessionSpan_MapsAgentInvoke()
    {
        OtlpSpan span = MakeSpan(
            "ccddee0011223344",
            "factory.session",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["factory.session.id"] = Element("sess-droid-3"),
            },
            kind: "internal",
            statusCode: "unset");

        CollectedRecord record = new DroidSpanMapper().MapSpan(span);

        Assert.Equal("droid", record.HarnessId);
        Assert.Equal(RecordOp.AgentInvoke, record.Op);
        Assert.Equal(RecordKind.Internal, record.Kind);
        Assert.Equal(RecordStatus.Unset, record.Status);
        Assert.Equal("sess-droid-3", record.SessionKey);
        Assert.NotNull(record.Agent);
        Assert.Equal("factory.session", record.Agent.AgentName);
        Assert.Null(record.Agent.Succeeded);
        Assert.Null(record.Agent.Error);
        Assert.Null(record.Tool);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);
    }

    [Fact]
    public void DroidMapSpans_BatchMapsSessionsAndRejectsNegativeTokensIntoProblems()
    {
        OtlpSpan first = MakeTurnSpan("1111111111111111", "sess-batch-1", 40, 12);
        OtlpSpan second = MakeTurnSpan("2222222222222222", "sess-batch-1", 10, 6);
        OtlpSpan bad = MakeTurnSpan("3333333333333333", "sess-batch-1", 10, -3);

        CollectedBatch batch = new DroidSpanMapper().MapSpans([first, second, bad]);

        Assert.Equal("droid", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-batch-1", session.SessionKey);
        Assert.Equal("droid", session.HarnessId);

        Assert.Equal(2, batch.Records.Count);
        CollectedRecord firstRecord = Assert.Single(batch.Records, static r => r.RecordId == "1111111111111111");
        Assert.Equal(40L, firstRecord.ReportedInputTokens);
        Assert.Equal(12L, firstRecord.ReportedOutputTokens);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(CanonValidation.TokenNegativeClass, problem.Code);
        Assert.Equal("3333333333333333", problem.SpanId);
        Assert.Equal("droid", problem.HarnessId);
    }

    [Fact]
    public void DroidMapSpans_GeminiAttributedSpan_YieldsZeroRecords()
    {
        OtlpSpan gemini = MakeSpan(
            "eeeeeeeeeeeeeeee",
            "factory.turn",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["factory.session.id"] = Element("sess-gemini-1"),
                ["gen_ai.system"] = Element("gemini"),
                ["gen_ai.usage.input_tokens"] = Element(50),
                ["gen_ai.usage.output_tokens"] = Element(9),
            });

        CollectedBatch batch = new DroidSpanMapper().MapSpans([gemini]);

        Assert.Equal("droid", batch.Source.HarnessId);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void DroidMap_NativeSourceUnitOtlpJsonWithGeminiNamespace_YieldsZeroRecords()
    {
        string otlpJson = """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "service.name", "value": { "stringValue": "factory" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "scope": { "name": "factory.droid", "version": "1.0.0" },
                      "spans": [
                        {
                          "traceId": "0102030405060708090a0b0c0d0e0f10",
                          "spanId": "ffffffffffffffff",
                          "name": "factory.turn",
                          "kind": "SPAN_KIND_CLIENT",
                          "startTimeUnixNano": "1763550000000000000",
                          "endTimeUnixNano": "1763550000500000000",
                          "status": { "code": "STATUS_CODE_OK" },
                          "attributes": [
                            { "key": "factory.session.id", "value": { "stringValue": "sess-gemini-2" } },
                            { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "50" } },
                            { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "9" } },
                            { "key": "gemini.usage.thoughts_tokens", "value": { "intValue": "7" } }
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
            HarnessId: "droid",
            SourceKey: "http://127.0.0.1:4318/v1/traces",
            ProviderId: "factory",
            ParserId: "droid-otlp-mapper",
            ParserContractVersion: "1",
            Format: "otlp-json",
            SourceRootLabel: "otlp",
            RevisionToken: "rev-1",
            Kind: SourceKind.Otlp);
        NativeSourceUnit unit = new(descriptor, otlpJson, Encoding.UTF8.GetByteCount(otlpJson), ImportedAtUtc);

        CollectedBatch batch = new DroidSpanMapper().Map(unit, ImportedAtUtc);

        Assert.Equal("droid", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void PiMapSpan_LlmSpan_AssertsEveryCollectedRecordProperty()
    {
        OtlpSpan span = MakeSpan(
            "aabbccddeeff0011",
            "pi.response",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-pi-1"),
                ["gen_ai.request.model"] = Element("pi-model-9"),
                ["gen_ai.usage.input_tokens"] = Element(100),
                ["gen_ai.usage.cache_read.input_tokens"] = Element(20),
                ["gen_ai.usage.cache_creation.input_tokens"] = Element(10),
                ["gen_ai.usage.output_tokens"] = Element(30),
                ["gen_ai.usage.reasoning_tokens"] = Element(5),
                ["pi.tools.selected"] = Element("Bash"),
                ["pi.skills"] = Element("rust-dev"),
                ["pi.context_files"] = Element("AGENTS.md"),
            },
            kind: "client",
            traceId: "0102030405060708090a0b0c0d0e0f10",
            parentSpanId: "1122334455667788",
            serviceName: "pi");

        CollectedRecord record = new PiSpanMapper().MapSpan(span);

        Assert.Equal("aabbccddeeff0011", record.RecordId);
        Assert.Equal("pi", record.HarnessId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal(RecordKind.Client, record.Kind);
        Assert.Equal("pi.response", record.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:00.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("sess-pi-1", record.SessionKey);
        Assert.Equal("1122334455667788", record.ParentRecordId);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", record.TraceId);
        Assert.Equal("pi-model-9", record.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:01.250Z", CultureInfo.InvariantCulture), record.EndedAt);
        Assert.Equal(1250L, record.ReportedDurationMs);

        Assert.Equal(5, record.Tokens.Count);
        Assert.Equal(100L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(20L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead).Count);
        Assert.Equal(10L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite).Count);
        Assert.Equal(30L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(5L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Reasoning).Count);
        Assert.Equal(130L, record.ReportedInputTokens);
        Assert.Equal(30L, record.ReportedOutputTokens);

        Assert.Equal(3, record.Parts.Count);
        Assert.Equal(ContentBucket.ToolDefinitions, record.Parts[0].Bucket);
        Assert.Equal("Bash", record.Parts[0].PlainText);
        Assert.Equal(ContentBucket.InstructionContext, record.Parts[1].Bucket);
        Assert.Equal("rust-dev", record.Parts[1].PlainText);
        Assert.Equal(ContentBucket.InstructionContext, record.Parts[2].Bucket);
        Assert.Equal("AGENTS.md", record.Parts[2].PlainText);

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
    public void PiMapSpan_ToolSpan_MapsToolCall()
    {
        OtlpSpan span = MakeSpan(
            "bbccddeeff001122",
            "pi.tool",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-pi-2"),
                ["gen_ai.tool.name"] = Element("Bash"),
                ["gen_ai.tool.call.id"] = Element("pi-call-1"),
                ["gen_ai.tool.call.arguments"] = Element("{\"command\":\"ls\"}"),
                ["gen_ai.tool.status"] = Element("ok"),
            },
            kind: "client",
            statusCode: "unset");

        CollectedRecord record = new PiSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.ToolInvoke, record.Op);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("sess-pi-2", record.SessionKey);
        Assert.NotNull(record.Tool);
        Assert.Equal("pi-call-1", record.Tool.CallId);
        Assert.Equal("Bash", record.Tool.ToolName);
        Assert.Null(record.Tool.McpServerName);
        Assert.Null(record.Tool.McpToolName);
        Assert.True(record.Tool.Succeeded);
        Assert.Null(record.Tool.Error);
        Assert.NotNull(record.Tool.Input);
        Assert.Null(record.Agent);
    }

    [Fact]
    public void PiMapSpan_AgentSpan_MapsAgentInvoke()
    {
        OtlpSpan span = MakeSpan(
            "ccddee0011223344",
            "invoke_agent planner",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-pi-3"),
                ["gen_ai.operation.name"] = Element("invoke_agent"),
                ["gen_ai.agent.name"] = Element("planner"),
            },
            kind: "internal",
            statusCode: "unset",
            parentSpanId: "9999888877776666");

        CollectedRecord record = new PiSpanMapper().MapSpan(span);

        Assert.Equal("pi", record.HarnessId);
        Assert.Equal(RecordOp.AgentInvoke, record.Op);
        Assert.Equal(RecordKind.Internal, record.Kind);
        Assert.Equal("sess-pi-3", record.SessionKey);
        Assert.NotNull(record.Agent);
        Assert.Equal("planner", record.Agent.AgentName);
        Assert.Equal("9999888877776666", record.Agent.SpawningCallId);
        Assert.Null(record.Agent.Succeeded);
        Assert.Null(record.Tool);
    }

    [Fact]
    public void PiMapSpans_GeminiAttributedSpans_YieldZeroRecords()
    {
        OtlpSpan bySystem = MakeSpan(
            "dddddddddddddddd",
            "pi.response",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-gemini-3"),
                ["gen_ai.system"] = Element("gemini"),
                ["gen_ai.usage.input_tokens"] = Element(60),
                ["gen_ai.usage.output_tokens"] = Element(11),
            });
        OtlpSpan byNamespace = MakeSpan(
            "eeeeeeeeeeeeeeee",
            "pi.response",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-gemini-4"),
                ["gemini.usage.thoughts_tokens"] = Element(7),
                ["gen_ai.usage.input_tokens"] = Element(60),
                ["gen_ai.usage.output_tokens"] = Element(11),
            });

        CollectedBatch batch = new PiSpanMapper().MapSpans([bySystem, byNamespace]);

        Assert.Equal("pi", batch.Source.HarnessId);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Problems);
    }

    private static OtlpSpan MakeSpan(
        string spanId,
        string name,
        Dictionary<string, JsonElement> attributes,
        string kind = "client",
        string statusCode = "ok",
        string traceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string? parentSpanId = null,
        string serviceName = "otlp")
    {
        return new OtlpSpan(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            Name: name,
            Kind: kind,
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550001250000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 1250.4,
            Status: new OtlpStatus(statusCode),
            Attributes: attributes,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element(serviceName),
            },
            Scope: new OtlpScope("test.scope", "1.0.0"));
    }

    private static OtlpSpan MakeTurnSpan(string spanId, string sessionId, long inputTokens, long outputTokens)
    {
        return MakeSpan(
            spanId,
            "factory.turn",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["factory.session.id"] = Element(sessionId),
                ["gen_ai.usage.input_tokens"] = Element(inputTokens),
                ["gen_ai.usage.output_tokens"] = Element(outputTokens),
            });
    }

    private static JsonElement Element(object value)
    {
        return JsonSerializer.SerializeToElement(value);
    }
}
