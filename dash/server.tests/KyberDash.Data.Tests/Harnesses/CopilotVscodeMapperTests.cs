using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.CopilotVscode;
using KyberDash.Data.Tokenization;
using KyberDash.Domain;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CopilotVscodeMapper"/> covering VS Code <c>chatSessions</c> journal
/// replay, cumulative turn parts, token/model mapping, tool-call round extraction, and field
/// register completeness.
/// </summary>
public class CopilotVscodeMapperTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HarnessId_IsCopilotVscode()
    {
        CopilotVscodeMapper mapper = new();
        Assert.Equal("copilot-vscode", mapper.HarnessId);
    }

    [Fact]
    public void Map_ReplaysMultiTurnJournalWithCumulativeHistoryAndExcludesCurrentResponse()
    {
        string jsonl = string.Join('\n',
        [
            """{"kind":0,"v":{"sessionId":"vscode-static-session","creationDate":"2026-09-22T10:00:00.000Z","requests":[]}}""",
            """{"kind":2,"k":["requests"],"v":[{"requestId":"request-1","timestamp":"2026-09-22T10:01:00.000Z","modelId":"copilot/gpt-5","message":{"text":"first request","parts":[{"type":"text","text":"first request"}],"modeInstructions":"Work in agent mode and keep edits scoped."},"modeInstructions":"Work in agent mode and keep edits scoped.","result":{"metadata":{"promptTokens":180,"outputTokens":30,"resolvedModel":"gpt-5"}},"response":{"text":"first response","parts":[{"type":"text","text":"first response"}]}},{"requestId":"request-2","timestamp":"2026-09-22T10:02:00.000Z","modelId":"copilot/gpt-5","message":{"text":"second request","parts":[{"type":"text","text":"second request"}],"modeInstructions":"Work in agent mode and keep edits scoped."},"modeInstructions":"Work in agent mode and keep edits scoped.","result":{"metadata":{"promptTokens":240,"outputTokens":40,"resolvedModel":"gpt-5"}},"response":{"text":"second response","parts":[{"type":"text","text":"second response"}]}}]}""",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("/fake-home/ws1/chatSessions/vscode-static-session.jsonl", jsonl);
        CopilotVscodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Equal("copilot-vscode", batch.Source.HarnessId);
        Assert.Equal(SourceKind.File, batch.Source.Kind);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("vscode-static-session", session.SessionKey);
        Assert.Equal("vscode-static-session", session.NativeSessionId);
        Assert.Equal("copilot-vscode", session.HarnessId);

        Assert.Equal(2, batch.Records.Count);

        CollectedRecord first = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, first.Op);
        Assert.Equal("gpt-5", first.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-09-22T10:01:00.000Z", CultureInfo.InvariantCulture), first.StartedAt);
        Assert.Equal(180, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(30, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(180, first.ReportedInputTokens);
        Assert.Equal(30, first.ReportedOutputTokens);
        Assert.Equal(2, first.Parts.Count);
        Assert.Equal(ContentBucket.ConversationHistory, first.Parts[0].Bucket);
        Assert.Equal("first request", first.Parts[0].PlainText);
        Assert.Equal(ContentBucket.InstructionContext, first.Parts[1].Bucket);
        Assert.Equal("Work in agent mode and keep edits scoped.", first.Parts[1].PlainText);
        Assert.DoesNotContain(first.Parts, static p => string.Equals(p.PlainText, "first response", StringComparison.Ordinal));

        CollectedRecord second = batch.Records[1];
        Assert.Equal(RecordOp.LlmInvoke, second.Op);
        Assert.Equal("gpt-5", second.Model);
        Assert.Equal(240, Assert.Single(second.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(40, Assert.Single(second.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(4, second.Parts.Count);
        Assert.Equal(ContentBucket.ConversationHistory, second.Parts[0].Bucket);
        Assert.Equal("first request", second.Parts[0].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, second.Parts[1].Bucket);
        Assert.Equal("first response", second.Parts[1].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, second.Parts[2].Bucket);
        Assert.Equal("second request", second.Parts[2].PlainText);
        Assert.Equal(ContentBucket.InstructionContext, second.Parts[3].Bucket);
        Assert.Equal("Work in agent mode and keep edits scoped.", second.Parts[3].PlainText);
        Assert.DoesNotContain(second.Parts, static p => string.Equals(p.PlainText, "second response", StringComparison.Ordinal));

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(2, store.CountRecords());
    }

    [Fact]
    public void Map_AppliesAppendThenEditUpdatesAndPrefersMetadataOutputTokensOverCompletionTokens()
    {
        string jsonl = string.Join('\n',
        [
            """{"kind":0,"v":{"version":3,"creationDate":1780157113020,"sessionId":"chat-edit","requests":[]}}""",
            """{"kind":2,"k":["requests"],"v":[{"requestId":"request_8c8ce017-6e3f-460a-9931-5a16825d231a","modelId":"copilot/claude-sonnet-4.6","completionTokens":490,"result":{"metadata":{"promptTokens":32543,"outputTokens":60,"resolvedModel":"claude-sonnet-4-6","toolCallRounds":[{"thinking":{"tokens":0},"modelId":"claude-sonnet-4.6"}],"agentId":"github.copilot.editsAgent"}}}]}""",
            """{"kind":1,"k":["requests",0,"result","metadata","outputTokens"],"v":88}""",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("/fake-home/ws1/chatSessions/append-edit.jsonl", jsonl);
        CopilotVscodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("chat-edit", session.NativeSessionId);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal("claude-sonnet-4-6", record.Model);
        Assert.Equal("github.copilot.editsAgent", record.AgentInstanceId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1780157113020), record.StartedAt);
        Assert.Equal(32543, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(88, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(32543, record.ReportedInputTokens);
        Assert.Equal(88, record.ReportedOutputTokens);
    }

    [Fact]
    public void Map_FallsBackToCompletionTokensAndStrippedModelId_AndLeavesNoTokensWhenAbsent()
    {
        string jsonl = string.Join('\n',
        [
            """{"kind":0,"v":{"sessionId":"chat-fallback","creationDate":"2026-11-19T10:00:00.000Z","requests":[]}}""",
            """{"kind":2,"k":["requests"],"v":[{"requestId":"req-completion","modelId":"copilot/gpt-4.1","completionTokens":490,"message":"Explain this error"},{"requestId":"req-no-tokens","message":{"parts":[{"text":"Follow-up"}]}}]}""",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("/fake-home/ws1/chatSessions/fallback.jsonl", jsonl);
        CopilotVscodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal(2, batch.Records.Count);

        CollectedRecord first = batch.Records[0];
        Assert.Equal("gpt-4.1", first.Model);
        CollectedTokenCount outputToken = Assert.Single(first.Tokens);
        Assert.Equal(TokenClass.Output, outputToken.TokenClass);
        Assert.Equal(490, outputToken.Count);
        Assert.Null(first.ReportedInputTokens);
        Assert.Equal(490, first.ReportedOutputTokens);

        CollectedRecord second = batch.Records[1];
        Assert.Equal("unknown", second.Model);
        Assert.Empty(second.Tokens);
        Assert.Null(second.ReportedInputTokens);
        Assert.Null(second.ReportedOutputTokens);
        Assert.Contains(second.Parts, static p => p.Bucket == ContentBucket.ConversationHistory && p.PlainText == "Follow-up");
    }

    [Fact]
    public void Map_ReturnsEmptyBatchForEmptyReconstructedRequestsArray()
    {
        string jsonl = """{"kind":0,"v":{"version":3,"creationDate":1780157113020,"sessionId":"chat-empty","requests":[]}}""" + "\n";

        NativeSourceUnit unit = MakeNativeUnit("/fake-home/ws1/chatSessions/empty.jsonl", jsonl);
        CollectedBatch batch = new CopilotVscodeMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_IgnoresForbiddenJournalPaths_SupportsDefaultAppendPath_AndExtractsToolRoundsAndCorrection()
    {
        string jsonl = string.Join('\n',
        [
            """{"kind":0,"v":{"version":3,"creationDate":1780157113020,"sessionId":"chat-proto","requests":[]}}""",
            """{"kind":1,"k":["__proto__","polluted"],"v":true}""",
            """{"kind":1,"k":["constructor","prototype","polluted"],"v":true}""",
            """{"kind":2,"v":[{"requestId":"req-1","timestamp":1780157114000,"modelId":"copilot/gpt-5","message":{"parts":[{"text":"No, do not edit that file."}]},"result":{"metadata":{"promptTokens":120,"outputTokens":25,"toolCallRounds":[{"toolCalls":[{"id":"tc-1","name":"read_file","arguments":{"path":"src/app.cs"}}]}]}}}]}""",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("/fake-home/ws1/chatSessions/proto.jsonl", jsonl);
        CollectedBatch batch = new CopilotVscodeMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Single(batch.Sessions);
        Assert.Equal(2, batch.Records.Count);

        CollectedRecord llmRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Equal("prefix_negation", llmRecord.Signals.CorrectionRule);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1780157114000), llmRecord.StartedAt);

        CollectedRecord toolRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.Equal(llmRecord.RecordId, toolRecord.ParentRecordId);
        Assert.Equal("read_file", toolRecord.Name);
        Assert.NotNull(toolRecord.Tool);
        Assert.Equal("tc-1", toolRecord.Tool.CallId);
        Assert.Equal("read_file", toolRecord.Tool.ToolName);
        Assert.NotNull(toolRecord.Tool.Input);
        Assert.Null(toolRecord.Tool.Input.PlainText);
        Assert.Equal(Sha256Hex("{\"path\":\"src/app.cs\"}"), toolRecord.Tool.Input.Sha256Fingerprint);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(2, store.CountRecords());
    }

    [Fact]
    public void Map_WithholdsJsonDocumentPartsAndRecordsWarningForMalformedLine()
    {
        string jsonl = string.Join('\n',
        [
            """{"kind":0,"v":{"sessionId":"chat-json","creationDate":"2026-11-19T10:00:00.000Z","requests":[]}}""",
            "{not valid json",
            """{"kind":2,"k":["requests"],"v":[{"requestId":"req-json","message":{"text":"{\"secret\":true}"},"modeInstructions":"{\"rules\":[\"strict\"]}","result":{"metadata":{"promptTokens":50,"outputTokens":10}}}]}""",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("/fake-home/ws1/chatSessions/malformed.jsonl", jsonl);
        CollectedBatch batch = new CopilotVscodeMapper().Map(unit, BaselineUtc);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal("warning", problem.Severity);
        Assert.Equal(CopilotVscodeMapper.MalformedLineCode, problem.Code);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(2, record.Parts.Count);
        Assert.All(record.Parts, static part =>
        {
            Assert.Null(part.PlainText);
            Assert.NotNull(part.Withheld);
        });

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(1, store.CountRecords());
    }

    [Fact]
    public void Map_SupportsDirectJsonSnapshotFormat()
    {
        string json = """
        {
          "sessionId": "snapshot-session",
          "creationDate": "2026-11-19T11:00:00.000Z",
          "requests": [
            {
              "requestId": "snap-1",
              "modelId": "copilot/gpt-5",
              "message": { "text": "Hello snapshot" },
              "result": { "metadata": { "promptTokens": 42, "outputTokens": 8 } }
            }
          ]
        }
        """;

        NativeSourceUnit unit = MakeNativeUnit("/fake-home/ws1/chatSessions/snapshot.json", json);
        CollectedBatch batch = new CopilotVscodeMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("snapshot-session", session.NativeSessionId);
        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("gpt-5", record.Model);
        Assert.Equal(42, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(8, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
    }

    [Fact]
    public void FieldRegister_DocumentsAllCopilotVscodeNativePropertiesAgainstCatalog()
    {
        string fieldRegisterPath = ResolveFieldRegisterPath();
        string markdown = File.ReadAllText(fieldRegisterPath).Replace("\r\n", "\n", StringComparison.Ordinal);

        string[] lines = markdown.Split('\n');
        bool inSection = false;
        Dictionary<(string TypeName, string PropertyName), (string Disposition, string TargetMapping, string Reason)> rows = [];

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                {
                    break;
                }

                inSection = string.Equals(line[3..].Trim(), "copilot-vscode", StringComparison.Ordinal);
                continue;
            }

            if (!inSection || !line.StartsWith('|'))
            {
                continue;
            }

            string[] cells = line.Trim('|').Split('|').Select(static c => c.Trim()).ToArray();
            if (cells.Length != 5
                || string.Equals(cells[0], "Native type", StringComparison.OrdinalIgnoreCase)
                || cells.All(static c => c.All(static ch => ch is '-' or ':')))
            {
                continue;
            }

            string nativeType = cells[0].Trim('`');
            string property = cells[1].Trim('`');
            string disposition = cells[2].Trim('`');
            rows[(nativeType, property)] = (disposition, cells[3], cells[4]);
        }

        Assert.NotEmpty(rows);

        Type[] nativeTypes =
        [
            typeof(CopilotVscodeJournalDocument),
            typeof(CopilotVscodeRequest),
            typeof(CopilotVscodeToolCall),
            typeof(CopilotVscodeJournalProblem),
        ];

        HashSet<(string TypeName, string PropertyName)> reflected = [];
        foreach (Type nativeType in nativeTypes)
        {
            foreach (PropertyInfo property in nativeType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(static p => p.GetMethod is not null && !string.Equals(p.Name, "EqualityContract", StringComparison.Ordinal)))
            {
                (string, string) key = (nativeType.Name, property.Name);
                Assert.True(reflected.Add(key));
                Assert.True(rows.ContainsKey(key), $"Missing field-register row for '{nativeType.Name}.{property.Name}' under '## copilot-vscode'.");
            }
        }

        foreach ((string TypeName, string PropertyName) key in rows.Keys)
        {
            Assert.True(reflected.Contains(key), $"Stale field-register row '{key.TypeName}.{key.PropertyName}' under '## copilot-vscode'.");

            (string disposition, string targetMapping, string reason) = rows[key];
            if (string.Equals(disposition, "mapped", StringComparison.Ordinal))
            {
                MatchCollection matches = Regex.Matches(targetMapping, @"`(?<table>[a-z][a-z0-9_]*)\.(?<column>[a-z][a-z0-9_]*)`");
                Assert.True(
                    matches.Count > 0 || targetMapping.Contains("nested:", StringComparison.Ordinal),
                    $"Mapped row '{key.TypeName}.{key.PropertyName}' must cite a table.column or nested type.");

                foreach (Match match in matches)
                {
                    string tableName = match.Groups["table"].Value;
                    string columnName = match.Groups["column"].Value;
                    Assert.True(
                        RelationalSchemaCatalog.TryGetTable(tableName, out TableDefinition? table) && table is not null,
                        $"Unknown catalog table '{tableName}' on '{key.TypeName}.{key.PropertyName}'.");
                    Assert.Contains(table.Columns, col => string.Equals(col.Name, columnName, StringComparison.Ordinal));
                }
            }
            else
            {
                Assert.Equal("dropped", disposition);
                Assert.False(string.IsNullOrWhiteSpace(reason) || reason is "—" or "-");
            }
        }
    }

    /// <summary>
    /// A request whose resolved model has its own published tokenizer counts the message part with
    /// that tokenizer: the measured tokens equal the model's own figure, not the stand-in's.
    /// </summary>
    [Fact]
    public void Map_CountsRequestPartsWithTheRequestModelOwnTokenizer()
    {
        string jsonl = string.Join('\n',
        [
            """{"kind":0,"v":{"sessionId":"chat-own","creationDate":"2026-11-19T10:00:00.000Z","requests":[]}}""",
            """{"kind":2,"k":["requests"],"v":[{"requestId":"req-own","modelId":"gpt-3.5-turbo","message":{"text":"{\"こんにちは\":\"世界\"}"},"result":{"metadata":{"promptTokens":50,"outputTokens":10}}}]}""",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("/fake-home/ws1/chatSessions/model-own.jsonl", jsonl);
        CollectedBatch batch = new CopilotVscodeMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("gpt-3.5-turbo", record.Model);
        CollectedContentPart part = Assert.Single(record.Parts);
        Assert.NotNull(part.Withheld);
        Assert.Equal(LocalTextTokenizer.Shared.Count("{\"こんにちは\":\"世界\"}", "gpt-3.5-turbo").Count, part.Withheld!.MeasuredTokens);
        Assert.NotEqual(LocalTextTokenizer.Shared.Count("{\"こんにちは\":\"世界\"}", null).Count, part.Withheld.MeasuredTokens);
    }

    private static NativeSourceUnit MakeNativeUnit(string path, string rawText)
    {
        SourceUnit sourceUnit = new(
            HarnessId: "copilot-vscode",
            SourceKey: Path.GetFullPath(path),
            ProviderId: "copilot",
            ParserId: "copilot-vscode-reader",
            ParserContractVersion: "1",
            Format: "copilot-vscode-journal",
            SourceRootLabel: "chatSessions",
            RevisionToken: "1-1",
            LastWriteUtc: BaselineUtc,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText));

        return new NativeSourceUnit(
            Unit: sourceUnit,
            RawText: rawText,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: BaselineUtc);
    }

    private static string ResolveFieldRegisterPath()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "docs", "dash", "field-register.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate docs/dash/field-register.md.");
    }

    private static string Sha256Hex(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
