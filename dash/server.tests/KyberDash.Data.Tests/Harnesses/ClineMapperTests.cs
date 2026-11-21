using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Cline;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="ClineMapper"/> covering the Cline VS Code extension
/// <c>tasks/&lt;id&gt;/ui_messages.json</c> surface (<c>cline</c>) and the Cline CLI
/// <c>sessions/&lt;id&gt;/&lt;id&gt;.messages.json</c> surface (<c>cline-cli</c>),
/// plus field register completeness for both sections.
/// </summary>
public class ClineMapperTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HarnessId_IsCline()
    {
        ClineMapper mapper = new();
        Assert.Equal("cline", mapper.HarnessId);
    }

    [Fact]
    public void Map_ClineTaskProducesUsageBearingLlmInvokeRecords()
    {
        string rawText = """
            [
              {"type":"say","say":"user_feedback","text":"build the feature","ts":1700000000000},
              {"type":"say","say":"api_req_started","text":"{\"tokensIn\":200,\"tokensOut\":100,\"cacheReads\":10,\"cacheWrites\":5,\"cost\":0.07}","ts":1700000001000},
              {"type":"say","say":"api_req_started","text":"{\"tokensIn\":50,\"tokensOut\":25}","ts":1700000002000}
            ]
            """;

        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/Library/Application Support/Code/User/globalStorage/saoudrizwan.claude-dev/tasks/task-parse/ui_messages.json",
            rawText,
            "cline",
            "cline-ui-messages-json");
        ClineMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Equal("cline", batch.Source.HarnessId);
        Assert.Equal(SourceKind.File, batch.Source.Kind);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("task-parse", session.SessionKey);
        Assert.Equal("task-parse", session.NativeSessionId);
        Assert.Equal("cline", session.HarnessId);

        Assert.Equal(2, batch.Records.Count);

        CollectedRecord first = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, first.Op);
        Assert.Equal(RecordKind.Internal, first.Kind);
        Assert.Equal("cline", first.HarnessId);
        Assert.Equal("task-parse", first.SessionKey);
        Assert.Equal("cline-auto", first.Model);
        Assert.Equal("cline:cline-auto", first.Name);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1700000001000),
            first.StartedAt);
        Assert.Equal(200, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(100, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(10, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.CacheRead).Count);
        Assert.Equal(5, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.CacheWrite).Count);
        Assert.Equal(200, first.ReportedInputTokens);
        Assert.Equal(100, first.ReportedOutputTokens);
        Assert.NotNull(first.ReportedCost);
        Assert.Equal(0.07, first.ReportedCost.Amount);
        Assert.Equal("USD", first.ReportedCost.Currency);
        Assert.StartsWith("synth:cline:task-parse:", first.RecordId, StringComparison.Ordinal);
        CollectedContentPart userPart = Assert.Single(first.Parts);
        Assert.Equal(ContentBucket.UserMessage, userPart.Bucket);
        Assert.Equal("build the feature", userPart.PlainText);

        CollectedRecord second = batch.Records[1];
        Assert.Equal(RecordOp.LlmInvoke, second.Op);
        Assert.Equal(50, Assert.Single(second.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(25, Assert.Single(second.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Null(second.ReportedCost);
        Assert.Empty(second.Parts);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(2, store.CountRecords("cline"));
    }

    [Fact]
    public void Map_ClineTaskSkipsZeroUsageEntriesAndRepairsBadTimestamps()
    {
        string rawText = """
            [
              {"type":"say","say":"api_req_started","text":"{\"tokensIn\":0,\"tokensOut\":0}","ts":1700000001000},
              {"type":"say","say":"api_req_started","text":"{\"tokensIn\":30,\"tokensOut\":10}","ts":"not-a-timestamp"}
            ]
            """;

        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/tasks/task-zero/ui_messages.json",
            rawText,
            "cline",
            "cline-ui-messages-json");
        ClineMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("task-zero", record.SessionKey);
        Assert.Equal(BaselineUtc, record.StartedAt);
        Assert.Equal(30, Assert.Single(record.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
    }

    [Fact]
    public void Map_ClineCliSessionProducesRecordsUnderClineCliId()
    {
        string rawText = """
            {
              "version":1,
              "sessionId":"sess-1",
              "messages":[
                {"id":"msg_0","role":"user","content":[{"type":"text","text":"add retry logic"}],"ts":1785701064304},
                {"id":"msg_1","role":"assistant","content":[{"type":"text","text":"done"},{"type":"tool_use","id":"call_1","name":"run_commands","input":{"commands":["npm test"]}}],"ts":1785701065304,"metrics":{"inputTokens":120,"outputTokens":40,"cacheReadTokens":1000,"cacheWriteTokens":50,"cost":0.02},"modelInfo":{"id":"z-ai/glm-5.2"}},
                {"id":"msg_2","role":"assistant","content":[{"type":"tool_use","id":"call_2","name":"skills","input":{"name":"code-review"}}],"ts":1785701066304,"metrics":{"inputTokens":60,"outputTokens":10},"modelInfo":{"id":"z-ai/glm-5.2"}}
              ]
            }
            """;

        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/.cline/data/sessions/sess-1/sess-1.messages.json",
            rawText,
            "cline-cli",
            "cline-cli-messages-json");
        ClineMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Equal("cline-cli", batch.Source.HarnessId);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-1", session.SessionKey);
        Assert.Equal("sess-1", session.NativeSessionId);
        Assert.Equal("cline-cli", session.HarnessId);

        List<CollectedRecord> parents = batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke).ToList();
        List<CollectedRecord> tools = batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke).ToList();
        Assert.Equal(2, parents.Count);
        Assert.Equal(2, tools.Count);

        CollectedRecord first = parents[0];
        Assert.Equal("cline-cli", first.HarnessId);
        Assert.Equal("sess-1", first.SessionKey);
        Assert.Equal("z-ai/glm-5.2", first.Model);
        Assert.Equal("cline-cli:z-ai/glm-5.2", first.Name);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1785701065304),
            first.StartedAt);
        Assert.Equal(120, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(40, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(1000, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.CacheRead).Count);
        Assert.Equal(50, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.CacheWrite).Count);
        Assert.NotNull(first.ReportedCost);
        Assert.Equal(0.02, first.ReportedCost.Amount);
        Assert.Equal("USD", first.ReportedCost.Currency);
        Assert.Empty(first.SkillsInvoked);
        CollectedContentPart userPart = Assert.Single(first.Parts);
        Assert.Equal(ContentBucket.UserMessage, userPart.Bucket);
        Assert.Equal("add retry logic", userPart.PlainText);

        CollectedRecord bashTool = Assert.Single(tools, static t => t.Tool != null && t.Tool.CallId == "call_1");
        Assert.Equal("Bash", bashTool.Tool!.ToolName);
        Assert.Equal(first.RecordId, bashTool.ParentRecordId);
        Assert.Equal(RecordStatus.Unset, bashTool.Status);
        Assert.NotNull(bashTool.Tool.Input);

        CollectedRecord second = parents[1];
        Assert.Equal(["code-review"], second.SkillsInvoked);
        Assert.Null(second.ReportedCost);
        Assert.Empty(second.Parts);

        CollectedRecord skillTool = Assert.Single(tools, static t => t.Tool != null && t.Tool.CallId == "call_2");
        Assert.Equal("Skill", skillTool.Tool!.ToolName);
        Assert.Equal(second.RecordId, skillTool.ParentRecordId);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(4, store.CountRecords("cline-cli"));
    }

    [Fact]
    public void Map_ClineCliSessionWithoutMetricsEmitsUnrecognizedProblem()
    {
        string rawText = """
            {
              "version":1,
              "sessionId":"sess-empty",
              "messages":[
                {"id":"msg_0","role":"user","content":[{"type":"text","text":"hello"}],"ts":1785701064304},
                {"id":"msg_1","role":"assistant","content":[{"type":"text","text":"hi"}],"ts":1785701065304}
              ]
            }
            """;

        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/.cline/data/sessions/sess-empty/sess-empty.messages.json",
            rawText,
            "cline-cli",
            "cline-cli-messages-json");
        ClineMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(ClineMapper.UnrecognizedTranscriptCode, problem.Code);
    }

    [Fact]
    public void Map_MalformedPayloadEmitsMalformedProblemsWithoutRecords()
    {
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/tasks/task-broken/ui_messages.json",
            "{{not valid json",
            "cline",
            "cline-ui-messages-json");
        ClineMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        Assert.NotEmpty(batch.Problems);
        Assert.Contains(batch.Problems, static p => p.Code == ClineMapper.MalformedLineCode);
        Assert.Contains(batch.Problems, static p => p.Code == ClineMapper.UnrecognizedTranscriptCode);
    }

    [Fact]
    public void Map_EmptyPayloadReturnsEmptyBatchWithoutProblems()
    {
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake/tasks/task-empty/ui_messages.json",
            "   ",
            "cline",
            "cline-ui-messages-json");
        ClineMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void FieldRegisterDocument_CoversClineNativeTypes()
    {
        string path = ResolveFieldRegisterPath();
        string markdown = File.ReadAllText(path);
        Dictionary<string, Dictionary<(string TypeName, string PropertyName), (string Disposition, string TargetMapping, string Reason)>> sections = ParseSections(markdown);

        Dictionary<string, Type[]> expected = new(StringComparer.Ordinal)
        {
            ["cline"] = [typeof(ClineTaskDocument), typeof(ClineUsageEntry), typeof(ClineParseProblem)],
            ["cline-cli"] = [typeof(ClineCliDocument), typeof(ClineCliMessage), typeof(ClineCliToolCall), typeof(ClineParseProblem)],
        };

        foreach ((string harness, Type[] nativeTypes) in expected)
        {
            Assert.True(sections.ContainsKey(harness), $"Field register is missing '## {harness}'.");
            Dictionary<(string TypeName, string PropertyName), (string Disposition, string TargetMapping, string Reason)> rows = sections[harness];

            HashSet<(string TypeName, string PropertyName)> reflected = [];
            foreach (Type nativeType in nativeTypes)
            {
                foreach (PropertyInfo property in nativeType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(static p => p.GetMethod is not null && !string.Equals(p.Name, "EqualityContract", StringComparison.Ordinal)))
                {
                    (string, string) key = (nativeType.Name, property.Name);
                    Assert.True(reflected.Add(key));
                    Assert.True(rows.ContainsKey(key), $"Missing field-register row for '{nativeType.Name}.{property.Name}' under '## {harness}'.");
                }
            }

            foreach ((string TypeName, string PropertyName) key in rows.Keys)
            {
                Assert.True(
                    reflected.Contains(key),
                    $"Stale field-register row '{key.TypeName}.{key.PropertyName}' under '## {harness}'.");

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
    }

    private static Dictionary<string, Dictionary<(string TypeName, string PropertyName), (string Disposition, string TargetMapping, string Reason)>> ParseSections(string markdown)
    {
        Dictionary<string, Dictionary<(string, string), (string, string, string)>> sections = new(StringComparer.Ordinal);
        Dictionary<(string, string), (string, string, string)>? current = null;

        foreach (string rawLine in markdown.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                string name = line[3..].Trim();
                current = [];
                sections[name] = current;
                continue;
            }

            if (current is null || !line.StartsWith('|'))
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

            current[(cells[0].Trim('`'), cells[1].Trim('`'))] = (cells[2].Trim('`'), cells[3], cells[4]);
        }

        return sections;
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
            SourceRootLabel: harnessId,
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
