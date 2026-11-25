using System.Security.Cryptography;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Claude;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Data.Repositories;
using KyberDash.Data.Tokenization;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="ClaudeTranscriptMapper"/> verifying pure JSONL-to-collector-DTO mapping,
/// token classes, absent optional rows, tool_use/tool_result pairing, JSON document withholding,
/// nested JSON strings, prose with JSON fragments, and numeric/boolean content slots.
/// </summary>
public class ClaudeTranscriptMapperTests
{
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_ThrowsArgumentNullException_WhenUnitIsNull()
    {
        ClaudeTranscriptMapper mapper = new();

        Assert.Throws<ArgumentNullException>(() => mapper.Map(null!, ImportedAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t  ")]
    [InlineData("{ broken json\n[]\n42\n\"just a string\"")]
    [InlineData("{\"type\":\"user\",\"sessionId\":\"sess-no-usage\",\"timestamp\":\"2026-11-19T10:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}")]
    public void Map_ReturnsEmptyCollections_WhenRawTextIsEmptyMalformedOrWithoutUsage(string? rawText)
    {
        ClaudeTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit("/home/dev/.claude/projects/repo/empty-session.jsonl", rawText);

        CollectedBatch batch = mapper.Map(unit, ImportedAt);

        Assert.Equal("claude-code", mapper.HarnessId);
        Assert.Equal("claude-code", batch.Source.HarnessId);
        Assert.Equal(SourceKind.File, batch.Source.Kind);
        Assert.Equal("/home/dev/.claude/projects/repo/empty-session.jsonl", batch.Source.Locator);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_MapsAllTokenClassesSessionMetadataAndParentRecordProperties()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"user\",\"sessionId\":\"sess-all\",\"customTitle\":\"Fix mapper\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"attributionMcpServer\":\"kw-docs\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Please inspect the build\"}]}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-all\",\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"terminationReason\":\"task_complete\",\"message\":{\"id\":\"msg-all-1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"Checking files\"},{\"type\":\"text\",\"text\":\"Build looks clean.\"}],\"usage\":{\"input_tokens\":120,\"output_tokens\":80,\"cache_read_input_tokens\":500,\"cache_creation_input_tokens\":40,\"reasoning_tokens\":15}}}",
        ]);

        ClaudeTranscriptMapper mapper = new();
        CollectedBatch batch = mapper.Map(CreateUnit("/tmp/.claude/projects/repo/sess-all.jsonl", jsonl), ImportedAt);

        Assert.Equal("claude-code", batch.Source.HarnessId);
        Assert.Equal(SourceKind.File, batch.Source.Kind);
        Assert.Equal("/tmp/.claude/projects/repo/sess-all.jsonl", batch.Source.Locator);
        Assert.Empty(batch.Problems);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-all", session.SessionKey);
        Assert.Equal("claude-code", session.HarnessId);
        Assert.Equal("sess-all", session.NativeSessionId);
        Assert.Null(session.Workspace);
        Assert.Equal("Fix mapper", session.Title);
        Assert.Empty(session.ReportedUsage);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("synth:claude-code:sess-all:msg-all-1", record.RecordId);
        Assert.Equal("claude-code", record.HarnessId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal(RecordKind.Internal, record.Kind);
        Assert.Equal("claude:claude-sonnet-4-5", record.Name);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 10, 0, 1, TimeSpan.Zero), record.StartedAt);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("sess-all", record.SessionKey);
        Assert.Null(record.ParentRecordId);
        Assert.Equal("synth:claude-code:sess-all", record.TraceId);
        Assert.Equal("claude-sonnet-4-5", record.Model);
        Assert.Null(record.EndedAt);
        Assert.Null(record.ReportedDurationMs);
        Assert.Null(record.ReportedCost);
        Assert.Null(record.Tool);
        Assert.Null(record.Agent);
        Assert.Empty(record.SkillsInvoked);
        Assert.Null(record.AgentInstanceId);
        Assert.Empty(record.ToolDefinitions);
        Assert.Null(record.Signals.ErrorMessage);
        Assert.Null(record.Signals.EditFailures);
        Assert.Null(record.Signals.CorrectionRule);
        Assert.Null(record.Signals.DeclaredContextWindow);
        Assert.Equal(660L, record.ReportedInputTokens);
        Assert.Equal(80L, record.ReportedOutputTokens);

        Assert.Equal(5, record.Tokens.Count);
        Assert.Contains(new CollectedTokenCount(TokenClass.Input, 120L), record.Tokens);
        Assert.Contains(new CollectedTokenCount(TokenClass.CacheRead, 500L), record.Tokens);
        Assert.Contains(new CollectedTokenCount(TokenClass.CacheWrite, 40L), record.Tokens);
        Assert.Contains(new CollectedTokenCount(TokenClass.Output, 80L), record.Tokens);
        Assert.Contains(new CollectedTokenCount(TokenClass.Reasoning, 15L), record.Tokens);

        Assert.Equal(3, record.Parts.Count);
        CollectedContentPart userPart = record.Parts[0];
        Assert.Equal(0, userPart.Position);
        Assert.Equal(ContentBucket.ConversationHistory, userPart.Bucket);
        Assert.Equal("Please inspect the build", userPart.PlainText);
        Assert.Null(userPart.Withheld);
        Assert.Null(userPart.ReportedTokens);
        Assert.Equal("kw-docs", userPart.Server);
        Assert.Equal("user", userPart.Role);
        Assert.Null(userPart.Resident);
        Assert.Null(userPart.OriginalUtf8BytesWhenTruncated);

        CollectedContentPart thinkingPart = record.Parts[1];
        Assert.Equal(1, thinkingPart.Position);
        Assert.Equal(ContentBucket.ConversationHistory, thinkingPart.Bucket);
        Assert.Equal("Checking files", thinkingPart.PlainText);
        Assert.Null(thinkingPart.Withheld);
        Assert.Null(thinkingPart.Server);
        Assert.Equal("assistant", thinkingPart.Role);

        CollectedContentPart assistantPart = record.Parts[2];
        Assert.Equal(2, assistantPart.Position);
        Assert.Equal(ContentBucket.ConversationHistory, assistantPart.Bucket);
        Assert.Equal("Build looks clean.", assistantPart.PlainText);
        Assert.Null(assistantPart.Withheld);
        Assert.Equal("assistant", assistantPart.Role);
    }

    [Fact]
    public void Map_OmitsAbsentOptionalTokenAndMetadataRows_WhenMissingFromUsageAndMessage()
    {
        string jsonl =
            "{\"type\":\"assistant\",\"timestamp\":\"2026-11-19T10:05:00.000Z\",\"message\":{\"role\":\"assistant\",\"content\":\"Done\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}";

        ClaudeTranscriptMapper mapper = new();
        CollectedBatch batch = mapper.Map(CreateUnit("/tmp/.claude/projects/repo/stem-session.jsonl", jsonl), ImportedAt);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("stem-session", session.SessionKey);
        Assert.Equal("stem-session", session.NativeSessionId);
        Assert.Null(session.Title);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.StartsWith("synth:claude-code:stem-session:", record.RecordId, StringComparison.Ordinal);
        Assert.Equal("claude:unknown", record.Name);
        Assert.Equal(RecordStatus.Unspecified, record.Status);
        Assert.Equal("stem-session", record.SessionKey);
        Assert.Equal("synth:claude-code:stem-session", record.TraceId);
        Assert.Null(record.Model);
        Assert.Null(record.EndedAt);
        Assert.Null(record.ReportedDurationMs);
        Assert.Null(record.ReportedCost);
        Assert.Null(record.Tool);
        Assert.Null(record.Agent);
        Assert.Null(record.Signals.ErrorMessage);
        Assert.Null(record.Signals.EditFailures);
        Assert.Null(record.Signals.CorrectionRule);
        Assert.Null(record.Signals.DeclaredContextWindow);
        Assert.Equal(10L, record.ReportedInputTokens);
        Assert.Equal(5L, record.ReportedOutputTokens);

        Assert.Equal(2, record.Tokens.Count);
        Assert.Contains(new CollectedTokenCount(TokenClass.Input, 10L), record.Tokens);
        Assert.Contains(new CollectedTokenCount(TokenClass.Output, 5L), record.Tokens);
        Assert.DoesNotContain(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead);
        Assert.DoesNotContain(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite);
        Assert.DoesNotContain(record.Tokens, static t => t.TokenClass == TokenClass.Reasoning);

        CollectedContentPart part = Assert.Single(record.Parts);
        Assert.Equal("Done", part.PlainText);
        Assert.Null(part.Withheld);
        Assert.Null(part.Server);
        Assert.Equal("assistant", part.Role);
        Assert.Null(part.Resident);
        Assert.Null(part.ReportedTokens);
        Assert.Null(part.OriginalUtf8BytesWhenTruncated);
    }

    [Fact]
    public void Map_PairsToolUseAndToolResultAcrossTurnsAndLeavesUnpairedResultFieldsAbsent()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"assistant\",\"sessionId\":\"sess-tools\",\"timestamp\":\"2026-11-19T11:00:00.000Z\",\"message\":{\"id\":\"msg-t1\",\"role\":\"assistant\",\"model\":\"claude-opus-4-6\",\"content\":[{\"type\":\"tool_use\",\"id\":\"tc-ok\",\"name\":\"mcp__codegraph__explore\",\"input\":{\"query\":\"WriteBatch\"}},{\"type\":\"tool_use\",\"id\":\"tc-err\",\"name\":\"bash\",\"input\":\"rm -rf /nope\"},{\"type\":\"tool_use\",\"id\":\"tc-unpaired\",\"name\":\"read_file\"}],\"usage\":{\"input_tokens\":50,\"output_tokens\":20}}}",
            "{\"type\":\"user\",\"sessionId\":\"sess-tools\",\"timestamp\":\"2026-11-19T11:00:01.250Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tc-ok\",\"content\":\"Found 2 matches\"},{\"type\":\"tool_result\",\"tool_use_id\":\"tc-err\",\"is_error\":true,\"exit_code\":2,\"content\":\"Permission denied\"}]}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-tools\",\"timestamp\":\"2026-11-19T11:00:02.000Z\",\"message\":{\"id\":\"msg-t2\",\"role\":\"assistant\",\"model\":\"claude-opus-4-6\",\"content\":[{\"type\":\"text\",\"text\":\"Handled tool errors\"}],\"usage\":{\"input_tokens\":60,\"output_tokens\":10}}}",
        ]);

        ClaudeTranscriptMapper mapper = new();
        CollectedBatch batch = mapper.Map(CreateUnit("/tmp/.claude/projects/repo/sess-tools.jsonl", jsonl), ImportedAt);

        Assert.Single(batch.Sessions);
        Assert.Equal(5, batch.Records.Count);

        CollectedRecord parent1 = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, parent1.Op);
        Assert.Equal("synth:claude-code:sess-tools:msg-t1", parent1.RecordId);

        CollectedRecord mcpTool = batch.Records[1];
        Assert.Equal(RecordOp.ToolInvoke, mcpTool.Op);
        Assert.Equal(RecordKind.Internal, mcpTool.Kind);
        Assert.Equal("mcp__codegraph__explore", mcpTool.Name);
        Assert.Equal(parent1.RecordId, mcpTool.ParentRecordId);
        Assert.Equal(parent1.TraceId, mcpTool.TraceId);
        Assert.Equal(parent1.SessionKey, mcpTool.SessionKey);
        Assert.Null(mcpTool.Model);
        Assert.Empty(mcpTool.Tokens);
        Assert.Null(mcpTool.ReportedCost);
        Assert.Null(mcpTool.ReportedInputTokens);
        Assert.Null(mcpTool.ReportedOutputTokens);
        Assert.Equal(RecordStatus.Ok, mcpTool.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 11, 0, 0, TimeSpan.Zero), mcpTool.StartedAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 11, 0, 1, 250, TimeSpan.Zero), mcpTool.EndedAt);
        Assert.Equal(1250L, mcpTool.ReportedDurationMs);
        Assert.NotNull(mcpTool.Tool);
        Assert.Equal("tc-ok", mcpTool.Tool!.CallId);
        Assert.Equal("mcp__codegraph__explore", mcpTool.Tool.ToolName);
        Assert.Equal("codegraph", mcpTool.Tool.McpServerName);
        Assert.Equal("explore", mcpTool.Tool.McpToolName);
        Assert.NotNull(mcpTool.Tool.Input);
        Assert.Null(mcpTool.Tool.Input!.PlainText);
        Assert.Equal(Sha256Hex("{\"query\":\"WriteBatch\"}"), mcpTool.Tool.Input.Sha256Fingerprint);
        Assert.NotNull(mcpTool.Tool.Result);
        Assert.Equal("Found 2 matches", mcpTool.Tool.Result!.PlainText);
        Assert.Null(mcpTool.Tool.Result.Sha256Fingerprint);
        Assert.True(mcpTool.Tool.Succeeded);
        Assert.Null(mcpTool.Tool.Error);
        Assert.Null(mcpTool.Tool.ExitCode);
        CollectedContentPart okResultPart = Assert.Single(mcpTool.Parts);
        Assert.Equal(ContentBucket.ToolResultContent, okResultPart.Bucket);
        Assert.Equal("Found 2 matches", okResultPart.PlainText);
        Assert.Null(okResultPart.Withheld);

        CollectedRecord errTool = batch.Records[2];
        Assert.Equal(RecordOp.ToolInvoke, errTool.Op);
        Assert.Equal(RecordStatus.Error, errTool.Status);
        Assert.NotNull(errTool.Tool);
        Assert.Equal("bash", errTool.Tool!.ToolName);
        Assert.Null(errTool.Tool.McpServerName);
        Assert.Null(errTool.Tool.McpToolName);
        Assert.Equal("rm -rf /nope", errTool.Tool.Input!.PlainText);
        Assert.Equal(Sha256Hex("rm -rf /nope"), errTool.Tool.Input.Sha256Fingerprint);
        Assert.Equal("Permission denied", errTool.Tool.Result!.PlainText);
        Assert.False(errTool.Tool.Succeeded);
        Assert.NotNull(errTool.Tool.Error);
        Assert.Equal("Permission denied", errTool.Tool.Error!.Message);
        Assert.Null(errTool.Tool.Error.Code);
        Assert.Equal(2L, errTool.Tool.ExitCode);

        CollectedRecord unpairedTool = batch.Records[3];
        Assert.Equal(RecordOp.ToolInvoke, unpairedTool.Op);
        Assert.Equal(RecordStatus.Unset, unpairedTool.Status);
        Assert.Null(unpairedTool.EndedAt);
        Assert.Null(unpairedTool.ReportedDurationMs);
        Assert.Empty(unpairedTool.Parts);
        Assert.NotNull(unpairedTool.Tool);
        Assert.Null(unpairedTool.Tool!.Input);
        Assert.Null(unpairedTool.Tool.Result);
        Assert.Null(unpairedTool.Tool.Succeeded);
        Assert.Null(unpairedTool.Tool.Error);
        Assert.Null(unpairedTool.Tool.ExitCode);
    }

    [Fact]
    public void Map_WithholdsJsonDocumentAndNestedJsonStringPayloadsAndPreservesProseWithJson()
    {
        string jsonDocResult = "{\"z\":1,\"a\":2}";
        string nestedJsonString = "\"{\\\"b\\\":1,\\\"a\\\":2}\"";
        string proseWithJson = "Observed fragment {\"ok\":true} in output.";

        string jsonl = string.Join('\n',
        [
            "{\"type\":\"assistant\",\"sessionId\":\"sess-json\",\"timestamp\":\"2026-11-19T11:10:00.000Z\",\"message\":{\"id\":\"msg-j1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"tool_use\",\"id\":\"tc-doc\",\"name\":\"read_json\",\"input\":{\"path\":\"a.json\"}},{\"type\":\"tool_use\",\"id\":\"tc-nested\",\"name\":\"read_nested\",\"input\":{\"path\":\"b.json\"}},{\"type\":\"tool_use\",\"id\":\"tc-prose\",\"name\":\"summarize\",\"input\":{\"path\":\"c.txt\"}}],\"usage\":{\"input_tokens\":30,\"output_tokens\":10}}}",
            $"{{\"type\":\"user\",\"sessionId\":\"sess-json\",\"timestamp\":\"2026-11-19T11:10:01.000Z\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"tool_result\",\"tool_use_id\":\"tc-doc\",\"content\":{System.Text.Json.JsonSerializer.Serialize(jsonDocResult)}}},{{\"type\":\"tool_result\",\"tool_use_id\":\"tc-nested\",\"content\":{System.Text.Json.JsonSerializer.Serialize(nestedJsonString)}}},{{\"type\":\"tool_result\",\"tool_use_id\":\"tc-prose\",\"content\":{System.Text.Json.JsonSerializer.Serialize(proseWithJson)}}}]}}}}",
        ]);

        ClaudeTranscriptMapper mapper = new();
        CollectedBatch batch = mapper.Map(CreateUnit("/tmp/.claude/projects/repo/sess-json.jsonl", jsonl), ImportedAt);

        Assert.Equal(4, batch.Records.Count);

        CollectedRecord docTool = batch.Records[1];
        Assert.Null(docTool.Tool!.Result!.PlainText);
        Assert.Equal(Encoding.UTF8.GetByteCount(jsonDocResult), docTool.Tool.Result.Utf8Bytes);
        Assert.Equal(jsonDocResult.Length, docTool.Tool.Result.Characters);
        Assert.Null(docTool.Tool.Result.Sha256Fingerprint);
        CollectedContentPart docPart = Assert.Single(docTool.Parts);
        Assert.Null(docPart.PlainText);
        Assert.NotNull(docPart.Withheld);
        Assert.Equal(Encoding.UTF8.GetByteCount(jsonDocResult), docPart.Withheld!.Utf8Bytes);
        Assert.Equal(jsonDocResult.Length, docPart.Withheld.Characters);
        Assert.Equal(LocalTextTokenizer.Shared.Count(jsonDocResult, "claude-sonnet-4-5").Count, docPart.Withheld.MeasuredTokens);

        CollectedRecord nestedTool = batch.Records[2];
        Assert.Null(nestedTool.Tool!.Result!.PlainText);
        Assert.Equal(Encoding.UTF8.GetByteCount(nestedJsonString), nestedTool.Tool.Result.Utf8Bytes);
        CollectedContentPart nestedPart = Assert.Single(nestedTool.Parts);
        Assert.Null(nestedPart.PlainText);
        Assert.NotNull(nestedPart.Withheld);
        Assert.Equal(Encoding.UTF8.GetByteCount(nestedJsonString), nestedPart.Withheld!.Utf8Bytes);

        CollectedRecord proseTool = batch.Records[3];
        Assert.Equal(proseWithJson, proseTool.Tool!.Result!.PlainText);
        CollectedContentPart prosePart = Assert.Single(proseTool.Parts);
        Assert.Equal(proseWithJson, prosePart.PlainText);
        Assert.Null(prosePart.Withheld);
    }

    /// <summary>
    /// A record sent to a model whose own published tokenizer differs from the stand-in counts its
    /// tool-result part with the model's own tokenizer: the measured tokens equal the tokenizer the
    /// model itself resolves to, not the stand-in figure a missing model would produce.
    /// </summary>
    [Fact]
    public void Map_CountsToolResultPartsWithTheRecordModelOwnTokenizer()
    {
        string jsonDocResult = "{\"こんにちは\":\"世界\"}";

        string jsonl = string.Join('\n',
        [
            "{\"type\":\"assistant\",\"sessionId\":\"sess-model-own\",\"timestamp\":\"2026-11-19T11:30:00.000Z\",\"message\":{\"id\":\"msg-m1\",\"role\":\"assistant\",\"model\":\"gpt-3.5-turbo\",\"content\":[{\"type\":\"tool_use\",\"id\":\"tc-own\",\"name\":\"read_json\",\"input\":{\"path\":\"a.json\"}}],\"usage\":{\"input_tokens\":30,\"output_tokens\":10}}}",
            $"{{\"type\":\"user\",\"sessionId\":\"sess-model-own\",\"timestamp\":\"2026-11-19T11:30:01.000Z\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"tool_result\",\"tool_use_id\":\"tc-own\",\"content\":{System.Text.Json.JsonSerializer.Serialize(jsonDocResult)}}}]}}}}",
        ]);

        ClaudeTranscriptMapper mapper = new();
        CollectedBatch batch = mapper.Map(CreateUnit("/tmp/.claude/projects/repo/sess-model-own.jsonl", jsonl), ImportedAt);

        CollectedRecord toolRecord = batch.Records[1];
        Assert.Equal("gpt-3.5-turbo", batch.Records[0].Model);
        CollectedContentPart part = Assert.Single(toolRecord.Parts);
        Assert.NotNull(part.Withheld);
        Assert.Equal(LocalTextTokenizer.Shared.Count(jsonDocResult, "gpt-3.5-turbo").Count, part.Withheld!.MeasuredTokens);
        Assert.NotEqual(LocalTextTokenizer.Shared.Count(jsonDocResult, null).Count, part.Withheld.MeasuredTokens);
    }

    [Fact]
    public void AnMcpLoadSkillCallKeepsItsReplyAsResultTextAndWithholdsItsJsonInput()
    {
        string reply = "skill: csharp-dev\n\n# csharp-dev\nGuidance.";

        string jsonl = string.Join('\n',
        [
            "{\"type\":\"assistant\",\"sessionId\":\"sess-kw-load\",\"timestamp\":\"2026-11-19T11:20:00.000Z\",\"message\":{\"id\":\"msg-kw1\",\"role\":\"assistant\",\"model\":\"claude-opus-4-6\",\"content\":[{\"type\":\"tool_use\",\"id\":\"tc-kw\",\"name\":\"mcp__kw-docs__load_skill\",\"input\":{\"name\":\"csharp-dev\"}}],\"usage\":{\"input_tokens\":20,\"output_tokens\":5}}}",
            $"{{\"type\":\"user\",\"sessionId\":\"sess-kw-load\",\"timestamp\":\"2026-11-19T11:20:01.000Z\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"tool_result\",\"tool_use_id\":\"tc-kw\",\"content\":{System.Text.Json.JsonSerializer.Serialize(reply)}}}]}}}}",
        ]);

        ClaudeTranscriptMapper mapper = new();
        CollectedBatch batch = mapper.Map(CreateUnit("/tmp/.claude/projects/repo/sess-kw-load.jsonl", jsonl), ImportedAt);

        CollectedRecord loadTool = batch.Records[1];
        Assert.NotNull(loadTool.Tool);
        Assert.Equal("kw-docs", loadTool.Tool!.McpServerName);
        Assert.Equal("load_skill", loadTool.Tool.McpToolName);
        Assert.NotNull(loadTool.Tool.Input);
        Assert.Null(loadTool.Tool.Input!.PlainText);
        Assert.Equal(Sha256Hex("{\"name\":\"csharp-dev\"}"), loadTool.Tool.Input.Sha256Fingerprint);
        Assert.Equal(reply, loadTool.Tool.Result!.PlainText);
        Assert.Equal(reply, Assert.Single(loadTool.Parts).PlainText);
    }

    [Fact]
    public void Map_PreservesNumericAndBooleanValuesInContentAndTextSlots()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"user\",\"sessionId\":\"sess-scalars\",\"customTitle\":123,\"timestamp\":\"2026-11-19T11:20:00.000Z\",\"message\":{\"role\":\"user\",\"content\":42}}",
            "{\"type\":\"user\",\"sessionId\":\"sess-scalars\",\"timestamp\":\"2026-11-19T11:20:00.500Z\",\"message\":{\"role\":\"user\",\"content\":true}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-scalars\",\"timestamp\":\"2026-11-19T11:20:01.000Z\",\"message\":{\"id\":\"msg-sc\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"thinking\",\"thinking\":false},{\"type\":\"text\",\"text\":99.5},{\"type\":\"tool_use\",\"id\":\"tc-num\",\"name\":\"calc\",\"input\":7},{\"type\":\"tool_result\",\"tool_use_id\":\"tc-num\",\"content\":true}],\"usage\":{\"input_tokens\":25,\"output_tokens\":5}}}",
        ]);

        ClaudeTranscriptMapper mapper = new();
        CollectedBatch batch = mapper.Map(CreateUnit("/tmp/.claude/projects/repo/sess-scalars.jsonl", jsonl), ImportedAt);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("123", session.Title);

        Assert.Equal(2, batch.Records.Count);
        CollectedRecord parent = batch.Records[0];
        Assert.Equal(6, parent.Parts.Count);
        Assert.Equal("42", parent.Parts[0].PlainText);
        Assert.Equal("true", parent.Parts[1].PlainText);
        Assert.Equal("false", parent.Parts[2].PlainText);
        Assert.Equal("99.5", parent.Parts[3].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, parent.Parts[4].Bucket);
        Assert.Contains("calc", parent.Parts[4].PlainText, StringComparison.Ordinal);
        Assert.Contains("7", parent.Parts[4].PlainText, StringComparison.Ordinal);
        Assert.Equal("assistant", parent.Parts[4].Role);
        Assert.Equal("true", parent.Parts[5].PlainText);
        Assert.Equal(ContentBucket.ToolResultContent, parent.Parts[5].Bucket);
        Assert.All(parent.Parts, static part => Assert.Null(part.Withheld));

        CollectedRecord tool = batch.Records[1];
        Assert.Equal("7", tool.Tool!.Input!.PlainText);
        Assert.Equal("true", tool.Tool.Result!.PlainText);
    }

    [Fact]
    public void Map_DetectsUserCorrectionCollapsesStreamingDuplicateUsageAndPersistsToInMemoryStore()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"user\",\"sessionId\":\"sess-corr\",\"aiTitle\":\"Correction session\",\"timestamp\":\"2026-11-19T11:30:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"No, wait, revert that and fix it.\"}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-corr\",\"timestamp\":\"2026-11-19T11:30:01.000Z\",\"message\":{\"id\":\"msg-c1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Reverting now.\"}],\"usage\":{\"input_tokens\":40,\"output_tokens\":15,\"cache_read_input_tokens\":10,\"cache_creation_input_tokens\":5}}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-corr\",\"timestamp\":\"2026-11-19T11:30:02.000Z\",\"exitCode\":1,\"message\":{\"id\":\"msg-c1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"tool_use\",\"id\":\"tc-fix\",\"name\":\"write_file\",\"input\":{\"path\":\"a.cs\"}}],\"usage\":{\"input_tokens\":40,\"output_tokens\":15,\"cache_read_input_tokens\":10,\"cache_creation_input_tokens\":5}}}",
            "{\"type\":\"user\",\"sessionId\":\"sess-corr\",\"timestamp\":\"2026-11-19T11:30:03.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tc-fix\",\"content\":\"Written\"}]}}",
        ]);

        ClaudeTranscriptMapper mapper = new();
        CollectedBatch batch = mapper.Map(CreateUnit("/tmp/.claude/projects/repo/sess-corr.jsonl", jsonl), ImportedAt);

        Assert.Single(batch.Sessions);
        Assert.Equal(2, batch.Records.Count);

        CollectedRecord parent = batch.Records[0];
        Assert.Equal(RecordStatus.Error, parent.Status);
        Assert.Equal("prefix_negation", parent.Signals.CorrectionRule);
        Assert.Equal(4, parent.Parts.Count);
        Assert.Equal(ContentBucket.ConversationHistory, parent.Parts[0].Bucket);
        Assert.Equal(ContentBucket.ConversationHistory, parent.Parts[1].Bucket);
        Assert.Equal(ContentBucket.ConversationHistory, parent.Parts[2].Bucket);
        Assert.Contains("write_file", parent.Parts[2].PlainText, StringComparison.Ordinal);
        Assert.Contains("a.cs", parent.Parts[2].PlainText, StringComparison.Ordinal);
        Assert.Equal(ContentBucket.ToolResultContent, parent.Parts[3].Bucket);
        Assert.Equal("Written", parent.Parts[3].PlainText);
        Assert.Equal(ContentBucket.ToolResultContent, batch.Records[1].Parts[0].Bucket);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        Assert.Equal(2, store.CountRecords());
        IReadOnlyList<CollectedSession> sessions = store.Sessions();
        Assert.Single(sessions);
        Assert.Equal("Correction session", sessions[0].Title);
    }

    /// <summary>
    /// The turn record carries the transcript's message id as its response id and the line's
    /// <c>requestId</c> as its request id, the ids the projector joins an OTLP turn to it on.
    /// </summary>
    [Fact]
    public void Map_WritesMessageIdAndRequestIdOnTheTurnRecord()
    {
        string jsonl = "{\"type\":\"assistant\",\"sessionId\":\"sess-ids\",\"requestId\":\"req_011CXYZ\",\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"message\":{\"id\":\"msg_01ABC\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}";

        CollectedBatch batch = new ClaudeTranscriptMapper().Map(CreateUnit("/tmp/.claude/projects/repo/sess-ids.jsonl", jsonl), ImportedAt);

        CollectedRecord turn = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Equal("msg_01ABC", turn.ResponseId);
        Assert.Equal("req_011CXYZ", turn.RequestId);
    }

    /// <summary>
    /// A transcript line with no message id and no request id leaves both ids absent: the record id's
    /// hash fallback is never written as a response id.
    /// </summary>
    [Fact]
    public void Map_LeavesIdsAbsent_WhenTheTranscriptHasNone()
    {
        string jsonl = "{\"type\":\"assistant\",\"sessionId\":\"sess-noid\",\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"message\":{\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}";

        CollectedBatch batch = new ClaudeTranscriptMapper().Map(CreateUnit("/tmp/.claude/projects/repo/sess-noid.jsonl", jsonl), ImportedAt);

        CollectedRecord turn = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Null(turn.ResponseId);
        Assert.Null(turn.RequestId);
    }

    /// <summary>
    /// An id longer than the join key bound is no join key: the turn record leaves it absent rather than
    /// storing it or cutting it down, while an id exactly at the bound is kept.
    /// </summary>
    [Fact]
    public void Map_LeavesAnIdLongerThanTheJoinBoundAbsent_AndKeepsOneAtTheBound()
    {
        string tooLongMessageId = new('m', OtlpJoinIds.MaxIdCharacters + 1);
        string atBoundRequestId = new('r', OtlpJoinIds.MaxIdCharacters);
        string jsonl = "{\"type\":\"assistant\",\"sessionId\":\"sess-longid\",\"requestId\":\"" + atBoundRequestId + "\",\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"message\":{\"id\":\"" + tooLongMessageId + "\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}";

        CollectedBatch batch = new ClaudeTranscriptMapper().Map(CreateUnit("/tmp/.claude/projects/repo/sess-longid.jsonl", jsonl), ImportedAt);

        CollectedRecord turn = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Null(turn.ResponseId);
        Assert.Equal(atBoundRequestId, turn.RequestId);
    }

    [Fact]
    public void Map_CoalescesStreamedAssistantEntriesSharingMessageIdAcrossGrowingUsage()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"user\",\"sessionId\":\"sess-stream\",\"timestamp\":\"2026-11-19T12:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"Inspect the module\"}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-stream\",\"requestId\":\"req-s1\",\"timestamp\":\"2026-11-19T12:00:01.000Z\",\"message\":{\"id\":\"msg-stream-1\",\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"Planning\"}],\"usage\":{\"input_tokens\":100,\"output_tokens\":1}}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-stream\",\"requestId\":\"req-s1\",\"timestamp\":\"2026-11-19T12:00:02.000Z\",\"message\":{\"id\":\"msg-stream-1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Running read_file\"}],\"usage\":{\"input_tokens\":100,\"output_tokens\":22,\"cache_read_input_tokens\":40}}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-stream\",\"requestId\":\"req-s1\",\"timestamp\":\"2026-11-19T12:00:03.000Z\",\"terminationReason\":\"task_complete\",\"message\":{\"id\":\"msg-stream-1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"tool_use\",\"id\":\"tc-s1\",\"name\":\"Skill\",\"input\":{\"skill\":\"deploy\"}}],\"usage\":{\"input_tokens\":100,\"output_tokens\":65,\"cache_read_input_tokens\":40,\"cache_creation_input_tokens\":10}}}",
            "{\"type\":\"user\",\"sessionId\":\"sess-stream\",\"timestamp\":\"2026-11-19T12:00:04.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tc-s1\",\"content\":\"Deployed\"}]}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-stream\",\"timestamp\":\"2026-11-19T12:00:05.000Z\",\"message\":{\"id\":\"msg-stream-2\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"All done.\"}],\"usage\":{\"input_tokens\":100,\"output_tokens\":65,\"cache_read_input_tokens\":40,\"cache_creation_input_tokens\":10}}}",
        ]);

        CollectedBatch batch = new ClaudeTranscriptMapper().Map(CreateUnit("/tmp/.claude/projects/repo/sess-stream.jsonl", jsonl), ImportedAt);

        Assert.Single(batch.Sessions);
        Assert.Equal(3, batch.Records.Count);

        CollectedRecord firstTurn = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, firstTurn.Op);
        Assert.Equal("synth:claude-code:sess-stream:msg-stream-1", firstTurn.RecordId);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 12, 0, 1, TimeSpan.Zero), firstTurn.StartedAt);
        Assert.Equal("claude-sonnet-4-5", firstTurn.Model);
        Assert.Equal(RecordStatus.Ok, firstTurn.Status);
        Assert.Equal(150L, firstTurn.ReportedInputTokens);
        Assert.Equal(65L, firstTurn.ReportedOutputTokens);
        Assert.Contains(new CollectedTokenCount(TokenClass.Input, 100L), firstTurn.Tokens);
        Assert.Contains(new CollectedTokenCount(TokenClass.Output, 65L), firstTurn.Tokens);
        Assert.Contains(new CollectedTokenCount(TokenClass.CacheRead, 40L), firstTurn.Tokens);
        Assert.Contains(new CollectedTokenCount(TokenClass.CacheWrite, 10L), firstTurn.Tokens);

        Assert.Equal(4, firstTurn.Parts.Count);
        Assert.Equal(ContentBucket.ConversationHistory, firstTurn.Parts[0].Bucket);
        Assert.Equal("Inspect the module", firstTurn.Parts[0].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, firstTurn.Parts[1].Bucket);
        Assert.Equal("Planning", firstTurn.Parts[1].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, firstTurn.Parts[2].Bucket);
        Assert.Equal("Running read_file", firstTurn.Parts[2].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, firstTurn.Parts[3].Bucket);
        Assert.Contains("Skill", firstTurn.Parts[3].PlainText, StringComparison.Ordinal);
        Assert.Contains("deploy", firstTurn.Parts[3].PlainText, StringComparison.Ordinal);

        CollectedRecord toolRecord = batch.Records[1];
        Assert.Equal(RecordOp.ToolInvoke, toolRecord.Op);
        Assert.Equal(firstTurn.RecordId, toolRecord.ParentRecordId);
        Assert.Equal("Deployed", toolRecord.Tool!.Result!.PlainText);

        CollectedRecord secondTurn = batch.Records[2];
        Assert.Equal("synth:claude-code:sess-stream:msg-stream-2", secondTurn.RecordId);
        Assert.Equal(2, secondTurn.Parts.Count);
        Assert.Equal(ContentBucket.ToolResultContent, secondTurn.Parts[0].Bucket);
        Assert.Equal("Deployed", secondTurn.Parts[0].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, secondTurn.Parts[1].Bucket);
        Assert.Equal("All done.", secondTurn.Parts[1].PlainText);
    }

    [Fact]
    public void Map_SplitsOversizedPlainTextOnIntactSystemReminderBoundariesWithTailOnlyWithheldPart()
    {
        string beforeReminder = new('a', 63_000);
        string reminderBody = new('b', 4_000);
        string afterReminder = new('c', 5_000);
        string fullText = $"{beforeReminder}<system-reminder>{reminderBody}</system-reminder>{afterReminder}";
        string expectedTail = $"<system-reminder>{reminderBody}</system-reminder>{afterReminder}";
        ToolContentMeasurement expectedTailMeasurement = ToolContentMeasurer.Measure(expectedTail, CanonSynth.MaxToolResultBytes, "claude-sonnet-4-5");
        ToolContentMeasurement expectedFullMeasurement = ToolContentMeasurer.Measure(fullText, CanonSynth.MaxToolResultBytes, "claude-sonnet-4-5");

        string jsonl =
            $"{{\"type\":\"assistant\",\"sessionId\":\"sess-over\",\"timestamp\":\"2026-11-19T12:10:00.000Z\",\"message\":{{\"id\":\"msg-over\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{{\"type\":\"text\",\"text\":{System.Text.Json.JsonSerializer.Serialize(fullText)}}}],\"usage\":{{\"input_tokens\":10,\"output_tokens\":5}}}}}}";

        CollectedBatch batch = new ClaudeTranscriptMapper().Map(CreateUnit("/tmp/.claude/projects/repo/sess-over.jsonl", jsonl), ImportedAt);

        CollectedRecord turn = Assert.Single(batch.Records);
        Assert.Equal(2, turn.Parts.Count);

        CollectedContentPart keptPart = turn.Parts[0];
        Assert.Equal(0, keptPart.Position);
        Assert.Equal(ContentBucket.ConversationHistory, keptPart.Bucket);
        Assert.Equal(beforeReminder, keptPart.PlainText);
        Assert.Null(keptPart.Withheld);
        Assert.Equal(Encoding.UTF8.GetByteCount(fullText), keptPart.OriginalUtf8BytesWhenTruncated);
        Assert.DoesNotContain("<system-reminder>", keptPart.PlainText, StringComparison.Ordinal);

        CollectedContentPart tailPart = turn.Parts[1];
        Assert.Equal(1, tailPart.Position);
        Assert.Equal(ContentBucket.ConversationHistory, tailPart.Bucket);
        Assert.Null(tailPart.PlainText);
        Assert.NotNull(tailPart.Withheld);
        Assert.Null(tailPart.OriginalUtf8BytesWhenTruncated);
        Assert.Equal(expectedTailMeasurement.Utf8Bytes, tailPart.Withheld!.Utf8Bytes);
        Assert.Equal(expectedTailMeasurement.Characters, tailPart.Withheld.Characters);
        Assert.Equal(expectedTailMeasurement.MeasuredTokens, tailPart.Withheld.MeasuredTokens);
        Assert.True(tailPart.Withheld.MeasuredTokens < expectedFullMeasurement.MeasuredTokens);

        List<ContextPart> contextParts =
        [
            new ContextPart("conversation_history", keptPart.PlainText!, Model: "claude-sonnet-4-5"),
            new ContextPart(
                "conversation_history",
                string.Empty,
                Tokens: tailPart.Withheld.MeasuredTokens,
                Model: "claude-sonnet-4-5",
                Tokenizer: tailPart.Withheld.Tokenizer,
                TokenizerIsModelOwn: tailPart.Withheld.TokenizerIsModelOwn),
        ];
        ContextAnalysis analysis = CanonContextAnalysis.AnalyzeContext(
            [new ContextTurn(contextParts, InputTokens: 50_000, FreshInput: 50_000)],
            new ContextCountOptions(ContextLimit: 200_000),
            static (text, model) => LocalTextTokenizer.Shared.Count(text, model));
        long expectedPrefixTokens = LocalTextTokenizer.Shared.Count(beforeReminder, "claude-sonnet-4-5").Count;
        Assert.Equal(expectedPrefixTokens + expectedTailMeasurement.MeasuredTokens, analysis.Turns[0].Buckets["conversation_history"]);
    }

    [Fact]
    public void Map_KeepsIntactLeadingSystemReminderAndBacksUpMidOpeningTagSplit()
    {
        string intactReminder = "<system-reminder>Keep this instruction intact</system-reminder>";
        string padding = new('x', 65_530 - intactReminder.Length);
        string trailingReminder = "<system-reminder>Split opening tag</system-reminder>";
        string fullText = $"{intactReminder}{padding}{trailingReminder}";
        string expectedPrefix = $"{intactReminder}{padding}";
        ToolContentMeasurement expectedTailMeasurement = ToolContentMeasurer.Measure(trailingReminder, CanonSynth.MaxToolResultBytes, "claude-sonnet-4-5");

        string jsonl =
            $"{{\"type\":\"assistant\",\"sessionId\":\"sess-midtag\",\"timestamp\":\"2026-11-19T12:15:00.000Z\",\"message\":{{\"id\":\"msg-midtag\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{{\"type\":\"text\",\"text\":{System.Text.Json.JsonSerializer.Serialize(fullText)}}}],\"usage\":{{\"input_tokens\":10,\"output_tokens\":5}}}}}}";

        CollectedBatch batch = new ClaudeTranscriptMapper().Map(CreateUnit("/tmp/.claude/projects/repo/sess-midtag.jsonl", jsonl), ImportedAt);

        CollectedRecord turn = Assert.Single(batch.Records);
        Assert.Equal(2, turn.Parts.Count);
        Assert.Equal(expectedPrefix, turn.Parts[0].PlainText);
        Assert.Null(turn.Parts[0].Withheld);
        Assert.Equal(Encoding.UTF8.GetByteCount(fullText), turn.Parts[0].OriginalUtf8BytesWhenTruncated);
        Assert.Null(turn.Parts[1].PlainText);
        Assert.NotNull(turn.Parts[1].Withheld);
        Assert.Equal(expectedTailMeasurement.Utf8Bytes, turn.Parts[1].Withheld!.Utf8Bytes);
        Assert.Equal(expectedTailMeasurement.MeasuredTokens, turn.Parts[1].Withheld!.MeasuredTokens);
    }

    [Fact]
    public void Map_FoldsCompactBoundaryAndCompactSummaryOntoNextKeptLlmInvokeTurnAndRoundTrips()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"user\",\"sessionId\":\"sess-compact\",\"timestamp\":\"2026-11-19T12:20:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"First turn\"}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-compact\",\"timestamp\":\"2026-11-19T12:20:01.000Z\",\"message\":{\"id\":\"msg-c1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Answer 1\"}],\"usage\":{\"input_tokens\":50,\"output_tokens\":10}}}",
            "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"sessionId\":\"sess-compact\",\"timestamp\":\"2026-11-19T12:20:02.000Z\",\"content\":\"Conversation compacted\"}",
            "{\"type\":\"user\",\"isCompactSummary\":true,\"sessionId\":\"sess-compact\",\"timestamp\":\"2026-11-19T12:20:02.100Z\",\"message\":{\"role\":\"user\",\"content\":\"Summary of prior turns\"}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-compact\",\"timestamp\":\"2026-11-19T12:20:03.000Z\",\"message\":{\"id\":\"msg-c2\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Answer 2\"}],\"usage\":{\"input_tokens\":20,\"output_tokens\":8}}}",
            "{\"type\":\"user\",\"isCompactSummary\":true,\"sessionId\":\"sess-compact\",\"timestamp\":\"2026-11-19T12:20:04.000Z\",\"message\":{\"role\":\"user\",\"content\":\"Second summary\"}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-compact\",\"timestamp\":\"2026-11-19T12:20:04.500Z\",\"message\":{\"id\":\"msg-c-zero\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[],\"usage\":{\"input_tokens\":0,\"output_tokens\":0}}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-compact\",\"timestamp\":\"2026-11-19T12:20:05.000Z\",\"message\":{\"id\":\"msg-c3\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Answer 3\"}],\"usage\":{\"input_tokens\":30,\"output_tokens\":12}}}",
        ]);

        CollectedBatch batch = new ClaudeTranscriptMapper().Map(CreateUnit("/tmp/.claude/projects/repo/sess-compact.jsonl", jsonl), ImportedAt);

        Assert.Equal(3, batch.Records.Count);
        Assert.Null(batch.Records[0].AgentInstanceId);
        Assert.Equal("claude:compact_boundary", batch.Records[1].AgentInstanceId);
        Assert.Equal("claude:compact_boundary", batch.Records[2].AgentInstanceId);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        CollectedRecord? roundTripped = store.FindRecord(batch.Records[1].RecordId);
        Assert.NotNull(roundTripped);
        Assert.Equal("claude:compact_boundary", roundTripped!.AgentInstanceId);
    }

    [Fact]
    public void Map_IsolatesSidechainAndSubagentTranscriptsUnderParentSessionAndAgentId()
    {
        string sidechainJsonl =
            "{\"type\":\"assistant\",\"sessionId\":\"sess-main\",\"isSidechain\":true,\"agentId\":\"a1b2c3\",\"timestamp\":\"2026-11-19T12:30:00.000Z\",\"message\":{\"id\":\"msg-sub-1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Subagent output\"}],\"usage\":{\"input_tokens\":15,\"output_tokens\":5}}}";

        CollectedBatch sidechainBatch = new ClaudeTranscriptMapper().Map(
            CreateUnit("/tmp/.claude/projects/repo/sess-main.jsonl", sidechainJsonl),
            ImportedAt);

        CollectedSession sidechainSession = Assert.Single(sidechainBatch.Sessions);
        Assert.Equal("sess-main#agent-a1b2c3", sidechainSession.SessionKey);
        Assert.Equal("sess-main#agent-a1b2c3", sidechainSession.NativeSessionId);
        CollectedRecord sidechainRecord = Assert.Single(sidechainBatch.Records);
        Assert.Equal("sess-main#agent-a1b2c3", sidechainRecord.SessionKey);
        Assert.Equal("synth:claude-code:sess-main#agent-a1b2c3:msg-sub-1", sidechainRecord.RecordId);

        string nestedFallbackJsonl =
            "{\"type\":\"assistant\",\"timestamp\":\"2026-11-19T12:31:00.000Z\",\"message\":{\"id\":\"msg-sub-2\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Nested subagent\"}],\"usage\":{\"input_tokens\":12,\"output_tokens\":4}}}";

        CollectedBatch nestedBatch = new ClaudeTranscriptMapper().Map(
            CreateUnit("/tmp/.claude/projects/repo/sess-dir-parent/subagents/agent-xyz999.jsonl", nestedFallbackJsonl),
            ImportedAt);

        CollectedSession nestedSession = Assert.Single(nestedBatch.Sessions);
        Assert.Equal("sess-dir-parent#agent-xyz999", nestedSession.SessionKey);
        Assert.Equal("sess-dir-parent#agent-xyz999", nestedSession.NativeSessionId);
        CollectedRecord nestedRecord = Assert.Single(nestedBatch.Records);
        Assert.Equal("sess-dir-parent#agent-xyz999", nestedRecord.SessionKey);
        Assert.Equal("synth:claude-code:sess-dir-parent#agent-xyz999:msg-sub-2", nestedRecord.RecordId);

        string normalJsonl =
            "{\"type\":\"assistant\",\"sessionId\":\"sess-regular\",\"isSidechain\":false,\"timestamp\":\"2026-11-19T12:32:00.000Z\",\"message\":{\"id\":\"msg-norm\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Main output\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":3}}}";

        CollectedBatch normalBatch = new ClaudeTranscriptMapper().Map(
            CreateUnit("/tmp/.claude/projects/repo/sess-regular.jsonl", normalJsonl),
            ImportedAt);

        CollectedSession normalSession = Assert.Single(normalBatch.Sessions);
        Assert.Equal("sess-regular", normalSession.SessionKey);
    }

    [Fact]
    public void Map_DoesNotMergeUsageLessSidechainTailIntoParentSessionTurn()
    {
        string sidechainTailJsonl = string.Join('\n',
        [
            "{\"type\":\"assistant\",\"sessionId\":\"sess-parent\",\"timestamp\":\"2026-11-19T12:40:00.000Z\",\"message\":{\"id\":\"msg-p1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Parent reply\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":4}}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-parent\",\"isSidechain\":true,\"agentId\":\"sub1\",\"timestamp\":\"2026-11-19T12:40:01.000Z\",\"message\":{\"id\":\"msg-sub-tail\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Sidechain tail only\"}]}}",
        ]);

        CollectedBatch sidechainTailBatch = new ClaudeTranscriptMapper().Map(
            CreateUnit("/tmp/.claude/projects/repo/sess-parent.jsonl", sidechainTailJsonl),
            ImportedAt);

        CollectedRecord onlyParentRecord = Assert.Single(sidechainTailBatch.Records);
        Assert.Equal("sess-parent", onlyParentRecord.SessionKey);
        CollectedContentPart onlyParentPart = Assert.Single(onlyParentRecord.Parts);
        Assert.Equal("Parent reply", onlyParentPart.PlainText);

        string interleavedJsonl = string.Join('\n',
        [
            "{\"type\":\"assistant\",\"sessionId\":\"sess-parent\",\"timestamp\":\"2026-11-19T12:40:00.000Z\",\"message\":{\"id\":\"msg-p1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Parent reply\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":4}}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-parent\",\"isSidechain\":true,\"agentId\":\"sub1\",\"timestamp\":\"2026-11-19T12:40:02.000Z\",\"message\":{\"id\":\"msg-sub-kept\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Subagent kept\"}],\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-parent\",\"timestamp\":\"2026-11-19T12:40:03.000Z\",\"message\":{\"id\":\"msg-p-tail\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Parent tail\"}]}}",
        ]);

        CollectedBatch interleavedBatch = new ClaudeTranscriptMapper().Map(
            CreateUnit("/tmp/.claude/projects/repo/sess-parent.jsonl", interleavedJsonl),
            ImportedAt);

        Assert.Equal(2, interleavedBatch.Records.Count);
        CollectedRecord parentRecord = Assert.Single(interleavedBatch.Records, static r => string.Equals(r.SessionKey, "sess-parent", StringComparison.Ordinal));
        CollectedRecord subagentRecord = Assert.Single(interleavedBatch.Records, static r => string.Equals(r.SessionKey, "sess-parent#agent-sub1", StringComparison.Ordinal));

        Assert.Equal(2, parentRecord.Parts.Count);
        Assert.Equal("Parent reply", parentRecord.Parts[0].PlainText);
        Assert.Equal("Parent tail", parentRecord.Parts[1].PlainText);

        CollectedContentPart subPart = Assert.Single(subagentRecord.Parts);
        Assert.Equal("Subagent kept", subPart.PlainText);
    }

    [Fact]
    public void Map_UsesInjectedTextTokenizer_ForToolUseAndOverflowAndDocumentParts()
    {
        StubTextTokenizer customTokenizer = new(tokenizer: "custom_fake_tokenizer", isModelOwn: true);
        ClaudeTranscriptMapper mapper = new(customTokenizer);
        string oversizedTail = "tail one two three";
        string oversizedText = new string('a', 66_000) + "<system-reminder>" + oversizedTail + "</system-reminder>";
        string jsonl = string.Join('\n',
        [
            $"{{\"type\":\"assistant\",\"sessionId\":\"sess-tok\",\"timestamp\":\"2026-11-19T12:45:00.000Z\",\"message\":{{\"id\":\"msg-tok\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{{\"type\":\"text\",\"text\":{System.Text.Json.JsonSerializer.Serialize(oversizedText)}}},{{\"type\":\"tool_use\",\"id\":\"toolu_tok\",\"name\":\"Read\",\"input\":{{\"path\":\"/tmp/example.txt\"}}}}],\"usage\":{{\"input_tokens\":10,\"output_tokens\":5}}}}}}",
            "{\"type\":\"user\",\"sessionId\":\"sess-tok\",\"timestamp\":\"2026-11-19T12:45:01.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_tok\",\"content\":\"{\\\"ok\\\":true,\\\"count\\\":3}\"}]}}",
        ]);

        CollectedBatch batch = mapper.Map(CreateUnit("/tmp/.claude/projects/repo/sess-tok.jsonl", jsonl), ImportedAt);

        Assert.Equal(2, batch.Records.Count);
        CollectedRecord llmRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        CollectedContentPart withheldTailPart = Assert.Single(
            llmRecord.Parts,
            static p => p.Bucket == ContentBucket.ConversationHistory && p.Withheld is not null);
        Assert.Equal("custom_fake_tokenizer", withheldTailPart.Withheld!.Tokenizer);
        Assert.True(withheldTailPart.Withheld.TokenizerIsModelOwn);
        Assert.Equal(
            customTokenizer.Count("<system-reminder>" + oversizedTail + "</system-reminder>", "claude-sonnet-4-5").Count,
            withheldTailPart.Withheld.MeasuredTokens);

        CollectedRecord toolRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        CollectedContentPart docResultPart = Assert.Single(toolRecord.Parts);
        Assert.NotNull(docResultPart.Withheld);
        Assert.Equal("custom_fake_tokenizer", docResultPart.Withheld!.Tokenizer);
        Assert.True(docResultPart.Withheld.TokenizerIsModelOwn);
    }

    [Fact]
    public void AddDashDataServices_InjectsRegisteredTextTokenizerIntoClaudeTranscriptMapper()
    {
        StubTextTokenizer customTokenizer = new(tokenizer: "di_registered_tokenizer", isModelOwn: false);
        ServiceCollection services = new();
        services.AddSingleton<ITextTokenizer>(customTokenizer);
        _ = services.AddDashDataServices(new KyberDash.Contracts.Services.DashOptions());

        using ServiceProvider provider = services.BuildServiceProvider();
        ClaudeTranscriptMapper claudeMapper = Assert.Single(
            provider.GetServices<IHarnessMapper>().OfType<ClaudeTranscriptMapper>());

        string oversizedText = new string('x', 66_000) + "<system-reminder>alpha beta gamma</system-reminder>";
        string jsonl =
            $"{{\"type\":\"assistant\",\"sessionId\":\"sess-di\",\"timestamp\":\"2026-11-19T12:50:00.000Z\",\"message\":{{\"id\":\"msg-di\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{{\"type\":\"text\",\"text\":{System.Text.Json.JsonSerializer.Serialize(oversizedText)}}}],\"usage\":{{\"input_tokens\":10,\"output_tokens\":5}}}}}}";

        CollectedBatch batch = claudeMapper.Map(CreateUnit("/tmp/.claude/projects/repo/sess-di.jsonl", jsonl), ImportedAt);

        CollectedRecord record = Assert.Single(batch.Records);
        CollectedContentPart withheldPart = Assert.Single(record.Parts, static p => p.Withheld is not null);
        Assert.Equal("di_registered_tokenizer", withheldPart.Withheld!.Tokenizer);
        Assert.False(withheldPart.Withheld.TokenizerIsModelOwn);
    }

    private sealed class StubTextTokenizer(string tokenizer, bool isModelOwn) : ITextTokenizer
    {
        public TokenCount Count(string text, string? model)
        {
            _ = model;
            int count = string.IsNullOrWhiteSpace(text)
                ? 0
                : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            return new TokenCount(count, tokenizer, isModelOwn);
        }
    }

    private static NativeSourceUnit CreateUnit(string sourceKey, string? rawText) =>
        new(
            Unit: new SourceUnit(
                HarnessId: TranscriptReaderRepository.ClaudeHarnessId,
                SourceKey: sourceKey,
                ProviderId: TranscriptReaderRepository.ClaudeProviderId,
                ParserId: TranscriptReaderRepository.ClaudeParserId,
                ParserContractVersion: HarnessSourceRegistry.ClaudeTranscriptV2ParserContractVersion,
                Format: TranscriptReaderRepository.ClaudeFormat,
                SourceRootLabel: "~/.claude/projects",
                RevisionToken: "1:100"),
            RawText: rawText,
            SizeBytes: rawText is null ? 0L : Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: ImportedAt);

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
