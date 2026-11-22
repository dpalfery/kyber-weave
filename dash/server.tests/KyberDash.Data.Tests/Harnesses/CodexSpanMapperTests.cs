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
/// Unit tests for <see cref="CodexSpanMapper"/> covering Codex OTLP model spans under the
/// <c>CodexAdapter</c> allowlist: only allowlisted attribute keys reach
/// <see cref="CollectedRecord"/> DTOs, unallowlisted attributes are dropped, and token
/// accounting follows the cache-inclusive convention.
/// </summary>
public class CodexSpanMapperTests
{
    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HarnessId_IsCodexCli()
    {
        CodexSpanMapper mapper = new();
        Assert.Equal("codex-cli", mapper.HarnessId);
    }

    [Fact]
    public void Allowlist_KeepsCodexAdapterKeys()
    {
        Assert.Contains("model", CodexSpanMapper.AllowedKeys);
        Assert.Contains("slug", CodexSpanMapper.AllowedKeys);
        Assert.Contains("conversation.id", CodexSpanMapper.AllowedKeys);
        Assert.Contains("gen_ai.session.id", CodexSpanMapper.AllowedKeys);
        Assert.Contains("session.id", CodexSpanMapper.AllowedKeys);
        Assert.Contains("event.timestamp", CodexSpanMapper.AllowedKeys);
        Assert.Contains("gen_ai.usage.", CodexSpanMapper.AllowedPrefixes);
        Assert.Contains("codex.usage.", CodexSpanMapper.AllowedPrefixes);
    }

    [Fact]
    public void MapSpan_ModelSpanWithUsage_AssertsEveryCollectedRecordProperty()
    {
        OtlpSpan span = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "aabbccddeeff0011",
            ParentSpanId: "1122334455667788",
            Name: "codex.response",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550001250000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 1250.4,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["conversation.id"] = Element("conv-codex-1"),
                ["gen_ai.session.id"] = Element("sess-codex-ignored"),
                ["model"] = Element("gpt-5.5"),
                ["gen_ai.response.model"] = Element("gpt-ignored"),
                ["gen_ai.usage.input_tokens"] = Element(100),
                ["input_tokens"] = Element(999),
                ["gen_ai.usage.cache_read.input_tokens"] = Element(20),
                ["gen_ai.usage.cache_write.input_tokens"] = Element(10),
                ["gen_ai.usage.output_tokens"] = Element(30),
                ["codex.usage.reasoning_output_tokens"] = Element(5),
                ["gen_ai.usage.cost"] = Element(0.0125),
                ["event.timestamp"] = Element("2026-11-19T11:00:00.000Z"),
                ["slug"] = Element("codex-slug-1"),
                ["user.email"] = Element("someone@example.com"),
                ["gen_ai.prompt"] = Element("Summarize changes"),
                ["gen_ai.tool.name"] = Element("Bash"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("codex"),
            },
            Scope: new OtlpScope("openai.codex", "1.0.0"));

        CodexSpanMapper mapper = new();
        CollectedRecord record = mapper.MapSpan(span);

        Assert.Equal("aabbccddeeff0011", record.RecordId);
        Assert.Equal("codex-cli", record.HarnessId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal(RecordKind.Client, record.Kind);
        Assert.Equal("codex.response", record.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:00.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("conv-codex-1", record.SessionKey);
        Assert.Equal("1122334455667788", record.ParentRecordId);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", record.TraceId);
        Assert.Equal("gpt-5.5", record.Model);
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

        Assert.Empty(record.Parts);
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
    public void MapSpan_UnallowlistedAttributesAreDroppedFromNative()
    {
        OtlpSpan span = new(
            TraceId: "aaaa",
            SpanId: "bbbb",
            ParentSpanId: null,
            Name: "codex.response",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000100000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 100.0,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["conversation.id"] = Element("conv-codex-drop"),
                ["gen_ai.usage.input_tokens"] = Element(12),
                ["gen_ai.usage.output_tokens"] = Element(4),
                ["gen_ai.response.model"] = Element("gpt-dropped"),
                ["input_tokens"] = Element(777),
                ["user.email"] = Element("someone@example.com"),
                ["gen_ai.prompt"] = Element("Summarize changes"),
                ["gen_ai.tool.name"] = Element("Bash"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            Scope: new OtlpScope("openai.codex", null));

        CodexSpanMapper mapper = new();
        OtlpSpanNative native = mapper.Parse(span);

        Assert.All(
            native.Attributes.Keys,
            static key => Assert.True(CodexSpanMapper.IsAllowlistedKey(key), $"Attribute '{key}' must be allowlisted."));
        Assert.DoesNotContain("gen_ai.response.model", native.Attributes.Keys);
        Assert.DoesNotContain("input_tokens", native.Attributes.Keys);
        Assert.DoesNotContain("user.email", native.Attributes.Keys);
        Assert.DoesNotContain("gen_ai.prompt", native.Attributes.Keys);
        Assert.DoesNotContain("gen_ai.tool.name", native.Attributes.Keys);

        CollectedRecord record = mapper.MapSpan(span);
        Assert.Null(record.Model);
        Assert.Equal(12L, record.ReportedInputTokens);
        Assert.Equal(4L, record.ReportedOutputTokens);
        Assert.Empty(record.Parts);
        Assert.Null(record.Tool);
    }

    [Fact]
    public void MapSpan_SpanWithoutUsage_MapsUnspecifiedOpWithAllowlistedSession()
    {
        OtlpSpan span = new(
            TraceId: "cccc",
            SpanId: "dddd",
            ParentSpanId: null,
            Name: "codex.session.start",
            Kind: "internal",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000000000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 0.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-codex-fallback"),
                ["model"] = Element("gpt-5.5"),
                ["event.timestamp"] = Element("2026-11-19T11:00:00.000Z"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("codex"),
            },
            Scope: new OtlpScope("openai.codex", null));

        CollectedRecord record = new CodexSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.Unspecified, record.Op);
        Assert.Equal(RecordKind.Internal, record.Kind);
        Assert.Equal("sess-codex-fallback", record.SessionKey);
        Assert.Equal("gpt-5.5", record.Model);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);
    }

    [Fact]
    public void MapSpan_ErrorStatus_MapsStatusMessageWhileDroppingUnallowlistedErrorDetail()
    {
        OtlpSpan span = new(
            TraceId: "eeee",
            SpanId: "ffff",
            ParentSpanId: null,
            Name: "codex.response",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000100000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 100.0,
            Status: new OtlpStatus("error", "Request failed"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["conversation.id"] = Element("conv-codex-err"),
                ["error.message"] = Element("secret detail"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            Scope: new OtlpScope("openai.codex", null));

        CollectedRecord record = new CodexSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.Unspecified, record.Op);
        Assert.Equal(RecordStatus.Error, record.Status);
        Assert.Equal("Request failed", record.Signals.ErrorMessage);
    }

    [Fact]
    public void MapSpans_BatchMapsSessionsAndRejectsNegativeTokensIntoProblems()
    {
        OtlpSpan first = SpanWithUsage("1111111111111111", null, "conv-batch-1", 40, 12);
        OtlpSpan second = SpanWithUsage("2222222222222222", "1111111111111111", "conv-batch-1", 10, 6);
        OtlpSpan bad = SpanWithUsage("3333333333333333", null, "conv-batch-1", 10, -3);

        CollectedBatch batch = new CodexSpanMapper().MapSpans([first, second, bad]);

        Assert.Equal("codex-cli", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("conv-batch-1", session.SessionKey);
        Assert.Equal("codex-cli", session.HarnessId);

        Assert.Equal(2, batch.Records.Count);
        CollectedRecord firstRecord = Assert.Single(batch.Records, static r => r.RecordId == "1111111111111111");
        Assert.Equal(40L, firstRecord.ReportedInputTokens);
        Assert.Equal(12L, firstRecord.ReportedOutputTokens);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(CanonValidation.TokenNegativeClass, problem.Code);
        Assert.Equal("3333333333333333", problem.SpanId);
        Assert.Equal("codex-cli", problem.HarnessId);
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
                      { "key": "service.name", "value": { "stringValue": "codex" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "scope": { "name": "openai.codex", "version": "1.0.0" },
                      "spans": [
                        {
                          "traceId": "0102030405060708090a0b0c0d0e0f10",
                          "spanId": "4444444444444444",
                          "name": "codex.response",
                          "kind": "SPAN_KIND_CLIENT",
                          "startTimeUnixNano": "1763550000000000000",
                          "endTimeUnixNano": "1763550000500000000",
                          "status": { "code": "STATUS_CODE_OK" },
                          "attributes": [
                            { "key": "conversation.id", "value": { "stringValue": "conv-json-1" } },
                            { "key": "model", "value": { "stringValue": "gpt-5.5" } },
                            { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "40" } },
                            { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "12" } },
                            { "key": "user.email", "value": { "stringValue": "someone@example.com" } }
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
            HarnessId: "codex-cli",
            SourceKey: "http://127.0.0.1:4318/v1/traces",
            ProviderId: "codex",
            ParserId: "codex-otlp-mapper",
            ParserContractVersion: "1",
            Format: "otlp-json",
            SourceRootLabel: "otlp",
            RevisionToken: "rev-1",
            Kind: SourceKind.Otlp);
        NativeSourceUnit unit = new(descriptor, otlpJson, Encoding.UTF8.GetByteCount(otlpJson), ImportedAtUtc);

        CollectedBatch batch = new CodexSpanMapper().Map(unit, ImportedAtUtc);

        Assert.Equal("codex-cli", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);
        Assert.Equal("http://127.0.0.1:4318/v1/traces", batch.Source.Locator);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("conv-json-1", session.SessionKey);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("4444444444444444", record.RecordId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal("gpt-5.5", record.Model);
        Assert.Equal(40L, record.ReportedInputTokens);
        Assert.Equal(12L, record.ReportedOutputTokens);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_MalformedOtlpPayload_EmitsMalformedOtlpProblem_AndValidBatchPersistsIntoRelationalStore()
    {
        SourceUnit descriptor = new(
            HarnessId: "codex-cli",
            SourceKey: "http://127.0.0.1:4318/v1/traces",
            ProviderId: "codex",
            ParserId: "codex-otlp-mapper",
            ParserContractVersion: "1",
            Format: "otlp-json",
            SourceRootLabel: "otlp",
            RevisionToken: "rev-bad",
            Kind: SourceKind.Otlp);
        NativeSourceUnit malformedUnit = new(descriptor, "{not-valid-json", 15, ImportedAtUtc);

        CodexSpanMapper mapper = new();
        CollectedBatch errorBatch = mapper.Map(malformedUnit, ImportedAtUtc);
        Assert.Empty(errorBatch.Records);
        CollectedProblem problem = Assert.Single(errorBatch.Problems);
        Assert.Equal("MALFORMED_OTLP_PAYLOAD", problem.Code);
        Assert.Equal("codex-cli", problem.HarnessId);

        OtlpSpan modelSpan = SpanWithUsage("span-persist-model", null, "conv-persist-1", 50, 15);
        CollectedBatch validBatch = mapper.MapSpans([modelSpan]);
        InMemoryRelationalStore store = new();
        store.WriteBatch(validBatch);

        Assert.Equal(1, store.CountRecords("codex-cli"));
        Assert.NotNull(store.GetSession("conv-persist-1"));
        Assert.NotNull(store.GetRecord("span-persist-model"));
    }

    private static OtlpSpan SpanWithUsage(
        string spanId,
        string? parentSpanId,
        string conversationId,
        long inputTokens,
        long outputTokens)
    {
        return new OtlpSpan(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            Name: "codex.response",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000500000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 500.0,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["conversation.id"] = Element(conversationId),
                ["model"] = Element("gpt-5.5"),
                ["gen_ai.usage.input_tokens"] = Element(inputTokens),
                ["gen_ai.usage.output_tokens"] = Element(outputTokens),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("codex"),
            },
            Scope: new OtlpScope("openai.codex", "1.0.0"));
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
}
