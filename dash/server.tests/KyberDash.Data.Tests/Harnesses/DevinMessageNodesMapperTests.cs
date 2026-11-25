using System.Globalization;
using System.Text;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Devin;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for the <c>message_nodes</c> export parse path of <see cref="DevinMapper"/>
/// (the Devin export contract the <c>sessions.db</c> reader hands the mapper) and for its
/// null and whitespace <see cref="NativeSourceUnit.RawText"/> handling.
/// </summary>
public class DevinMessageNodesMapperTests
{
    private const string SourceKey = "/home/u/.local/share/devin/cli/sessions.db#session-1";

    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private const string Export = """
        {
          "session_id": "session-1",
          "model": "export-model",
          "title": "Fix the build",
          "working_directory": "/work/repo",
          "created_at": "2026-11-25T08:00:00Z",
          "last_activity_at": "2026-11-25T08:10:00Z",
          "session_metadata": {},
          "message_nodes": [
            { "node_id": "n1", "parent_node_id": null, "created_at": "2026-11-25T08:00:00Z",
              "chat_message": { "message_id": "m1", "role": "user", "content": "first question",
                "metadata": { "is_user_input": true, "created_at": "2026-11-25T08:00:00Z" } } },
            { "node_id": "n2", "parent_node_id": "n1", "created_at": "2026-11-25T08:00:01Z",
              "chat_message": { "message_id": "m2", "role": "assistant", "content": "answer",
                "thinking": "hmm",
                "tool_calls": [
                  { "id": "tc1", "name": "read_file", "arguments": { "path": "a.cs" }, "index": 0, "kind": "function" },
                  { "id": "tc2", "name": "grep", "arguments": { "pattern": "x" }, "index": 1, "kind": "function" }
                ],
                "metadata": {
                  "created_at": "2026-11-25T08:00:02Z",
                  "generation_model": "generation-model",
                  "finish_reason": "tool_calls",
                  "metrics": { "input_tokens": 100, "output_tokens": 20, "cache_read_tokens": 5,
                    "cache_creation_tokens": 10, "ttft_ms": 300, "total_time_ms": 900 } } } },
            { "node_id": "n3", "parent_node_id": "n2", "created_at": "2026-11-25T08:00:03Z",
              "chat_message": { "message_id": "m3", "role": "tool", "tool_call_id": "tc1", "content": "file text" } },
            { "node_id": "n4", "parent_node_id": "n3", "created_at": "2026-11-25T08:00:04Z",
              "chat_message": { "message_id": "m4", "role": "system", "content": "system prompt" } },
            { "node_id": "n5", "parent_node_id": "n4", "created_at": "2026-11-25T08:01:00Z",
              "chat_message": { "message_id": "m5", "role": "user", "content": "second question",
                "metadata": { "is_user_input": true } } },
            { "node_id": "n6", "parent_node_id": "n5", "created_at": "2026-11-25T08:01:01Z",
              "chat_message": { "message_id": "m6", "role": "assistant", "content": "no metrics here",
                "metadata": { "created_at": "2026-11-25T08:01:02Z" } } }
          ]
        }
        """;

    [Fact]
    public void Map_MessageNodesExport_MapsAssistantNodesToLlmInvokeRecordsWithToolChildren()
    {
        CollectedBatch batch = new DevinMapper().Map(MakeUnit(Export), BaselineUtc);

        Assert.Empty(batch.Problems);
        List<CollectedRecord> llm = batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke).ToList();
        Assert.Equal(2, llm.Count);

        CollectedRecord first = llm[0];
        Assert.Equal("generation-model", first.Model);
        Assert.Equal("synth:devin:session-1:m2", first.RecordId);
        Assert.Equal(
            DateTimeOffset.Parse("2026-11-25T08:00:02Z", CultureInfo.InvariantCulture),
            first.StartedAt);
        Assert.Contains(first.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 100);
        Assert.Contains(first.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 20);
        Assert.Contains(first.Tokens, static t => t.TokenClass == TokenClass.CacheRead && t.Count == 5);
        Assert.Contains(first.Tokens, static t => t.TokenClass == TokenClass.CacheWrite && t.Count == 10);
        Assert.Null(first.ReportedCost);
        Assert.Equal(ContentBucket.UserMessage, first.Parts[0].Bucket);
        Assert.Equal("first question", first.Parts[0].PlainText);

        List<CollectedRecord> tools = batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke).ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal("tc1", tools[0].Tool!.CallId);
        Assert.Equal("read_file", tools[0].Tool!.ToolName);
        Assert.NotNull(tools[0].Tool!.Input);
        Assert.Equal("grep", tools[1].Tool!.ToolName);
        Assert.All(tools, t => Assert.Equal(first.RecordId, t.ParentRecordId));
    }

    [Fact]
    public void Map_MessageNodesExport_AssistantWithoutMetricsHasNoTokensAndUsesExportModel()
    {
        CollectedBatch batch = new DevinMapper().Map(MakeUnit(Export), BaselineUtc);

        CollectedRecord second = batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke).ElementAt(1);
        Assert.Equal("export-model", second.Model);
        Assert.Empty(second.Tokens);
        Assert.Null(second.ReportedCost);
        Assert.Equal("second question", second.Parts[0].PlainText);
    }

    [Fact]
    public void Map_MessageNodesExport_ToolAndSystemNodesEmitNoRecords()
    {
        CollectedBatch batch = new DevinMapper().Map(MakeUnit(Export), BaselineUtc);

        Assert.Equal(4, batch.Records.Count);
        Assert.DoesNotContain(
            batch.Records,
            static r => r.RecordId.EndsWith(":m3", StringComparison.Ordinal)
                || r.RecordId.EndsWith(":m4", StringComparison.Ordinal));
    }

    [Fact]
    public void Map_MessageNodesExport_SessionCarriesTitleAndWorkingDirectory()
    {
        CollectedBatch batch = new DevinMapper().Map(MakeUnit(Export), BaselineUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("session-1", session.SessionKey);
        Assert.Equal("Fix the build", session.Title);
        Assert.Equal("/work/repo", session.Workspace?.WorkingDirectory);
    }

    [Fact]
    public void Map_MessageNodesExport_NodeWithoutChatMessageObjectIsOneProblemAndOthersStillMap()
    {
        string raw = """
            {
              "session_id": "session-2",
              "model": "export-model",
              "message_nodes": [
                { "node_id": "n1", "chat_message": "not an object" },
                { "node_id": "n2", "chat_message": { "message_id": "m2", "role": "assistant",
                    "metadata": { "metrics": { "input_tokens": 7 } } } }
              ]
            }
            """;

        CollectedBatch batch = new DevinMapper().Map(MakeUnit(raw), BaselineUtc);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(DevinMapper.MalformedDocumentCode, problem.Code);
        Assert.Equal("warning", problem.Severity);
        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 7);
    }

    [Fact]
    public void Map_MessageNodesExport_UnrecognisedRoleIsOneProblem()
    {
        string raw = """
            { "session_id": "s", "message_nodes": [
              { "node_id": "n1", "chat_message": { "message_id": "m1", "role": "narrator", "content": "x" } } ] }
            """;

        CollectedBatch batch = new DevinMapper().Map(MakeUnit(raw), BaselineUtc);

        Assert.Equal(DevinMapper.MalformedDocumentCode, Assert.Single(batch.Problems).Code);
        Assert.Empty(batch.Records);
    }

    [Fact]
    public void Parse_MessageNodesRoot_IsValidWithUserAndAssistantSteps()
    {
        DevinTranscriptDocument document = DevinMapper.Parse(Export);

        Assert.True(document.IsValid);
        Assert.Equal("session-1", document.SessionId);
        Assert.Equal("export-model", document.AgentModelName);
        Assert.Equal(4, document.Steps.Count);
        Assert.True(document.Steps[0].Metadata!.IsUserInput);
        Assert.Equal("first question", document.Steps[0].MessageText);
        Assert.Equal("generation-model", document.Steps[1].ModelName);
        Assert.Equal("{ \"path\": \"a.cs\" }", document.Steps[1].ToolCalls[0].ArgumentsJson);
    }

    [Fact]
    public void Map_NullRawText_IsOneWarningProblemNamingTheSourceKey()
    {
        NativeSourceUnit unit = MakeUnit(Export) with { RawText = null };

        CollectedBatch batch = new DevinMapper().Map(unit, BaselineUtc);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal("warning", problem.Severity);
        Assert.Equal(DevinMapper.MalformedDocumentCode, problem.Code);
        Assert.Contains(SourceKey, problem.Message, StringComparison.Ordinal);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    public void Map_WhitespaceRawText_StaysSilentlyEmpty(string raw)
    {
        CollectedBatch batch = new DevinMapper().Map(MakeUnit(raw), BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
    }

    private static NativeSourceUnit MakeUnit(string rawText)
    {
        SourceUnit sourceUnit = new(
            HarnessId: "devin",
            SourceKey: SourceKey,
            ProviderId: "devin",
            ParserId: "devin-reader",
            ParserContractVersion: "1",
            Format: "devin-test",
            SourceRootLabel: "devin",
            RevisionToken: "1-1",
            LastWriteUtc: BaselineUtc,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText));

        return new NativeSourceUnit(sourceUnit, rawText, Encoding.UTF8.GetByteCount(rawText), BaselineUtc);
    }
}
