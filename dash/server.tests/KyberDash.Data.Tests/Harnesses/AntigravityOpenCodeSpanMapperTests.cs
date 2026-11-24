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
/// Unit tests for <see cref="AntigravitySpanMapper"/> and <see cref="OpenCodeSpanMapper"/>
/// covering Antigravity OTLP model/tool/agent spans and OpenCode contract-3
/// <c>step</c>/<c>tool</c>/<c>request</c> spans: evidenced keys reach
/// <see cref="CollectedRecord"/> DTOs, token accounting follows each adapter's convention,
/// and unsupported OpenCode contract versions fall back to generic op resolution.
/// </summary>
public class AntigravityOpenCodeSpanMapperTests
{
    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HarnessId_IsAntigravity()
    {
        AntigravitySpanMapper mapper = new();
        Assert.Equal("antigravity", mapper.HarnessId);
    }

    [Fact]
    public void HarnessId_IsOpencode()
    {
        OpenCodeSpanMapper mapper = new();
        Assert.Equal("opencode", mapper.HarnessId);
    }

    [Fact]
    public void OpenCodeContract_RequiresVersion3AndDocumentsEvidencedKeys()
    {
        Assert.Equal("3", OpenCodeSpanMapper.SupportedContractVersion);
        Assert.Equal(CanonAdapters.OpencodeContractVersion, OpenCodeSpanMapper.SupportedContractVersion);
        Assert.Contains("opencode.span.kind", OpenCodeSpanMapper.SpanKindKeys);
        Assert.Contains("opencode.session.id", OpenCodeSpanMapper.SessionKeys);
        Assert.Contains("opencode.model.id", OpenCodeSpanMapper.ModelKeys);
        Assert.Contains("opencode.tool.name", OpenCodeSpanMapper.ToolNameKeys);
        Assert.Contains("opencode.cost", OpenCodeSpanMapper.CostKeys);
        Assert.Contains("opencode.usage.input", OpenCodeSpanMapper.InputKeys);
        Assert.Contains("opencode.usage.cache_read", OpenCodeSpanMapper.CacheReadKeys);
        Assert.Contains("opencode.usage.cache_write", OpenCodeSpanMapper.CacheWriteKeys);
        Assert.Contains("opencode.usage.output", OpenCodeSpanMapper.OutputKeys);
        Assert.Contains("opencode.usage.reasoning", OpenCodeSpanMapper.ReasoningKeys);
        Assert.Contains("opencode.prompt.text", OpenCodeSpanMapper.PromptTextKeys);
        Assert.Contains("opencode.context.system", OpenCodeSpanMapper.ContextSystemKeys);
        Assert.Contains("opencode.context.tools", OpenCodeSpanMapper.ContextToolsKeys);
    }

    [Fact]
    public void AntigravityMapSpan_ModelSpanWithGeminiThoughts_AssertsEveryCollectedRecordProperty()
    {
        OtlpSpan span = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "aabbccddeeff0011",
            ParentSpanId: "1122334455667788",
            Name: "antigravity.response",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550001250000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 1250.4,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-ag-1"),
                ["gen_ai.agent.name"] = Element("antigravity"),
                ["gen_ai.response.model"] = Element("gemini-2.5-pro"),
                ["gen_ai.usage.input_tokens"] = Element(100),
                ["gen_ai.usage.cache_read.input_tokens"] = Element(20),
                ["gen_ai.usage.output_tokens"] = Element(30),
                ["gemini.usage.thoughts_tokens"] = Element(5),
                ["gen_ai.usage.cost"] = Element(0.0125),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("antigravity"),
            },
            Scope: new OtlpScope("google.antigravity", "1.0.0"));

        AntigravitySpanMapper mapper = new();
        CollectedRecord record = mapper.MapSpan(span);

        Assert.Equal("aabbccddeeff0011", record.RecordId);
        Assert.Equal("antigravity", record.HarnessId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal(RecordKind.Client, record.Kind);
        Assert.Equal("antigravity.response", record.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:00.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("sess-ag-1", record.SessionKey);
        Assert.Equal("1122334455667788", record.ParentRecordId);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", record.TraceId);
        Assert.Equal("gemini-2.5-pro", record.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:01.250Z", CultureInfo.InvariantCulture), record.EndedAt);
        Assert.Equal(1250L, record.ReportedDurationMs);

        Assert.Equal(5, record.Tokens.Count);
        Assert.Equal(80L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(20L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead).Count);
        Assert.Equal(0L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite).Count);
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
    public void AntigravityMapSpan_ReasoningKeyBeatsThoughtTokens()
    {
        OtlpSpan span = AntigravitySpanWithUsage(
            "reasoning-beats-thoughts",
            null,
            "sess-ag-reason",
            100,
            30,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.usage.reasoning_tokens"] = Element(7),
                ["gemini.usage.thoughts_tokens"] = Element(5),
            });

        CollectedRecord record = new AntigravitySpanMapper().MapSpan(span);

        Assert.Equal(7L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Reasoning).Count);
        Assert.Equal(100L, record.ReportedInputTokens);
        Assert.Equal(30L, record.ReportedOutputTokens);
    }

    [Fact]
    public void AntigravityMapSpan_ToolSpan_MapsToolInvoke()
    {
        OtlpSpan span = new(
            TraceId: "22223333444455556666777788889999",
            SpanId: "tool-span-ag-1",
            ParentSpanId: "aabbccddeeff0011",
            Name: "antigravity.tool",
            Kind: "client",
            StartTimeUnixNano: "1763550002000000000",
            EndTimeUnixNano: "1763550002350000000",
            Timestamp: "2026-11-19T11:00:02.000Z",
            DurationMs: 350.0,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-ag-tool"),
                ["gen_ai.tool.name"] = Element("read_file"),
                ["gen_ai.tool.call.id"] = Element("call-ag-1"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("antigravity"),
            },
            Scope: new OtlpScope("google.antigravity", "1.0.0"));

        CollectedRecord record = new AntigravitySpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.ToolInvoke, record.Op);
        Assert.Equal("sess-ag-tool", record.SessionKey);
        Assert.NotNull(record.Tool);
        Assert.Equal("call-ag-1", record.Tool.CallId);
        Assert.Equal("read_file", record.Tool.ToolName);
        Assert.True(record.Tool.Succeeded);
        Assert.Null(record.Tool.Error);
        Assert.Null(record.Agent);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
    }

    [Fact]
    public void AntigravityMapSpan_InvokeAgentSpan_MapsAgentInvoke()
    {
        OtlpSpan span = new(
            TraceId: "33334444555566667777888899990000",
            SpanId: "agent-span-ag-1",
            ParentSpanId: "parent-ag-1",
            Name: "invoke_agent explore",
            Kind: "internal",
            StartTimeUnixNano: "1763550003000000000",
            EndTimeUnixNano: "1763550004000000000",
            Timestamp: "2026-11-19T11:00:03.000Z",
            DurationMs: 1000.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-ag-agent"),
                ["gen_ai.operation.name"] = Element("invoke_agent"),
                ["gen_ai.agent.name"] = Element("explore-agent"),
                ["gen_ai.agent.status"] = Element("ok"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("antigravity"),
            },
            Scope: new OtlpScope("google.antigravity", "1.0.0"));

        CollectedRecord record = new AntigravitySpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.AgentInvoke, record.Op);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.NotNull(record.Agent);
        Assert.Equal("explore-agent", record.Agent.AgentName);
        Assert.Equal("parent-ag-1", record.Agent.SpawningCallId);
        Assert.True(record.Agent.Succeeded);
        Assert.Null(record.Tool);
    }

    [Fact]
    public void AntigravityMapSpans_BatchMapsSessionsAndRejectsNegativeTokensIntoProblems()
    {
        OtlpSpan first = AntigravitySpanWithUsage("1111111111111111", null, "sess-ag-batch", 40, 12);
        OtlpSpan second = AntigravitySpanWithUsage("2222222222222222", "1111111111111111", "sess-ag-batch", 10, 6);
        OtlpSpan bad = AntigravitySpanWithUsage("3333333333333333", null, "sess-ag-batch", 10, -3);

        CollectedBatch batch = new AntigravitySpanMapper().MapSpans([first, second, bad]);

        Assert.Equal("antigravity", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-ag-batch", session.SessionKey);
        Assert.Equal("antigravity", session.HarnessId);

        Assert.Equal(2, batch.Records.Count);
        CollectedRecord firstRecord = Assert.Single(batch.Records, static r => r.RecordId == "1111111111111111");
        Assert.Equal(40L, firstRecord.ReportedInputTokens);
        Assert.Equal(12L, firstRecord.ReportedOutputTokens);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(CanonValidation.TokenNegativeClass, problem.Code);
        Assert.Equal("3333333333333333", problem.SpanId);
        Assert.Equal("antigravity", problem.HarnessId);
    }

    [Fact]
    public void AntigravityMap_MalformedOtlpPayload_EmitsMalformedOtlpProblem_AndValidBatchPersistsIntoRelationalStore()
    {
        SourceUnit descriptor = new(
            HarnessId: "antigravity",
            SourceKey: "http://127.0.0.1:4318/v1/traces",
            ProviderId: "antigravity",
            ParserId: "antigravity-otlp-mapper",
            ParserContractVersion: "1",
            Format: "otlp-json",
            SourceRootLabel: "otlp",
            RevisionToken: "rev-bad",
            Kind: SourceKind.Otlp);
        NativeSourceUnit malformedUnit = new(descriptor, "{not-valid-json", 15, ImportedAtUtc);

        AntigravitySpanMapper mapper = new();
        CollectedBatch errorBatch = mapper.Map(malformedUnit, ImportedAtUtc);
        Assert.Empty(errorBatch.Records);
        CollectedProblem problem = Assert.Single(errorBatch.Problems);
        Assert.Equal("MALFORMED_OTLP_PAYLOAD", problem.Code);
        Assert.Equal("antigravity", problem.HarnessId);

        OtlpSpan modelSpan = AntigravitySpanWithUsage("span-persist-model", null, "sess-ag-persist", 50, 15);
        CollectedBatch validBatch = mapper.MapSpans([modelSpan]);
        InMemoryRelationalStore store = new();
        store.WriteBatch(validBatch);

        Assert.Equal(1, store.CountRecords("antigravity"));
        Assert.NotNull(store.GetSession("sess-ag-persist"));
        Assert.NotNull(store.FindRecord("span-persist-model"));
    }

    [Fact]
    public void OpenCodeMapSpan_StepSpan_AssertsEveryCollectedRecordProperty()
    {
        OtlpSpan span = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "aabbccddeeff0022",
            ParentSpanId: "req-opencode-1",
            Name: "opencode.step",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550001250000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 1250.4,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Element("3"),
                ["opencode.span.kind"] = Element("step"),
                ["opencode.session.id"] = Element("sess-oc-1"),
                ["opencode.model.id"] = Element("opencode-test-model"),
                ["opencode.usage.input"] = Element(120),
                ["opencode.usage.cache_read"] = Element(10),
                ["opencode.usage.cache_write"] = Element(5),
                ["opencode.usage.output"] = Element(40),
                ["opencode.usage.reasoning"] = Element(6),
                ["opencode.cost"] = Element(0.02),
                ["opencode.prompt.text"] = Element("Summarize the changes"),
                ["opencode.context.system"] = Element("Be concise."),
                ["opencode.context.tools"] = Element("bash, read"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("opencode"),
            },
            Scope: new OtlpScope("opencode.plugin", "3.0.0"));

        OpenCodeSpanMapper mapper = new();
        CollectedRecord record = mapper.MapSpan(span);

        Assert.Equal("aabbccddeeff0022", record.RecordId);
        Assert.Equal("opencode", record.HarnessId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal(RecordKind.Client, record.Kind);
        Assert.Equal("opencode.step", record.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:00.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("sess-oc-1", record.SessionKey);
        Assert.Equal("req-opencode-1", record.ParentRecordId);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", record.TraceId);
        Assert.Equal("opencode-test-model", record.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:01.250Z", CultureInfo.InvariantCulture), record.EndedAt);
        Assert.Equal(1250L, record.ReportedDurationMs);

        Assert.Equal(5, record.Tokens.Count);
        Assert.Equal(120L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(10L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead).Count);
        Assert.Equal(5L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite).Count);
        Assert.Equal(46L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(6L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Reasoning).Count);
        Assert.Equal(135L, record.ReportedInputTokens);
        Assert.Equal(46L, record.ReportedOutputTokens);

        Assert.NotNull(record.ReportedCost);
        Assert.Equal(0.02, record.ReportedCost.Amount, precision: 6);
        Assert.Equal("USD", record.ReportedCost.Currency);

        Assert.Equal(3, record.Parts.Count);
        CollectedContentPart promptPart = record.Parts[0];
        Assert.Equal(0, promptPart.Position);
        Assert.Equal(ContentBucket.ConversationHistory, promptPart.Bucket);
        Assert.Equal("Summarize the changes", promptPart.PlainText);
        Assert.Null(promptPart.Withheld);

        CollectedContentPart systemPart = record.Parts[1];
        Assert.Equal(1, systemPart.Position);
        Assert.Equal(ContentBucket.SystemPrompt, systemPart.Bucket);
        Assert.Equal("Be concise.", systemPart.PlainText);
        Assert.Null(systemPart.Withheld);

        CollectedContentPart toolsPart = record.Parts[2];
        Assert.Equal(2, toolsPart.Position);
        Assert.Equal(ContentBucket.ToolDefinitions, toolsPart.Bucket);
        Assert.Equal("bash, read", toolsPart.PlainText);
        Assert.Null(toolsPart.Withheld);

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
    public void OpenCodeMapSpan_ToolSpan_MapsToolInvokeWithPluginToolName()
    {
        OtlpSpan span = new(
            TraceId: "44445555666677778888999900001111",
            SpanId: "tool-span-oc-1",
            ParentSpanId: "req-opencode-2",
            Name: "opencode.tool",
            Kind: "client",
            StartTimeUnixNano: "1763550002000000000",
            EndTimeUnixNano: "1763550002350000000",
            Timestamp: "2026-11-19T11:00:02.000Z",
            DurationMs: 350.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Element("3"),
                ["opencode.span.kind"] = Element("tool"),
                ["opencode.session.id"] = Element("sess-oc-tool"),
                ["opencode.tool.name"] = Element("bash"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("opencode"),
            },
            Scope: new OtlpScope("opencode.plugin", "3.0.0"));

        CollectedRecord record = new OpenCodeSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.ToolInvoke, record.Op);
        Assert.Equal("sess-oc-tool", record.SessionKey);
        Assert.NotNull(record.Tool);
        Assert.Equal("tool-span-oc-1", record.Tool.CallId);
        Assert.Equal("bash", record.Tool.ToolName);
        Assert.Null(record.Tool.Succeeded);
        Assert.Null(record.Tool.Error);
        Assert.Null(record.Agent);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedCost);
    }

    [Fact]
    public void OpenCodeMapSpan_RequestSpan_MapsAgentInvoke()
    {
        OtlpSpan span = new(
            TraceId: "55556666777788889999000011112222",
            SpanId: "req-opencode-3",
            ParentSpanId: null,
            Name: "opencode.request",
            Kind: "server",
            StartTimeUnixNano: "1763550003000000000",
            EndTimeUnixNano: "1763550004000000000",
            Timestamp: "2026-11-19T11:00:03.000Z",
            DurationMs: 1000.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Element("3"),
                ["opencode.span.kind"] = Element("request"),
                ["opencode.session.id"] = Element("sess-oc-request"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("opencode"),
            },
            Scope: new OtlpScope("opencode.plugin", "3.0.0"));

        CollectedRecord record = new OpenCodeSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.AgentInvoke, record.Op);
        Assert.Equal("sess-oc-request", record.SessionKey);
        Assert.NotNull(record.Agent);
        Assert.Equal("opencode.request", record.Agent.AgentName);
        Assert.Null(record.Agent.Succeeded);
        Assert.Null(record.Tool);
        Assert.Empty(record.Tokens);
    }

    [Fact]
    public void OpenCodeMapSpan_UnsupportedContractVersion_FallsBackToGenericOpWithoutUsageTokens()
    {
        OtlpSpan span = new(
            TraceId: "66667777888899990000111122223333",
            SpanId: "legacy-oc-1",
            ParentSpanId: null,
            Name: "opencode.step",
            Kind: "client",
            StartTimeUnixNano: "1763550005000000000",
            EndTimeUnixNano: "1763550005500000000",
            Timestamp: "2026-11-19T11:00:05.000Z",
            DurationMs: 500.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Element("2"),
                ["opencode.span.kind"] = Element("step"),
                ["opencode.session.id"] = Element("sess-oc-legacy"),
                ["opencode.model.id"] = Element("opencode-test-model"),
                ["opencode.usage.input"] = Element(50),
                ["opencode.usage.output"] = Element(10),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("opencode"),
            },
            Scope: new OtlpScope("opencode.plugin", "2.0.0"));

        CollectedRecord record = new OpenCodeSpanMapper().MapSpan(span);

        Assert.Equal(RecordOp.Unspecified, record.Op);
        Assert.Equal("sess-oc-legacy", record.SessionKey);
        Assert.Equal("opencode-test-model", record.Model);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);
        Assert.Null(record.Tool);
        Assert.Null(record.Agent);
    }

    [Fact]
    public void OpenCodeMapSpan_StepSpanWithOverflowingOutput_FoldsToZeroWithoutReasoningRow()
    {
        OtlpSpan span = OpenCodeStepSpanWithUsage(
            "overflow-oc-1",
            "sess-oc-overflow",
            50,
            long.MaxValue,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.usage.reasoning"] = Element(1),
            });

        CollectedRecord record = new OpenCodeSpanMapper().MapSpan(span);

        Assert.Equal(4, record.Tokens.Count);
        Assert.Equal(50L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(0L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.DoesNotContain(record.Tokens, static t => t.TokenClass == TokenClass.Reasoning);
        Assert.Equal(50L, record.ReportedInputTokens);
        Assert.Equal(0L, record.ReportedOutputTokens);
    }

    [Fact]
    public void OpenCodeMapSpans_BatchMapsSessionsAndRejectsNegativeTokensIntoProblems()
    {
        OtlpSpan step = OpenCodeStepSpanWithUsage("aaaaaaaaaaaaaaaa", "sess-oc-batch", 40, 12);
        OtlpSpan tool = OpenCodeToolSpan("bbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaa", "sess-oc-batch", "bash");
        OtlpSpan bad = OpenCodeStepSpanWithUsage("cccccccccccccccc", "sess-oc-batch", -1, 4);

        CollectedBatch batch = new OpenCodeSpanMapper().MapSpans([step, tool, bad]);

        Assert.Equal("opencode", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-oc-batch", session.SessionKey);
        Assert.Equal("opencode", session.HarnessId);

        Assert.Equal(2, batch.Records.Count);
        CollectedRecord stepRecord = Assert.Single(batch.Records, static r => r.RecordId == "aaaaaaaaaaaaaaaa");
        Assert.Equal(RecordOp.LlmInvoke, stepRecord.Op);
        Assert.Equal(40L, stepRecord.ReportedInputTokens);
        Assert.Equal(12L, stepRecord.ReportedOutputTokens);
        Assert.Equal(RecordOp.ToolInvoke, Assert.Single(batch.Records, static r => r.RecordId == "bbbbbbbbbbbbbbbb").Op);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(CanonValidation.TokenNegativeFresh, problem.Code);
        Assert.Equal("cccccccccccccccc", problem.SpanId);
        Assert.Equal("opencode", problem.HarnessId);
    }

    [Fact]
    public void OpenCodeMap_NativeSourceUnitOtlpJson_MapsSessionAndRecord()
    {
        string otlpJson = """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "service.name", "value": { "stringValue": "opencode" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "scope": { "name": "opencode.plugin", "version": "3.0.0" },
                      "spans": [
                        {
                          "traceId": "0102030405060708090a0b0c0d0e0f10",
                          "spanId": "4444444444444444",
                          "name": "opencode.step",
                          "kind": "SPAN_KIND_CLIENT",
                          "startTimeUnixNano": "1763550000000000000",
                          "endTimeUnixNano": "1763550000500000000",
                          "status": { "code": "STATUS_CODE_OK" },
                          "attributes": [
                            { "key": "opencode.contract_version", "value": { "stringValue": "3" } },
                            { "key": "opencode.span.kind", "value": { "stringValue": "step" } },
                            { "key": "opencode.session.id", "value": { "stringValue": "sess-oc-json" } },
                            { "key": "opencode.model.id", "value": { "stringValue": "opencode-test-model" } },
                            { "key": "opencode.usage.input", "value": { "intValue": "40" } },
                            { "key": "opencode.usage.output", "value": { "intValue": "12" } }
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
            HarnessId: "opencode",
            SourceKey: "http://127.0.0.1:4318/v1/traces",
            ProviderId: "opencode",
            ParserId: "opencode-otlp-mapper",
            ParserContractVersion: "1",
            Format: "otlp-json",
            SourceRootLabel: "otlp",
            RevisionToken: "rev-1",
            Kind: SourceKind.Otlp);
        NativeSourceUnit unit = new(descriptor, otlpJson, Encoding.UTF8.GetByteCount(otlpJson), ImportedAtUtc);

        CollectedBatch batch = new OpenCodeSpanMapper().Map(unit, ImportedAtUtc);

        Assert.Equal("opencode", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);
        Assert.Equal("http://127.0.0.1:4318/v1/traces", batch.Source.Locator);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-oc-json", session.SessionKey);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("4444444444444444", record.RecordId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal("opencode-test-model", record.Model);
        Assert.Equal(40L, record.ReportedInputTokens);
        Assert.Equal(12L, record.ReportedOutputTokens);
        Assert.Empty(batch.Problems);
    }

    private static OtlpSpan AntigravitySpanWithUsage(
        string spanId,
        string? parentSpanId,
        string sessionId,
        long inputTokens,
        long outputTokens,
        Dictionary<string, JsonElement>? extraAttributes = null)
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["gen_ai.session.id"] = Element(sessionId),
            ["gen_ai.agent.name"] = Element("antigravity"),
            ["gen_ai.response.model"] = Element("gemini-2.5-pro"),
            ["gen_ai.usage.input_tokens"] = Element(inputTokens),
            ["gen_ai.usage.output_tokens"] = Element(outputTokens),
        };

        if (extraAttributes is not null)
        {
            foreach (KeyValuePair<string, JsonElement> entry in extraAttributes)
            {
                attributes[entry.Key] = entry.Value;
            }
        }

        return new OtlpSpan(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            Name: "antigravity.response",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000500000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 500.0,
            Status: new OtlpStatus("ok"),
            Attributes: attributes,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("antigravity"),
            },
            Scope: new OtlpScope("google.antigravity", "1.0.0"));
    }

    private static OtlpSpan OpenCodeStepSpanWithUsage(
        string spanId,
        string sessionId,
        long inputTokens,
        long outputTokens,
        Dictionary<string, JsonElement>? extraAttributes = null)
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["opencode.contract_version"] = Element("3"),
            ["opencode.span.kind"] = Element("step"),
            ["opencode.session.id"] = Element(sessionId),
            ["opencode.usage.input"] = Element(inputTokens),
            ["opencode.usage.output"] = Element(outputTokens),
        };

        if (extraAttributes is not null)
        {
            foreach (KeyValuePair<string, JsonElement> entry in extraAttributes)
            {
                attributes[entry.Key] = entry.Value;
            }
        }

        return new OtlpSpan(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: spanId,
            ParentSpanId: null,
            Name: "opencode.step",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000500000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 500.0,
            Status: new OtlpStatus("ok"),
            Attributes: attributes,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("opencode"),
            },
            Scope: new OtlpScope("opencode.plugin", "3.0.0"));
    }

    private static OtlpSpan OpenCodeToolSpan(
        string spanId,
        string? parentSpanId,
        string sessionId,
        string toolName)
    {
        return new OtlpSpan(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            Name: "opencode.tool",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000500000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 500.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Element("3"),
                ["opencode.span.kind"] = Element("tool"),
                ["opencode.session.id"] = Element(sessionId),
                ["opencode.tool.name"] = Element(toolName),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("opencode"),
            },
            Scope: new OtlpScope("opencode.plugin", "3.0.0"));
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
}
