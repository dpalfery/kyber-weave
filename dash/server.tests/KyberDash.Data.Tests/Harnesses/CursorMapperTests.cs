using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Cursor;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CursorMapper"/> covering <c>cursorDiskKV</c> bubble-row mapping,
/// <c>cursor-agent</c> JSONL and legacy text transcript mapping, <c>cursor-agent -&gt; cursor</c>
/// harness normalization, native token counts only (text length never becomes tokens), and
/// field register completeness.
/// </summary>
public class CursorMapperTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HarnessId_IsCursor()
    {
        CursorMapper mapper = new();
        Assert.Equal("cursor", mapper.HarnessId);
    }

    [Fact]
    public void Map_BubbleRowsWithReportedTokensYieldUsageBearingLlmInvokeRecords()
    {
        string rawText = """
            [
              {"bubble_key":"bubbleId:composer-1:11111111-1111-1111-1111-111111111111","input_tokens":60,"output_tokens":0,"model":null,"created_at":"2026-09-22T10:01:00.000Z","request_id":"req-1","user_text":"summarize this file","text_length":18,"bubble_type":1,"context_window":200000,"code_blocks":null},
              {"bubble_key":"bubbleId:composer-1:22222222-2222-2222-2222-222222222222","input_tokens":120,"output_tokens":45,"model":"claude-sonnet-4-5","created_at":"2026-09-22T10:02:00.000Z","request_id":"req-1","user_text":"Here is the summary.","text_length":20,"bubble_type":2,"context_window":200000,"code_blocks":null}
            ]
            """;

        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/Library/Application Support/Cursor/User/globalStorage/state.vscdb",
            rawText,
            "cursor",
            "cursor-vscdb");
        CursorMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Equal("cursor", batch.Source.HarnessId);
        Assert.Equal(SourceKind.File, batch.Source.Kind);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("composer-1", session.SessionKey);
        Assert.Equal("composer-1", session.NativeSessionId);
        Assert.Equal("cursor", session.HarnessId);

        Assert.Equal(2, batch.Records.Count);

        CollectedRecord userRecord = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, userRecord.Op);
        Assert.Equal(RecordKind.Internal, userRecord.Kind);
        Assert.Equal("cursor", userRecord.HarnessId);
        Assert.Equal("composer-1", userRecord.SessionKey);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-22T10:01:00.000Z", CultureInfo.InvariantCulture),
            userRecord.StartedAt);
        Assert.Equal(60, Assert.Single(userRecord.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(60, userRecord.ReportedInputTokens);
        Assert.Null(userRecord.ReportedOutputTokens);
        Assert.StartsWith("synth:cursor:composer-1:", userRecord.RecordId, StringComparison.Ordinal);

        CollectedRecord assistantRecord = batch.Records[1];
        Assert.Equal(RecordOp.LlmInvoke, assistantRecord.Op);
        Assert.Equal("claude-sonnet-4-5", assistantRecord.Model);
        Assert.Equal("cursor:claude-sonnet-4-5", assistantRecord.Name);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-22T10:02:00.000Z", CultureInfo.InvariantCulture),
            assistantRecord.StartedAt);
        Assert.Equal(120, Assert.Single(assistantRecord.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(45, Assert.Single(assistantRecord.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(120, assistantRecord.ReportedInputTokens);
        Assert.Equal(45, assistantRecord.ReportedOutputTokens);
        Assert.Equal(2, assistantRecord.Parts.Count);
        Assert.Equal(ContentBucket.UserMessage, assistantRecord.Parts[0].Bucket);
        Assert.Equal("summarize this file", assistantRecord.Parts[0].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, assistantRecord.Parts[1].Bucket);
        Assert.Equal("Here is the summary.", assistantRecord.Parts[1].PlainText);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(2, store.CountRecords("cursor"));
    }

    [Fact]
    public void Map_BubbleRowWithJsonRequestIdFallsBackToBubbleKeyTail()
    {
        string rawText = """
            [
              {"bubble_key":"bubbleId:composer-9:33333333-3333-3333-3333-333333333333","input_tokens":60,"output_tokens":10,"model":null,"created_at":"2026-09-22T10:01:00.000Z","request_id":"{\"a\": 1}","user_text":"hello","text_length":5,"bubble_type":1,"context_window":200000,"code_blocks":null}
            ]
            """;
        NativeSourceUnit unit = MakeNativeUnit("/fake/state.vscdb", rawText, "cursor", "cursor-vscdb");

        CollectedBatch batch = new CursorMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(
            "synth:cursor:composer-9:33333333-3333-3333-3333-333333333333",
            record.RecordId);
    }

    [Fact]
    public void Map_BubbleRowsWithZeroNativeTokensYieldNotMeasuredRecordsAndSkipEmptyOnes()
    {
        string rawText = """
            [
              {"bubble_key":"bubbleId:composer-2:11111111-1111-1111-1111-111111111111","input_tokens":0,"output_tokens":0,"model":null,"created_at":"2026-09-22T10:01:00.000Z","request_id":null,"user_text":"","text_length":0,"bubble_type":2,"context_window":null,"code_blocks":null},
              {"bubble_key":"bubbleId:composer-2:22222222-2222-2222-2222-222222222222","input_tokens":0,"output_tokens":0,"model":null,"created_at":"2026-09-22T10:02:00.000Z","request_id":null,"user_text":"user_text excerpt","text_length":400,"bubble_type":2,"context_window":null,"code_blocks":null}
            ]
            """;

        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/state.vscdb",
            rawText,
            "cursor",
            "cursor-vscdb");
        CursorMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal("cursor-auto", record.Model);
        Assert.Equal("user_text excerpt", Assert.Single(record.Parts).PlainText);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);
    }

    [Fact]
    public void Map_BubbleRowsSkipMultilineComposerKeysAndTimestampLessRows()
    {
        string rawText = """
            [
              {"bubble_key":"bubbleId:task-call_abc\nfc_xyz:99999999-9999-9999-9999-999999999999","input_tokens":10,"output_tokens":5,"model":null,"created_at":"2026-09-22T10:01:00.000Z","request_id":null,"user_text":"tool output","text_length":11,"bubble_type":2,"context_window":null,"code_blocks":null},
              {"bubble_key":"bubbleId:composer-3:11111111-1111-1111-1111-111111111111","input_tokens":10,"output_tokens":5,"model":null,"created_at":null,"request_id":null,"user_text":"no timestamp","text_length":12,"bubble_type":2,"context_window":null,"code_blocks":null},
              {"bubble_key":"bubbleId:composer-3:22222222-2222-2222-2222-222222222222","input_tokens":10,"output_tokens":5,"model":null,"created_at":"2026-09-22T10:03:00.000Z","request_id":null,"user_text":"kept","text_length":4,"bubble_type":2,"context_window":null,"code_blocks":null}
            ]
            """;

        NativeSourceUnit unit = MakeNativeUnit("/fake/state.vscdb", rawText, "cursor", "cursor-vscdb");
        CursorMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("composer-3", record.SessionKey);
        Assert.Equal("kept", record.Parts[0].PlainText);
    }

    [Fact]
    public void Map_CursorAgentJsonlTranscriptYieldsTurnsAndFoldsAliasToCursor()
    {
        string rawText = string.Join('\n',
        [
            """{"role":"user","message":{"content":[{"type":"text","text":"<user_query>explain closures</user_query>"}]}}""",
            """{"role":"assistant","message":{"content":[{"type":"text","text":"A closure captures variables."},{"type":"tool_use","name":"Read"}]}}""",
            """{"role":"user","message":{"content":[{"type":"text","text":"thanks"}]}}""",
            """{"role":"assistant","message":{"content":[{"type":"text","text":"Anytime."}]}}""",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/.cursor/projects/p1/agent-transcripts/550e8400-e29b-41d4-a716-446655440000.jsonl",
            rawText,
            "cursor-agent",
            "cursor-agent-jsonl");
        CursorMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Equal("cursor", batch.Source.HarnessId);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", session.SessionKey);
        Assert.Equal("cursor", session.HarnessId);

        Assert.Equal(2, batch.Records.Count);
        foreach (CollectedRecord record in batch.Records)
        {
            Assert.Equal(RecordOp.LlmInvoke, record.Op);
            Assert.Equal(RecordKind.Internal, record.Kind);
            Assert.Equal("cursor", record.HarnessId);
            Assert.Equal("cursor-agent-auto", record.Model);
            Assert.Equal(BaselineUtc, record.StartedAt);
            Assert.Empty(record.Tokens);
        }

        CollectedRecord first = batch.Records[0];
        Assert.Null(first.ReportedInputTokens);
        Assert.Null(first.ReportedOutputTokens);
        Assert.Equal("explain closures", first.Parts[0].PlainText);
        Assert.Equal(ContentBucket.UserMessage, first.Parts[0].Bucket);
        Assert.Equal("A closure captures variables.", first.Parts[1].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, first.Parts[1].Bucket);
        Assert.DoesNotContain(batch.Records, static r => r.Op == RecordOp.ToolInvoke);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(2, store.CountRecords("cursor"));
        Assert.Equal(0, store.CountRecords("cursor-agent"));
    }

    [Fact]
    public void Map_CursorAgentLegacyTxtTranscriptParsesMarkersAndKeepsReasoning()
    {
        string rawText = string.Join('\n',
        [
            "user: <user_query>refactor this</user_query>",
            "A: here is the refactor",
            "[Thinking] consider edge cases",
            "[Tool call] Read",
            "[Tool result] ok",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/.cursor/projects/p1/agent-transcripts/notes.txt",
            rawText,
            "cursor",
            "cursor-agent-jsonl");
        CursorMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);

        // A transcript carries no token counts, so the turn is kept with none rather than with a
        // figure guessed from its text length.
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);

        // The `[Tool result]` marker regex requires a word boundary after `]`, which never
        // occurs before the trailing text, so the line stays in the body exactly as in
        // `cursor-agent.ts`.
        Assert.EndsWith("[Tool result] ok", record.Parts[1].PlainText, StringComparison.Ordinal);
        Assert.Equal(3, record.Parts.Count);
        Assert.Equal(ContentBucket.Reasoning, record.Parts[2].Bucket);
        Assert.Equal("consider edge cases", record.Parts[2].PlainText);
        Assert.DoesNotContain(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
    }

    [Fact]
    public void Map_BubbleRowWithNativeCountsAndLongTextKeepsOnlyTheNativeCounts()
    {
        string rawText = """
            [
              {"bubble_key":"bubbleId:composer-5:11111111-1111-1111-1111-111111111111","input_tokens":0,"output_tokens":7,"model":null,"created_at":"2026-09-22T10:01:00.000Z","request_id":null,"user_text":"reply","text_length":4000,"bubble_type":2,"context_window":null,"code_blocks":null}
            ]
            """;
        NativeSourceUnit unit = MakeNativeUnit("/fake/state.vscdb", rawText, "cursor", "cursor-vscdb");

        CollectedBatch batch = new CursorMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        CollectedTokenCount output = Assert.Single(record.Tokens);
        Assert.Equal(TokenClass.Output, output.TokenClass);
        Assert.Equal(7, output.Count);
        Assert.Null(record.ReportedInputTokens);
        Assert.Equal(7, record.ReportedOutputTokens);
    }

    [Fact]
    public void Map_TranscriptTurnWithOnlyAToolCallIsKeptAsTurnWithNoCounts()
    {
        string rawText = string.Join('\n',
        [
            """{"role":"user","message":{"content":[{"type":"text","text":"go"}]}}""",
            """{"role":"assistant","message":{"content":[{"type":"tool_use","name":"Read"}]}}""",
        ]) + "\n";
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/.cursor/projects/p1/agent-transcripts/tool-only.jsonl",
            rawText,
            "cursor",
            "cursor-agent-jsonl");

        CollectedBatch batch = new CursorMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);
        Assert.Equal("go", Assert.Single(record.Parts).PlainText);
        Assert.Single(batch.Sessions);
    }

    [Fact]
    public void Map_EmptyRawTextReturnsEmptyBatch()
    {
        NativeSourceUnit unit = MakeNativeUnit("/fake/state.vscdb", "   ", "cursor", "cursor-vscdb");
        CursorMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_MalformedBubbleJsonEmitsProblem()
    {
        NativeSourceUnit unit = MakeNativeUnit("/fake/state.vscdb", "[{not json}]", "cursor", "cursor-vscdb");
        CursorMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(CursorMapper.MalformedLineCode, problem.Code);
    }

    [Fact]
    public void Map_UnrecognizedTranscriptEmitsProblem()
    {
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/notes.txt",
            "just some prose without any markers\nmore prose\n",
            "cursor-agent",
            "cursor-agent-jsonl");
        CursorMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Equal("cursor", batch.Source.HarnessId);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(CursorMapper.UnrecognizedTranscriptCode, problem.Code);
    }

    [Fact]
    public void Map_MalformedJsonlLineEmitsProblemAndKeepsOtherTurns()
    {
        string rawText = string.Join('\n',
        [
            """{"role":"user","message":{"content":[{"type":"text","text":"hello"}]}}""",
            "{broken jsonl line",
            """{"role":"assistant","message":{"content":[{"type":"text","text":"Hi there."}]}}""",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/t.jsonl",
            rawText,
            "cursor",
            "cursor-agent-jsonl");
        CursorMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Single(batch.Records);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(CursorMapper.MalformedLineCode, problem.Code);
        Assert.Equal("cursor", problem.HarnessId);
    }

    [Fact]
    public void FieldRegister_CursorSectionDocumentsEveryNativeProperty()
    {
        string[] lines = File.ReadAllLines(ResolveFieldRegisterPath());

        Dictionary<(string TypeName, string PropertyName), (string Disposition, string TargetMapping, string Reason)> rows = [];
        bool inSection = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                {
                    break;
                }

                inSection = string.Equals(line[3..].Trim(), "cursor", StringComparison.Ordinal);
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
            typeof(CursorBubbleRow),
            typeof(CursorBubbleDocument),
            typeof(CursorParseProblem),
            typeof(CursorTranscriptTurn),
            typeof(CursorTranscriptDocument),
        ];

        HashSet<(string TypeName, string PropertyName)> reflected = [];
        foreach (Type nativeType in nativeTypes)
        {
            foreach (PropertyInfo property in nativeType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(static p => p.GetMethod is not null && !string.Equals(p.Name, "EqualityContract", StringComparison.Ordinal)))
            {
                (string, string) key = (nativeType.Name, property.Name);
                Assert.True(reflected.Add(key));
                Assert.True(rows.ContainsKey(key), $"Missing field-register row for '{nativeType.Name}.{property.Name}' under '## cursor'.");
            }
        }

        foreach ((string TypeName, string PropertyName) key in rows.Keys)
        {
            Assert.True(
                reflected.Contains(key),
                $"Stale field-register row '{key.TypeName}.{key.PropertyName}' under '## cursor'.");

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

    private static NativeSourceUnit MakeNativeUnit(string path, string rawText, string harnessId, string format)
    {
        SourceUnit sourceUnit = new(
            HarnessId: harnessId,
            SourceKey: path,
            ProviderId: harnessId,
            ParserId: format + "-reader",
            ParserContractVersion: "1",
            Format: format,
            SourceRootLabel: "cursor",
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
}
