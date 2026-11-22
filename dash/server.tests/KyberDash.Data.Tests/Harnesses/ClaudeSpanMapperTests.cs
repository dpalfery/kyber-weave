using System.Globalization;
using System.Security.Cryptography;
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
/// Unit tests for <see cref="OtlpSpanNative"/> and <see cref="ClaudeSpanMapper"/> covering
/// Claude Code OTLP model, tool, and <c>invoke_agent</c> spans with <c>client</c> and
/// <c>internal</c> kinds, exclusive token accounting, and full <see cref="CollectedRecord"/> invariants.
/// </summary>
public class ClaudeSpanMapperTests
{
    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HarnessId_IsClaudeCode()
    {
        ClaudeSpanMapper mapper = new();
        Assert.Equal("claude-code", mapper.HarnessId);
    }

    [Fact]
    public void MapSpan_ModelSpanWithClientKind_AssertsEveryCollectedRecordProperty()
    {
        OtlpSpan span = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "aabbccddeeff0011",
            ParentSpanId: "1122334455667788",
            Name: "claude_code.llm.request",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550001250000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 1250.4,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude_code.session.id"] = Element("unused-ignored"),
                ["gen_ai.session.id"] = Element("sess-claude-model-1"),
                ["gen_ai.request.model"] = Element("claude-sonnet-4-5"),
                ["gen_ai.response.model"] = Element("claude-sonnet-4-6"),
                ["gen_ai.usage.input_tokens"] = Element(120),
                ["gen_ai.usage.cache_read.input_tokens"] = Element(400),
                ["gen_ai.usage.cache_creation.input_tokens"] = Element(80),
                ["gen_ai.usage.output_tokens"] = Element(65),
                ["gen_ai.usage.reasoning_tokens"] = Element(15),
                ["gen_ai.usage.cost"] = Element(0.0125),
                ["gen_ai.request.max_context_tokens"] = Element(200000),
                ["gen_ai.agent.id"] = Element("agent-inst-1"),
                ["gen_ai.skill.name"] = Element("bug-crusher"),
                ["gen_ai.system_instructions"] = Element("Be concise and accurate."),
                ["gen_ai.input.messages"] = Element("""[{"role":"user","parts":[{"type":"text","text":"Explain this diff."}]}]"""),
                ["gen_ai.usage.sys_tokens"] = Element(12),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("claude-code"),
            },
            Scope: new OtlpScope("com.anthropic.claude_code", "2.1.0"));

        ClaudeSpanMapper mapper = new();
        CollectedRecord record = mapper.MapSpan(span);

        Assert.Equal("aabbccddeeff0011", record.RecordId);
        Assert.Equal("claude-code", record.HarnessId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal(RecordKind.Client, record.Kind);
        Assert.Equal("claude_code.llm.request", record.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:00.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("sess-claude-model-1", record.SessionKey);
        Assert.Equal("1122334455667788", record.ParentRecordId);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", record.TraceId);
        Assert.Equal("claude-sonnet-4-6", record.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:01.250Z", CultureInfo.InvariantCulture), record.EndedAt);
        Assert.Equal(1250L, record.ReportedDurationMs);

        Assert.Equal(5, record.Tokens.Count);
        Assert.Equal(120L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(400L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead).Count);
        Assert.Equal(80L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite).Count);
        Assert.Equal(65L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(15L, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Reasoning).Count);
        Assert.Equal(600L, record.ReportedInputTokens);
        Assert.Equal(65L, record.ReportedOutputTokens);

        Assert.NotNull(record.ReportedCost);
        Assert.Equal(0.0125, record.ReportedCost.Amount, precision: 6);
        Assert.Equal("USD", record.ReportedCost.Currency);

        Assert.Equal(2, record.Parts.Count);
        CollectedContentPart systemPart = record.Parts[0];
        Assert.Equal(0, systemPart.Position);
        Assert.Equal(ContentBucket.SystemPrompt, systemPart.Bucket);
        Assert.Equal("Be concise and accurate.", systemPart.PlainText);
        Assert.Null(systemPart.Withheld);
        Assert.Equal(12L, systemPart.ReportedTokens);

        CollectedContentPart messagePart = record.Parts[1];
        Assert.Equal(1, messagePart.Position);
        Assert.Equal(ContentBucket.ConversationHistory, messagePart.Bucket);
        Assert.Equal("Explain this diff.", messagePart.PlainText);
        Assert.Null(messagePart.Withheld);

        Assert.Null(record.Tool);
        Assert.Null(record.Agent);
        Assert.Equal(["bug-crusher"], record.SkillsInvoked);
        Assert.Equal("agent-inst-1", record.AgentInstanceId);
        Assert.Empty(record.ToolDefinitions);
        Assert.Null(record.Signals.ErrorMessage);
        Assert.Null(record.Signals.EditFailures);
        Assert.Null(record.Signals.CorrectionRule);
        Assert.Equal(200000L, record.Signals.DeclaredContextWindow);
    }

    [Fact]
    public void MapSpan_ToolSpanWithInternalKindAndMcpTool_AssertsEveryCollectedRecordAndToolProperty()
    {
        string rawArgs = """{"query":"ClaudeCodeAdapter","limit":5}""";
        string resultText = "Found 1 matching adapter symbol.";

        OtlpSpan span = new(
            TraceId: "11112222333344445555666677778888",
            SpanId: "deadbeef00000001",
            ParentSpanId: "aabbccddeeff0011",
            Name: "execute_tool mcp__codegraph__codegraph_explore",
            Kind: "internal",
            StartTimeUnixNano: "1763550002000000000",
            EndTimeUnixNano: "1763550002350000000",
            Timestamp: "2026-11-19T11:00:02.000Z",
            DurationMs: 350.0,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["span.type"] = Element("tool"),
                ["tool_name"] = Element("mcp__codegraph__codegraph_explore"),
                ["gen_ai.tool.call.id"] = Element("toolu_01ABC"),
                ["gen_ai.tool.call.arguments"] = Element(rawArgs),
                ["gen_ai.tool.call.result"] = Element(resultText),
                ["session.id"] = Element("sess-claude-tool-1"),
                ["gen_ai.tool.exit_code"] = Element(0),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("claude-code"),
            },
            Scope: new OtlpScope("com.anthropic.claude_code", "2.1.0"));

        ClaudeSpanMapper mapper = new();
        CollectedRecord record = mapper.MapSpan(span);

        Assert.Equal("deadbeef00000001", record.RecordId);
        Assert.Equal("claude-code", record.HarnessId);
        Assert.Equal(RecordOp.ToolInvoke, record.Op);
        Assert.Equal(RecordKind.Internal, record.Kind);
        Assert.Equal("execute_tool mcp__codegraph__codegraph_explore", record.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:02.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("sess-claude-tool-1", record.SessionKey);
        Assert.Equal("aabbccddeeff0011", record.ParentRecordId);
        Assert.Equal("11112222333344445555666677778888", record.TraceId);
        Assert.Null(record.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:02.350Z", CultureInfo.InvariantCulture), record.EndedAt);
        Assert.Equal(350L, record.ReportedDurationMs);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);
        Assert.Null(record.ReportedCost);

        CollectedContentPart resultPart = Assert.Single(record.Parts);
        Assert.Equal(0, resultPart.Position);
        Assert.Equal(ContentBucket.ToolResultContent, resultPart.Bucket);
        Assert.Equal(resultText, resultPart.PlainText);
        Assert.Null(resultPart.Withheld);

        Assert.NotNull(record.Tool);
        Assert.Equal("toolu_01ABC", record.Tool.CallId);
        Assert.Equal("mcp__codegraph__codegraph_explore", record.Tool.ToolName);
        Assert.Equal("codegraph", record.Tool.McpServerName);
        Assert.Equal("codegraph_explore", record.Tool.McpToolName);
        Assert.True(record.Tool.Succeeded);
        Assert.Null(record.Tool.Error);
        Assert.Equal(0L, record.Tool.ExitCode);

        Assert.NotNull(record.Tool.Input);
        Assert.Null(record.Tool.Input.PlainText);
        Assert.Equal(Encoding.UTF8.GetByteCount(rawArgs), record.Tool.Input.Utf8Bytes);
        Assert.Equal(rawArgs.Length, record.Tool.Input.Characters);
        Assert.Equal(Sha256Hex("""{"limit":5,"query":"ClaudeCodeAdapter"}"""), record.Tool.Input.Sha256Fingerprint);
        Assert.Null(record.Tool.Input.OriginalUtf8BytesWhenTruncated);

        Assert.NotNull(record.Tool.Result);
        Assert.Equal(resultText, record.Tool.Result.PlainText);
        Assert.Equal(Encoding.UTF8.GetByteCount(resultText), record.Tool.Result.Utf8Bytes);
        Assert.Equal(resultText.Length, record.Tool.Result.Characters);
        Assert.Null(record.Tool.Result.Sha256Fingerprint);
        Assert.Null(record.Tool.Result.OriginalUtf8BytesWhenTruncated);

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
    public void MapSpan_ToolSpanWithErrorStatus_MapsToolErrorAndSignals()
    {
        OtlpSpan span = new(
            TraceId: "11112222333344445555666677778888",
            SpanId: "deadbeef00000002",
            ParentSpanId: "aabbccddeeff0011",
            Name: "Bash",
            Kind: "client",
            StartTimeUnixNano: "1763550003000000000",
            EndTimeUnixNano: "1763550003100000000",
            Timestamp: "2026-11-19T11:00:03.000Z",
            DurationMs: 100.0,
            Status: new OtlpStatus("error", "Command exited with non-zero status"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.tool.name"] = Element("Bash"),
                ["gen_ai.tool.call.id"] = Element("toolu_02ERR"),
                ["gen_ai.tool.arguments"] = Element("git status --porcelain"),
                ["gen_ai.tool.result"] = Element("fatal: not a git repository"),
                ["gen_ai.tool.status"] = Element("error"),
                ["gen_ai.tool.error.code"] = Element("EXIT_NON_ZERO"),
                ["tool.exit_code"] = Element(128),
                ["claude.session_id"] = Element("sess-claude-err"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("claude-code"),
            },
            Scope: new OtlpScope("com.anthropic.claude_code", "2.1.0"));

        ClaudeSpanMapper mapper = new();
        CollectedRecord record = mapper.MapSpan(span);

        Assert.Equal(RecordOp.ToolInvoke, record.Op);
        Assert.Equal(RecordKind.Client, record.Kind);
        Assert.Equal(RecordStatus.Error, record.Status);
        Assert.Equal("sess-claude-err", record.SessionKey);
        Assert.Equal("Command exited with non-zero status", record.Signals.ErrorMessage);

        Assert.NotNull(record.Tool);
        Assert.Equal("toolu_02ERR", record.Tool.CallId);
        Assert.Equal("Bash", record.Tool.ToolName);
        Assert.Null(record.Tool.McpServerName);
        Assert.Null(record.Tool.McpToolName);
        Assert.False(record.Tool.Succeeded);
        Assert.Equal(128L, record.Tool.ExitCode);
        Assert.NotNull(record.Tool.Error);
        Assert.Equal("Command exited with non-zero status", record.Tool.Error.Message);
        Assert.Equal("EXIT_NON_ZERO", record.Tool.Error.Code);
        Assert.NotNull(record.Tool.Input);
        Assert.Equal("git status --porcelain", record.Tool.Input.PlainText);
        Assert.NotNull(record.Tool.Result);
        Assert.Equal("fatal: not a git repository", record.Tool.Result.PlainText);

        CollectedContentPart resultPart = Assert.Single(record.Parts);
        Assert.Equal(ContentBucket.ToolResultContent, resultPart.Bucket);
        Assert.Equal("fatal: not a git repository", resultPart.PlainText);
        Assert.Equal("tool", resultPart.Role);
    }

    [Theory]
    [InlineData("internal", RecordKind.Internal, "ok", RecordStatus.Ok, true, null)]
    [InlineData("client", RecordKind.Client, "error", RecordStatus.Error, false, "Subagent timed out")]
    public void MapSpan_InvokeAgentSpanWithClientOrInternalKind_AssertsEveryCollectedRecordAndAgentProperty(
        string otlpKind,
        RecordKind expectedKind,
        string otlpStatus,
        RecordStatus expectedStatus,
        bool expectedSucceeded,
        string? expectedError)
    {
        Dictionary<string, JsonElement> attrs = new(StringComparer.Ordinal)
        {
            ["gen_ai.operation.name"] = Element("invoke_agent"),
            ["gen_ai.agent.name"] = Element("Explore"),
            ["gen_ai.agent.id"] = Element("agent-sub-42"),
            ["gen_ai.agent.spawning_record_id"] = Element("toolu_spawn_9"),
            ["gen_ai.session.id"] = Element("sess-claude-agent-1"),
            ["gen_ai.request.model"] = Element("claude-haiku-4-5"),
        };

        if (expectedError is not null)
        {
            attrs["gen_ai.agent.error"] = Element(expectedError);
        }

        OtlpSpan span = new(
            TraceId: "abcdefabcdefabcdefabcdefabcdef01",
            SpanId: "beef000000000099",
            ParentSpanId: "parent-turn-1",
            Name: "invoke_agent Explore",
            Kind: otlpKind,
            StartTimeUnixNano: "1763550004000000000",
            EndTimeUnixNano: "1763550005500000000",
            Timestamp: "2026-11-19T11:00:04.000Z",
            DurationMs: 1500.0,
            Status: new OtlpStatus(otlpStatus, expectedError),
            Attributes: attrs,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("claude-code"),
            },
            Scope: new OtlpScope("com.anthropic.claude_code", "2.1.0"));

        ClaudeSpanMapper mapper = new();
        CollectedRecord record = mapper.MapSpan(span);

        Assert.Equal("beef000000000099", record.RecordId);
        Assert.Equal("claude-code", record.HarnessId);
        Assert.Equal(RecordOp.AgentInvoke, record.Op);
        Assert.Equal(expectedKind, record.Kind);
        Assert.Equal("invoke_agent Explore", record.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:04.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(expectedStatus, record.Status);
        Assert.Equal("sess-claude-agent-1", record.SessionKey);
        Assert.Equal("parent-turn-1", record.ParentRecordId);
        Assert.Equal("abcdefabcdefabcdefabcdefabcdef01", record.TraceId);
        Assert.Equal("claude-haiku-4-5", record.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T11:00:05.500Z", CultureInfo.InvariantCulture), record.EndedAt);
        Assert.Equal(1500L, record.ReportedDurationMs);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);
        Assert.Null(record.ReportedCost);
        Assert.Empty(record.Parts);
        Assert.Null(record.Tool);

        Assert.NotNull(record.Agent);
        Assert.Equal("Explore", record.Agent.AgentName);
        Assert.Equal("toolu_spawn_9", record.Agent.SpawningCallId);
        Assert.Equal(expectedSucceeded, record.Agent.Succeeded);
        Assert.Equal(expectedError, record.Agent.Error);

        Assert.Empty(record.SkillsInvoked);
        Assert.Equal("agent-sub-42", record.AgentInstanceId);
        Assert.Empty(record.ToolDefinitions);
        Assert.Equal(expectedError, record.Signals.ErrorMessage);
        Assert.Null(record.Signals.EditFailures);
        Assert.Null(record.Signals.CorrectionRule);
        Assert.Null(record.Signals.DeclaredContextWindow);
    }

    [Fact]
    public void Map_NativeSourceUnitOtlpJson_MapsSessionsRecordsAndRejectsNegativeTokensIntoProblems()
    {
        string otlpJson = """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "service.name", "value": { "stringValue": "claude-code" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "scope": { "name": "com.anthropic.claude_code", "version": "2.1.0" },
                      "spans": [
                        {
                          "traceId": "0102030405060708090a0b0c0d0e0f10",
                          "spanId": "1111111111111111",
                          "name": "claude.model",
                          "kind": "SPAN_KIND_CLIENT",
                          "startTimeUnixNano": "1763550000000000000",
                          "endTimeUnixNano": "1763550000500000000",
                          "status": { "code": "STATUS_CODE_OK" },
                          "attributes": [
                            { "key": "session.id", "value": { "stringValue": "sess-batch-1" } },
                            { "key": "model", "value": { "stringValue": "claude-sonnet-4-6" } },
                            { "key": "input_tokens", "value": { "intValue": "40" } },
                            { "key": "cache_read_tokens", "value": { "intValue": "10" } },
                            { "key": "cache_creation_tokens", "value": { "intValue": "5" } },
                            { "key": "output_tokens", "value": { "intValue": "12" } }
                          ]
                        },
                        {
                          "traceId": "0102030405060708090a0b0c0d0e0f10",
                          "spanId": "2222222222222222",
                          "parentSpanId": "1111111111111111",
                          "name": "invoke_agent",
                          "kind": "SPAN_KIND_INTERNAL",
                          "startTimeUnixNano": "1763550000600000000",
                          "endTimeUnixNano": "1763550000900000000",
                          "status": { "code": "STATUS_CODE_OK" },
                          "attributes": [
                            { "key": "session.id", "value": { "stringValue": "sess-batch-1" } },
                            { "key": "gen_ai.operation.name", "value": { "stringValue": "invoke_agent" } },
                            { "key": "gen_ai.agent.name", "value": { "stringValue": "code-reviewer" } }
                          ]
                        },
                        {
                          "traceId": "0102030405060708090a0b0c0d0e0f10",
                          "spanId": "3333333333333333",
                          "name": "claude.bad",
                          "kind": "SPAN_KIND_CLIENT",
                          "startTimeUnixNano": "1763550001000000000",
                          "endTimeUnixNano": "1763550001100000000",
                          "status": { "code": "STATUS_CODE_OK" },
                          "attributes": [
                            { "key": "session.id", "value": { "stringValue": "sess-batch-1" } },
                            { "key": "input_tokens", "value": { "intValue": "10" } },
                            { "key": "output_tokens", "value": { "intValue": "-3" } }
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
            HarnessId: "claude-code",
            SourceKey: "http://127.0.0.1:4318/v1/traces",
            ProviderId: "anthropic",
            ParserId: "claude-otlp-mapper",
            ParserContractVersion: "1",
            Format: "otlp-json",
            SourceRootLabel: "otlp",
            RevisionToken: "rev-1",
            Kind: SourceKind.Otlp);
        NativeSourceUnit unit = new(descriptor, otlpJson, Encoding.UTF8.GetByteCount(otlpJson), ImportedAtUtc);

        CollectedBatch batch = new ClaudeSpanMapper().Map(unit, ImportedAtUtc);

        Assert.Equal("claude-code", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);
        Assert.Equal("http://127.0.0.1:4318/v1/traces", batch.Source.Locator);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-batch-1", session.SessionKey);
        Assert.Equal("claude-code", session.HarnessId);
        Assert.Equal("sess-batch-1", session.NativeSessionId);

        Assert.Equal(2, batch.Records.Count);
        CollectedRecord modelRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Equal("1111111111111111", modelRecord.RecordId);
        Assert.Equal(55L, modelRecord.ReportedInputTokens);
        Assert.Equal(12L, modelRecord.ReportedOutputTokens);

        CollectedRecord agentRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.AgentInvoke);
        Assert.Equal("2222222222222222", agentRecord.RecordId);
        Assert.NotNull(agentRecord.Agent);
        Assert.Equal("code-reviewer", agentRecord.Agent.AgentName);
        Assert.Equal("1111111111111111", agentRecord.Agent.SpawningCallId);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(CanonValidation.TokenNegativeClass, problem.Code);
        Assert.Equal("3333333333333333", problem.SpanId);
        Assert.Equal("claude-code", problem.HarnessId);
    }

    [Fact]
    public void OtlpSpanNative_Parse_ExposesCanonAdapterKeyListsAndExtractsTypedFields()
    {
        Assert.Equal(["gen_ai.usage.input_tokens", "input_tokens"], OtlpSpanNative.InputTokenKeys);
        Assert.Equal(["gen_ai.usage.output_tokens", "output_tokens"], OtlpSpanNative.OutputTokenKeys);
        Assert.Equal(["gen_ai.response.model", "gen_ai.request.model", "model"], OtlpSpanNative.CanonicalModelKeys);
        Assert.Contains("gen_ai.session.id", OtlpSpanNative.SessionIdKeys);
        Assert.Contains("gen_ai.usage.cache_read.input_tokens", OtlpSpanNative.CacheReadKeys);
        Assert.Contains("gen_ai.usage.cache_creation.input_tokens", OtlpSpanNative.CacheCreationKeys);

        OtlpSpan span = new(
            TraceId: "aaaa",
            SpanId: "bbbb",
            ParentSpanId: null,
            Name: "claude.turn",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000000000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 0.0,
            Status: new OtlpStatus("unset"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["model"] = Element("{\"nested\":\"doc\"}"),
                ["gen_ai.request.model"] = Element("claude-opus-4-6"),
                ["session.id"] = Element("sess-native-1"),
                ["input_tokens"] = Element("30"),
                ["cache_read_tokens"] = Element(10),
                ["cache_creation_tokens"] = Element(5),
                ["output_tokens"] = Element(20),
                ["gen_ai.prompt"] = Element("Summarize changes"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            Scope: new OtlpScope("com.anthropic.claude_code", null));

        OtlpSpanNative native = OtlpSpanNative.Parse(span);

        Assert.Equal("bbbb", native.SpanId);
        Assert.Equal("aaaa", native.TraceId);
        Assert.Equal("otlp", native.Source);
        Assert.Equal("sess-native-1", native.SessionId);
        Assert.Equal("claude-opus-4-6", native.Model);
        Assert.Equal(30L, native.InputTokens);
        Assert.Equal(10L, native.CacheReadTokens);
        Assert.Equal(5L, native.CacheCreationTokens);
        Assert.Equal(20L, native.OutputTokens);
        ContentPart promptPart = Assert.Single(native.Parts);
        Assert.Equal("conversation_history", promptPart.Part);
        Assert.Equal("Summarize changes", promptPart.Text);
    }

    [Fact]
    public void Map_MalformedOtlpPayload_EmitsMalformedOtlpProblem_AndValidBatchPersistsIntoRelationalStore()
    {
        SourceUnit descriptor = new(
            HarnessId: "claude-code",
            SourceKey: "http://127.0.0.1:4318/v1/traces",
            ProviderId: "anthropic",
            ParserId: "claude-otlp-mapper",
            ParserContractVersion: "1",
            Format: "otlp-json",
            SourceRootLabel: "otlp",
            RevisionToken: "rev-bad",
            Kind: SourceKind.Otlp);
        NativeSourceUnit malformedUnit = new(descriptor, "{not-valid-json", 15, ImportedAtUtc);

        ClaudeSpanMapper mapper = new();
        CollectedBatch errorBatch = mapper.Map(malformedUnit, ImportedAtUtc);
        Assert.Empty(errorBatch.Records);
        CollectedProblem problem = Assert.Single(errorBatch.Problems);
        Assert.Equal("MALFORMED_OTLP_PAYLOAD", problem.Code);
        Assert.Equal("claude-code", problem.HarnessId);

        OtlpSpan modelSpan = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "span-persist-model",
            ParentSpanId: null,
            Name: "claude.model",
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550000250000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 250.0,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-persist-1"),
                ["gen_ai.response.model"] = Element("claude-sonnet-4-6"),
                ["gen_ai.usage.input_tokens"] = Element(50),
                ["gen_ai.usage.output_tokens"] = Element(15),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("claude-code"),
            },
            Scope: new OtlpScope("com.anthropic.claude_code", "2.1.0"));

        OtlpSpan toolSpan = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "span-persist-tool",
            ParentSpanId: "span-persist-model",
            Name: "Read",
            Kind: "internal",
            StartTimeUnixNano: "1763550000300000000",
            EndTimeUnixNano: "1763550000350000000",
            Timestamp: "2026-11-19T11:00:00.300Z",
            DurationMs: 50.0,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-persist-1"),
                ["gen_ai.tool.name"] = Element("Read"),
                ["gen_ai.tool.call.id"] = Element("toolu_read_1"),
                ["gen_ai.tool.mcp.server"] = Element("fs"),
                ["gen_ai.tool.mcp.tool"] = Element("read_file"),
                ["gen_ai.tool.arguments"] = Element("""{"path":"README.md"}"""),
                ["gen_ai.tool.result"] = Element("file contents"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("claude-code"),
            },
            Scope: new OtlpScope("com.anthropic.claude_code", "2.1.0"));

        OtlpSpan agentSpan = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "span-persist-agent",
            ParentSpanId: "span-persist-tool",
            Name: "invoke_agent",
            Kind: "internal",
            StartTimeUnixNano: "1763550000400000000",
            EndTimeUnixNano: "1763550000800000000",
            Timestamp: "2026-11-19T11:00:00.400Z",
            DurationMs: 400.0,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.session.id"] = Element("sess-persist-1"),
                ["gen_ai.operation.name"] = Element("invoke_agent"),
                ["gen_ai.agent.name"] = Element("architect"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("claude-code"),
            },
            Scope: new OtlpScope("com.anthropic.claude_code", "2.1.0"));

        CollectedBatch validBatch = mapper.MapSpans([modelSpan, toolSpan, agentSpan]);
        InMemoryRelationalStore store = new();
        store.WriteBatch(validBatch);

        Assert.Equal(3, store.CountRecords("claude-code"));
        Assert.NotNull(store.GetSession("sess-persist-1"));
        Assert.NotNull(store.GetRecord("span-persist-model"));
        Assert.NotNull(store.GetRecord("span-persist-tool")?.Tool);
        Assert.NotNull(store.GetRecord("span-persist-agent")?.Agent);
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    private static string Sha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }
}
