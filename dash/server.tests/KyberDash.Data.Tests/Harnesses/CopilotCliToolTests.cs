using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.CopilotCli;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CopilotCliMapper"/> tool request correlation, JSON result withholding,
/// incomplete tool handling, MCP server/tool attribution, and <c>skill.invoked</c> mapping.
/// </summary>
public class CopilotCliToolTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_CorrelatesToolRequestsExecutionStartAndComplete_IntoChildToolInvokeLinkedToMessage()
    {
        string jsonl = string.Join(
            '\n',
            [
                """{"type":"session.start","id":"e1","timestamp":"2026-11-19T10:00:00.000Z","data":{"sessionId":"sess-tool-1","selectedModel":"claude-sonnet-4.5"}}""",
                """{"type":"user.message","id":"e2","timestamp":"2026-11-19T10:00:01.000Z","data":{"content":"Check git status and list files."}}""",
                """{"type":"assistant.message","id":"e3","timestamp":"2026-11-19T10:00:02.000Z","data":{"messageId":"msg-1","content":"Running git status.","outputTokens":30,"toolRequests":[{"toolCallId":"call_1","name":"bash","type":"function","arguments":{"command":"git status","timeout":5000}}]}}""",
                """{"type":"tool.execution_start","id":"e4","parentId":"e3","timestamp":"2026-11-19T10:00:02.100Z","data":{"toolCallId":"call_1","toolName":"bash","arguments":{"command":"git status","timeout":5000}}}""",
                """{"type":"tool.execution_complete","id":"e5","parentId":"e4","timestamp":"2026-11-19T10:00:02.450Z","data":{"toolCallId":"call_1","toolName":"bash","success":true,"result":{"content":"On branch main\nnothing to commit"}}}""",
                """{"type":"assistant.message","id":"e6","timestamp":"2026-11-19T10:00:03.000Z","data":{"messageId":"msg-2","content":"Clean working tree.","outputTokens":12}}""",
            ]) + "\n";

        NativeSourceUnit unit = MakeUnit("sess-tool-1", jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Equal(3, batch.Records.Count);

        List<CollectedRecord> llmRecords = [.. batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke)];
        Assert.Equal(2, llmRecords.Count);
        CollectedRecord firstTurn = llmRecords[0];
        CollectedRecord toolRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);

        Assert.Equal("copilot-cli", toolRecord.HarnessId);
        Assert.Equal(RecordKind.Internal, toolRecord.Kind);
        Assert.Equal("bash", toolRecord.Name);
        Assert.Equal(firstTurn.RecordId, toolRecord.ParentRecordId);
        Assert.Equal(firstTurn.SessionKey, toolRecord.SessionKey);
        Assert.Equal(firstTurn.TraceId, toolRecord.TraceId);
        Assert.Equal(RecordStatus.Ok, toolRecord.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:02.100Z", CultureInfo.InvariantCulture), toolRecord.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:02.450Z", CultureInfo.InvariantCulture), toolRecord.EndedAt);
        Assert.Equal(350L, toolRecord.ReportedDurationMs);

        Assert.NotNull(toolRecord.Tool);
        Assert.Equal("call_1", toolRecord.Tool.CallId);
        Assert.Equal("bash", toolRecord.Tool.ToolName);
        Assert.True(toolRecord.Tool.Succeeded);
        Assert.Null(toolRecord.Tool.Error);

        Assert.NotNull(toolRecord.Tool.Input);
        Assert.Null(toolRecord.Tool.Input.PlainText);
        Assert.True(toolRecord.Tool.Input.Utf8Bytes > 0);
        Assert.True(toolRecord.Tool.Input.Characters > 0);
        Assert.Equal(Sha256Hex("{\"command\":\"git status\",\"timeout\":5000}"), toolRecord.Tool.Input.Sha256Fingerprint);

        Assert.NotNull(toolRecord.Tool.Result);
        Assert.Equal("On branch main\nnothing to commit", toolRecord.Tool.Result.PlainText);
        Assert.Equal(Encoding.UTF8.GetByteCount("On branch main\nnothing to commit"), toolRecord.Tool.Result.Utf8Bytes);
        Assert.Equal("On branch main\nnothing to commit".Length, toolRecord.Tool.Result.Characters);

        CollectedContentPart resultPart = Assert.Single(toolRecord.Parts);
        Assert.Equal(ContentBucket.ToolResultContent, resultPart.Bucket);
        Assert.Equal("On branch main\nnothing to commit", resultPart.PlainText);
        Assert.Null(resultPart.Withheld);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        CollectedRecord? storedTool = store.GetRecord(toolRecord.RecordId);
        Assert.NotNull(storedTool);
        Assert.Equal(firstTurn.RecordId, storedTool.ParentRecordId);
        Assert.Equal(toolRecord.EndedAt, storedTool.EndedAt);
        Assert.Equal(350L, storedTool.ReportedDurationMs);
        Assert.True(storedTool.Tool?.Succeeded);
    }

    [Fact]
    public void Map_JsonDocumentToolResult_WithholdsPlainTextAndKeepsSizeOnly()
    {
        string jsonPayload = "{\"status\":\"ok\",\"files\":[\"a.cs\",\"b.cs\"]}";
        string escapedPayload = System.Text.Json.JsonSerializer.Serialize(jsonPayload);
        string jsonl = string.Join(
            '\n',
            [
                """{"type":"session.start","id":"e1","timestamp":"2026-11-19T10:00:00.000Z","data":{"sessionId":"sess-json-res","selectedModel":"gpt-5"}}""",
                """{"type":"assistant.message","id":"e2","timestamp":"2026-11-19T10:00:01.000Z","data":{"messageId":"msg-1","content":"Reading structured output.","toolRequests":[{"toolCallId":"call_json_str","name":"inspect_str","arguments":{"path":"a.cs"}},{"toolCallId":"call_json_obj","name":"inspect_obj","arguments":{"path":"b.cs"}}]}}""",
                """{"type":"tool.execution_start","id":"e3","timestamp":"2026-11-19T10:00:01.100Z","data":{"toolCallId":"call_json_str","toolName":"inspect_str"}}""",
                "{\"type\":\"tool.execution_complete\",\"id\":\"e4\",\"timestamp\":\"2026-11-19T10:00:01.200Z\",\"data\":{\"toolCallId\":\"call_json_str\",\"success\":true,\"result\":{\"content\":" + escapedPayload + "}}}",
                """{"type":"tool.execution_start","id":"e5","timestamp":"2026-11-19T10:00:01.300Z","data":{"toolCallId":"call_json_obj","toolName":"inspect_obj"}}""",
                """{"type":"tool.execution_complete","id":"e6","timestamp":"2026-11-19T10:00:01.400Z","data":{"toolCallId":"call_json_obj","success":true,"result":{"items":[1,2,3],"nested":{"ok":true}}}}""",
            ]) + "\n";

        NativeSourceUnit unit = MakeUnit("sess-json-res", jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        List<CollectedRecord> tools = [.. batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke)];
        Assert.Equal(2, tools.Count);

        CollectedRecord strResultTool = tools[0];
        Assert.NotNull(strResultTool.Tool?.Result);
        Assert.Null(strResultTool.Tool.Result.PlainText);
        Assert.Null(strResultTool.Tool.Result.Sha256Fingerprint);
        Assert.Null(strResultTool.Tool.Result.OriginalUtf8BytesWhenTruncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(jsonPayload), strResultTool.Tool.Result.Utf8Bytes);
        Assert.Equal(jsonPayload.Length, strResultTool.Tool.Result.Characters);

        CollectedContentPart strPart = Assert.Single(strResultTool.Parts);
        Assert.Equal(ContentBucket.ToolResultContent, strPart.Bucket);
        Assert.Null(strPart.PlainText);
        Assert.NotNull(strPart.Withheld);
        Assert.Equal(Encoding.UTF8.GetByteCount(jsonPayload), strPart.Withheld.Utf8Bytes);

        CollectedRecord objResultTool = tools[1];
        Assert.NotNull(objResultTool.Tool?.Result);
        Assert.Null(objResultTool.Tool.Result.PlainText);
        Assert.Null(objResultTool.Tool.Result.Sha256Fingerprint);
        Assert.True(objResultTool.Tool.Result.Utf8Bytes > 0);
        Assert.True(objResultTool.Tool.Result.Characters > 0);

        CollectedContentPart objPart = Assert.Single(objResultTool.Parts);
        Assert.Equal(ContentBucket.ToolResultContent, objPart.Bucket);
        Assert.Null(objPart.PlainText);
        Assert.NotNull(objPart.Withheld);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        IReadOnlyList<StoredToolCall> storedCalls = store.ToolCalls(sessionKey: "sess-json-res");
        Assert.Equal(2, storedCalls.Count);
        Assert.All(storedCalls, static call =>
        {
            Assert.NotNull(call.Call.Result);
            Assert.Null(call.Call.Result.PlainText);
            Assert.True(call.Call.Result.Utf8Bytes > 0);
            Assert.True(call.Call.Result.Characters > 0);
        });
    }

    [Fact]
    public void Map_IncompleteToolCall_OmitsRecordEndAndToolCallOutcome()
    {
        string jsonl = string.Join(
            '\n',
            [
                """{"type":"session.start","id":"e1","timestamp":"2026-11-19T10:00:00.000Z","data":{"sessionId":"sess-incomplete","selectedModel":"gpt-5"}}""",
                """{"type":"assistant.message","id":"e2","timestamp":"2026-11-19T10:00:01.000Z","data":{"messageId":"msg-inc","content":"Starting build.","toolRequests":[{"toolCallId":"call_started_only","name":"bash","arguments":{"command":"dotnet test"}},{"toolCallId":"call_requested_only","name":"read_file","arguments":{"path":"README.md"}}]}}""",
                """{"type":"tool.execution_start","id":"e3","parentId":"e2","timestamp":"2026-11-19T10:00:01.200Z","data":{"toolCallId":"call_started_only","toolName":"bash"}}""",
            ]) + "\n";

        NativeSourceUnit unit = MakeUnit("sess-incomplete", jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        CollectedRecord parent = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        CollectedRecord startedOnly = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);

        Assert.Equal(parent.RecordId, startedOnly.ParentRecordId);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:01.200Z", CultureInfo.InvariantCulture), startedOnly.StartedAt);
        Assert.Null(startedOnly.EndedAt);
        Assert.Null(startedOnly.ReportedDurationMs);
        Assert.Equal(RecordStatus.Unset, startedOnly.Status);
        Assert.Empty(startedOnly.Parts);
        Assert.NotNull(startedOnly.Tool);
        Assert.Equal("call_started_only", startedOnly.Tool.CallId);
        Assert.Equal("bash", startedOnly.Tool.ToolName);
        Assert.Null(startedOnly.Tool.Succeeded);
        Assert.Null(startedOnly.Tool.Result);
        Assert.Null(startedOnly.Tool.Error);
        Assert.NotNull(startedOnly.Tool.Input);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        CollectedRecord? stored = store.GetRecord(startedOnly.RecordId);
        Assert.NotNull(stored);
        Assert.Null(stored.EndedAt);
        Assert.Equal(RecordStatus.Unset, stored.Status);
        Assert.Null(stored.Tool?.Succeeded);
        Assert.Null(stored.Tool?.Result);
    }

    [Fact]
    public void Map_McpToolAndSkillInvoked_PopulateMcpAttributionAndRecordSkillsWithoutDuplicates()
    {
        string jsonl = string.Join(
            '\n',
            [
                """{"type":"session.start","id":"e1","timestamp":"2026-11-19T10:00:00.000Z","data":{"sessionId":"sess-mcp-skill","selectedModel":"claude-sonnet-4.5"}}""",
                """{"type":"assistant.message","id":"e2","timestamp":"2026-11-19T10:00:01.000Z","data":{"messageId":"msg-1","content":"Loading skill and querying docs.","toolRequests":[{"toolCallId":"call_skill_1","name":"skill","arguments":{"skill":"csharp-dev"}},{"toolCallId":"call_mcp_1","name":"kyber_docs-docs_search","type":"mcp","mcpServerName":"kyber-docs","mcpToolName":"docs_answer_question_from_repo_documentation","arguments":{"query":"tool_call_mcp"}},{"toolCallId":"call_mcp_2","name":"mcp__codegraph__codegraph_explore","arguments":{"query":"CopilotCliMapper"}}]}}""",
                """{"type":"tool.execution_start","id":"e3","parentId":"e2","timestamp":"2026-11-19T10:00:01.050Z","data":{"toolCallId":"call_skill_1","toolName":"skill"}}""",
                """{"type":"skill.invoked","id":"e4","parentId":"e3","timestamp":"2026-11-19T10:00:01.060Z","data":{"name":"csharp-dev","toolCallId":"call_skill_1"}}""",
                """{"type":"skill.invoked","id":"e5","parentId":"e2","timestamp":"2026-11-19T10:00:01.070Z","data":{"skillName":"test"}}""",
                """{"type":"tool.execution_complete","id":"e6","parentId":"e3","timestamp":"2026-11-19T10:00:01.100Z","data":{"toolCallId":"call_skill_1","success":true,"result":{"content":"Skill loaded"}}}""",
                """{"type":"tool.execution_start","id":"e7","parentId":"e2","timestamp":"2026-11-19T10:00:01.200Z","data":{"toolCallId":"call_mcp_1","toolName":"kyber_docs-docs_search","mcpServerName":"kyber-docs","mcpToolName":"docs_answer_question_from_repo_documentation"}}""",
                """{"type":"tool.execution_complete","id":"e8","parentId":"e7","timestamp":"2026-11-19T10:00:01.350Z","data":{"toolCallId":"call_mcp_1","success":true,"result":{"content":"Found 1 match"}}}""",
                """{"type":"tool.execution_start","id":"e9","parentId":"e2","timestamp":"2026-11-19T10:00:01.400Z","data":{"toolCallId":"call_mcp_2","toolName":"mcp__codegraph__codegraph_explore"}}""",
                """{"type":"tool.execution_complete","id":"e10","parentId":"e9","timestamp":"2026-11-19T10:00:01.600Z","data":{"toolCallId":"call_mcp_2","success":true,"result":{"content":"Explored symbol"}}}""",
                """{"type":"assistant.message","id":"e11","timestamp":"2026-11-19T10:00:02.000Z","data":{"messageId":"msg-2","content":"Done."}}""",
            ]) + "\n";

        NativeSourceUnit unit = MakeUnit("sess-mcp-skill", jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        List<CollectedRecord> llmRecords = [.. batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke)];
        Assert.Equal(2, llmRecords.Count);

        CollectedRecord turn1 = llmRecords[0];
        Assert.Equal(["csharp-dev", "test"], turn1.SkillsInvoked);
        Assert.Empty(llmRecords[1].SkillsInvoked);

        List<CollectedRecord> toolRecords = [.. batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke)];
        Assert.Equal(3, toolRecords.Count);
        Assert.All(toolRecords, tr => Assert.Equal(turn1.RecordId, tr.ParentRecordId));

        CollectedRecord explicitMcp = toolRecords[1];
        Assert.NotNull(explicitMcp.Tool);
        Assert.Equal("kyber-docs", explicitMcp.Tool.McpServerName);
        Assert.Equal("docs_answer_question_from_repo_documentation", explicitMcp.Tool.McpToolName);

        CollectedRecord prefixedMcp = toolRecords[2];
        Assert.NotNull(prefixedMcp.Tool);
        Assert.Equal("codegraph", prefixedMcp.Tool.McpServerName);
        Assert.Equal("codegraph_explore", prefixedMcp.Tool.McpToolName);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        IReadOnlyList<StoredSkillInvocation> storedSkills = store.SkillsInvoked(spanId: turn1.RecordId);
        Assert.Equal(["csharp-dev", "test"], [.. storedSkills.Select(static s => s.SkillName)]);

        StoredToolCall? storedMcp1 = store.ToolCalls(spanId: explicitMcp.RecordId).SingleOrDefault();
        Assert.NotNull(storedMcp1);
        Assert.Equal("kyber-docs", storedMcp1.Call.McpServerName);
        Assert.Equal("docs_answer_question_from_repo_documentation", storedMcp1.Call.McpToolName);

        StoredToolCall? storedMcp2 = store.ToolCalls(spanId: prefixedMcp.RecordId).SingleOrDefault();
        Assert.NotNull(storedMcp2);
        Assert.Equal("codegraph", storedMcp2.Call.McpServerName);
        Assert.Equal("codegraph_explore", storedMcp2.Call.McpToolName);
    }

    [Fact]
    public void Map_FailedToolAndMultiMessageTurns_SetErrorSignalsAndLinkEachToolToItsRequestingMessage()
    {
        string jsonl = string.Join(
            '\n',
            [
                """{"type":"session.start","id":"e1","timestamp":"2026-11-19T10:00:00.000Z","data":{"sessionId":"sess-fail","selectedModel":"gpt-5"}}""",
                """{"type":"assistant.message","id":"e2","timestamp":"2026-11-19T10:00:01.000Z","data":{"messageId":"msg-a","content":"First try.","toolRequests":[{"toolCallId":"call_fail","name":"bash","arguments":{"command":"false"}}]}}""",
                """{"type":"tool.execution_start","id":"e3","timestamp":"2026-11-19T10:00:01.100Z","data":{"toolCallId":"call_fail","toolName":"bash"}}""",
                """{"type":"tool.execution_complete","id":"e4","timestamp":"2026-11-19T10:00:01.250Z","data":{"toolCallId":"call_fail","success":false,"exitCode":2,"error":{"message":"Command exited with status 2","code":"EXIT_NONZERO"},"result":{"content":"{\"stderr\":\"boom\"}"}}}""",
                """{"type":"assistant.message","id":"e5","agentId":"sub-worker","timestamp":"2026-11-19T10:00:02.000Z","data":{"messageId":"msg-b","content":"Subagent turn.","toolRequests":[{"toolCallId":"call_sub","name":"edit_file","arguments":"plain patch text"}]}}""",
                """{"type":"tool.execution_start","id":"e6","agentId":"sub-worker","timestamp":"2026-11-19T10:00:02.050Z","data":{"toolCallId":"call_sub","toolName":"edit_file"}}""",
                """{"type":"skill.invoked","id":"e7","agentId":"sub-worker","timestamp":"2026-11-19T10:00:02.075Z","data":{"skill":"rust-dev"}}""",
                """{"type":"tool.execution_complete","id":"e8","agentId":"sub-worker","timestamp":"2026-11-19T10:00:02.300Z","data":{"toolCallId":"call_sub","isError":false,"durationMs":250,"result":{"content":"Edited."}}}""",
            ]) + "\n";

        NativeSourceUnit unit = MakeUnit("sess-fail", jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        List<CollectedRecord> llmRecords = [.. batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke)];
        Assert.Equal(2, llmRecords.Count);
        CollectedRecord msgA = llmRecords[0];
        CollectedRecord msgB = llmRecords[1];

        Assert.Empty(msgA.SkillsInvoked);
        Assert.Equal(["rust-dev"], msgB.SkillsInvoked);

        List<CollectedRecord> tools = [.. batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke)];
        Assert.Equal(2, tools.Count);

        CollectedRecord failedTool = tools[0];
        Assert.Equal(msgA.RecordId, failedTool.ParentRecordId);
        Assert.Equal(RecordStatus.Error, failedTool.Status);
        Assert.False(failedTool.Tool?.Succeeded);
        Assert.Equal(2L, failedTool.Tool?.ExitCode);
        Assert.NotNull(failedTool.Tool?.Error);
        Assert.Equal("Command exited with status 2", failedTool.Tool.Error.Message);
        Assert.Equal("EXIT_NONZERO", failedTool.Tool.Error.Code);
        Assert.Equal("Command exited with status 2", failedTool.Signals.ErrorMessage);
        Assert.Null(failedTool.Tool.Result?.PlainText);
        Assert.True(failedTool.Tool.Result?.Utf8Bytes > 0);

        CollectedRecord subTool = tools[1];
        Assert.Equal(msgB.RecordId, subTool.ParentRecordId);
        Assert.Equal("sub-worker", subTool.AgentInstanceId);
        Assert.Equal(RecordStatus.Ok, subTool.Status);
        Assert.True(subTool.Tool?.Succeeded);
        Assert.Equal("plain patch text", subTool.Tool?.Input?.PlainText);
        Assert.Equal("Edited.", subTool.Tool?.Result?.PlainText);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        Assert.Equal(4, store.CountRecords("copilot-cli"));
    }

    private static NativeSourceUnit MakeUnit(string sessionId, string rawText)
    {
        string path = Path.Combine(
            Path.GetPathRoot(Environment.CurrentDirectory) ?? "/",
            "fake-home",
            ".copilot",
            "session-state",
            sessionId,
            "events.jsonl");

        SourceUnit sourceUnit = new(
            HarnessId: "copilot-cli",
            SourceKey: Path.GetFullPath(path),
            ProviderId: "copilot",
            ParserId: "copilot-cli-reader",
            ParserContractVersion: "1",
            Format: "copilot-cli-jsonl",
            SourceRootLabel: sessionId,
            RevisionToken: "1-1",
            LastWriteUtc: BaselineUtc,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText));

        return new NativeSourceUnit(
            Unit: sourceUnit,
            RawText: rawText,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: BaselineUtc);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
