using System.Security.Cryptography;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Claude;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
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
        Assert.Equal(CanonTokenCache.ApproximateO200kBase(jsonDocResult), docPart.Withheld.MeasuredTokens);

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
        Assert.Equal(5, parent.Parts.Count);
        Assert.Equal("42", parent.Parts[0].PlainText);
        Assert.Equal("true", parent.Parts[1].PlainText);
        Assert.Equal("false", parent.Parts[2].PlainText);
        Assert.Equal("99.5", parent.Parts[3].PlainText);
        Assert.Equal("true", parent.Parts[4].PlainText);
        Assert.Equal(ContentBucket.ToolResultContent, parent.Parts[4].Bucket);
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
        Assert.Equal(3, parent.Parts.Count);
        Assert.Equal(ContentBucket.ConversationHistory, parent.Parts[0].Bucket);
        Assert.Equal(ContentBucket.ConversationHistory, parent.Parts[1].Bucket);
        Assert.Equal(ContentBucket.ToolResultContent, parent.Parts[2].Bucket);
        Assert.Equal("Written", parent.Parts[2].PlainText);
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

    private static NativeSourceUnit CreateUnit(string sourceKey, string? rawText) =>
        new(
            Unit: new SourceUnit(
                HarnessId: TranscriptReaderRepository.ClaudeHarnessId,
                SourceKey: sourceKey,
                ProviderId: TranscriptReaderRepository.ClaudeProviderId,
                ParserId: TranscriptReaderRepository.ClaudeParserId,
                ParserContractVersion: TranscriptReaderRepository.ParserContractVersion,
                Format: TranscriptReaderRepository.ClaudeFormat,
                SourceRootLabel: "~/.claude/projects",
                RevisionToken: "1:100"),
            RawText: rawText,
            SizeBytes: rawText is null ? 0L : Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: ImportedAt);

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
