using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Kilo;
using KyberDash.Data.Tokenization;
using KyberDash.Domain;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="KiloMapper"/> covering the <c>kilo-shared-runtime</c> kilo.db row-export
/// surface and the <c>kilo-vscode-legacy</c> <c>ui_messages.json</c> task-tree surface, plus field
/// register completeness.
/// </summary>
public class KiloMapperTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A JSON document whose token counts differ between cl100k_base and the o200k_base stand-in.</summary>
    private const string DivergentJsonText = "{\"summary\":\"こんにちは世界、今日はいい天気ですね。\"}";

    [Fact]
    public void HarnessId_IsKiloSharedRuntime()
    {
        KiloMapper mapper = new();
        Assert.Equal("kilo-shared-runtime", mapper.HarnessId);
    }

    [Fact]
    public void Map_SharedRuntimeContractFixtureProducesTurnsInTimeOrderWithToolRecords()
    {
        string export = BuildSharedRuntimeExport();
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/.local/share/kilo/kilo.db",
            export,
            KiloMapper.KiloSharedRuntimeHarnessId,
            format: "kilo-sqlite",
            parserId: "kilo-sqlite-reader");

        KiloMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Equal("kilo-shared-runtime", batch.Source.HarnessId);
        Assert.Equal(SourceKind.File, batch.Source.Kind);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-kilo-reader", session.SessionKey);
        Assert.Equal("sess-kilo-reader", session.NativeSessionId);
        Assert.Equal("kilo-shared-runtime", session.HarnessId);
        Assert.Equal("Kilo reader contract", session.Title);

        Assert.Equal(5, batch.Records.Count);

        CollectedRecord first = batch.Records[0];
        Assert.Equal("synth:kilo-shared-runtime:sess-kilo-reader:msg-a1", first.RecordId);
        Assert.Equal(RecordOp.LlmInvoke, first.Op);
        Assert.Equal("kilo-shared-runtime", first.HarnessId);
        Assert.Equal("claude-opus-4-6", first.Model);
        Assert.Equal(T0.AddMinutes(1), first.StartedAt);
        Assert.Equal("sess-kilo-reader", first.SessionKey);
        Assert.Equal("synth:kilo-shared-runtime:sess-kilo-reader", first.TraceId);
        Assert.Equal(100, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(20, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(100, first.ReportedInputTokens);
        Assert.Equal(20, first.ReportedOutputTokens);
        Assert.Equal(3, first.Parts.Count);
        Assert.Equal(ContentBucket.ConversationHistory, first.Parts[0].Bucket);
        Assert.Equal("first question", first.Parts[0].PlainText);
        Assert.Equal("user", first.Parts[0].Role);
        Assert.Equal(ContentBucket.ConversationHistory, first.Parts[1].Bucket);
        Assert.Equal("first answer", first.Parts[1].PlainText);
        Assert.Equal(ContentBucket.ToolResultContent, first.Parts[2].Bucket);
        Assert.Equal("file.txt\n", first.Parts[2].PlainText);

        CollectedRecord firstTool = batch.Records[1];
        Assert.Equal(RecordOp.ToolInvoke, firstTool.Op);
        Assert.Equal(first.RecordId, firstTool.ParentRecordId);
        Assert.Equal("synth:kilo-shared-runtime:sess-kilo-reader:msg-a1:call-1", firstTool.RecordId);
        Assert.NotNull(firstTool.Tool);
        Assert.Equal("call-1", firstTool.Tool.CallId);
        Assert.Equal("bash", firstTool.Tool.ToolName);
        Assert.NotNull(firstTool.Tool.Input);
        Assert.Equal(64, firstTool.Tool.Input.Sha256Fingerprint?.Length);
        Assert.True(firstTool.Tool.Succeeded);
        Assert.Null(firstTool.Tool.Error);
        Assert.Equal(T0.AddMinutes(1), firstTool.StartedAt);

        CollectedRecord second = batch.Records[2];
        Assert.Equal("synth:kilo-shared-runtime:sess-kilo-reader:msg-a2", second.RecordId);
        Assert.Equal(RecordOp.LlmInvoke, second.Op);
        Assert.Equal(300, Assert.Single(second.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(60, Assert.Single(second.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(4, second.Parts.Count);
        Assert.Equal("second question", second.Parts[0].PlainText);
        Assert.Equal("second answer", second.Parts[1].PlainText);
        Assert.Equal("second answer, continued", second.Parts[2].PlainText);
        Assert.Equal(ContentBucket.ToolResultContent, second.Parts[3].Bucket);
        Assert.Equal("ENOENT: no such file", second.Parts[3].PlainText);
        Assert.DoesNotContain(
            second.Parts,
            static p => p.PlainText is not null && p.PlainText.Contains("weighing the options", StringComparison.Ordinal));

        CollectedRecord secondTool = batch.Records[3];
        Assert.Equal(RecordOp.ToolInvoke, secondTool.Op);
        Assert.Equal(second.RecordId, secondTool.ParentRecordId);
        Assert.NotNull(secondTool.Tool);
        Assert.Equal("call-2", secondTool.Tool.CallId);
        Assert.Equal("read", secondTool.Tool.ToolName);
        Assert.False(secondTool.Tool.Succeeded);
        Assert.NotNull(secondTool.Tool.Error);
        Assert.Equal(RecordStatus.Error, secondTool.Status);

        CollectedRecord third = batch.Records[4];
        Assert.Equal("synth:kilo-shared-runtime:sess-kilo-reader:msg-a3", third.RecordId);
        Assert.Equal(7, Assert.Single(third.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(3, Assert.Single(third.Tokens, static t => t.TokenClass == TokenClass.Output).Count);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(5, store.CountRecords());
    }

    [Fact]
    public void Map_SharedRuntimeSkipsRouterMessageAndNonBucketParts()
    {
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/.local/share/kilo/kilo.db",
            BuildSharedRuntimeExport(),
            KiloMapper.KiloSharedRuntimeHarnessId,
            format: "kilo-sqlite",
            parserId: "kilo-sqlite-reader");

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        Assert.DoesNotContain(batch.Records, static r => r.RecordId.Contains("msg-router", StringComparison.Ordinal));
        foreach (CollectedRecord record in batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke))
        {
            Assert.DoesNotContain(
                record.Parts,
                static p => p.PlainText is not null && p.PlainText.Contains("router filler answer", StringComparison.Ordinal));
            Assert.All(
                record.Parts,
                p => Assert.True(
                    p.Bucket is ContentBucket.ConversationHistory or ContentBucket.ToolResultContent,
                    $"Unexpected bucket {p.Bucket} on {record.RecordId}."));
        }
    }

    [Fact]
    public void Map_SharedRuntimeCarriesLastToolResultIntoTextOnlyFollowUp()
    {
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/.local/share/kilo/kilo.db",
            BuildSharedRuntimeExport(),
            KiloMapper.KiloSharedRuntimeHarnessId,
            format: "kilo-sqlite",
            parserId: "kilo-sqlite-reader");

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        CollectedRecord third = batch.Records.Single(static r =>
            string.Equals(r.RecordId, "synth:kilo-shared-runtime:sess-kilo-reader:msg-a3", StringComparison.Ordinal));
        Assert.Equal(2, third.Parts.Count);
        Assert.Equal("third answer", third.Parts[0].PlainText);
        Assert.Equal(ContentBucket.ToolResultContent, third.Parts[1].Bucket);
        Assert.Equal("ENOENT: no such file", third.Parts[1].PlainText);
    }

    [Fact]
    public void Map_SharedRuntimeIncludesChildSessionMessagesUnderRootSession()
    {
        long t0 = T0.ToUnixTimeMilliseconds();
        string export = """
            {
              "session_id": "sess-kilo-root",
              "messages": [
                {"id": "msg-root-a1", "session_id": "sess-kilo-root", "time_created": __KILO_TS_0__, "data": {"role": "assistant", "modelID": "claude-opus-4-6", "tokens_input": 100, "tokens_output": 20}},
                {"id": "msg-child-a1", "session_id": "sess-kilo-child", "time_created": __KILO_TS_1__, "data": {"role": "assistant", "modelID": "claude-opus-4-6", "tokens_input": 50, "tokens_output": 10}}
              ],
              "parts": [
                {"id": "part-root", "message_id": "msg-root-a1", "time_created": __KILO_TS_0__, "data": {"type": "text", "text": "root answer"}},
                {"id": "part-child", "message_id": "msg-child-a1", "time_created": __KILO_TS_1__, "data": {"type": "text", "text": "child answer"}}
              ]
            }
            """
            .Replace("__KILO_TS_0__", (t0).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__KILO_TS_1__", (t0 + 1000).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/.local/share/kilo/kilo.db",
            export,
            KiloMapper.KiloSharedRuntimeHarnessId,
            format: "kilo-sqlite",
            parserId: "kilo-sqlite-reader");

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        Assert.Equal(2, batch.Records.Count);
        Assert.Equal(
            "synth:kilo-shared-runtime:sess-kilo-root:msg-child-a1",
            batch.Records[1].RecordId);
        Assert.All(batch.Records, static r => Assert.Equal("sess-kilo-root", r.SessionKey));
    }

    [Fact]
    public void Map_SharedRuntimeWithholdsJsonDocumentTitle()
    {
        long t0 = T0.ToUnixTimeMilliseconds();
        string export = """
            {
              "session_id": "sess-kilo-json-title",
              "title": "{\"kind\": \"summary\"}",
              "messages": [
                {"id": "msg-a1", "session_id": "sess-kilo-json-title", "time_created": __KILO_TS_JT__, "data": {"role": "assistant", "modelID": "claude-opus-4-6", "tokens": {"input": 10, "output": 2}}}
              ],
              "parts": [
                {"id": "part-a1", "message_id": "msg-a1", "time_created": __KILO_TS_JT__, "data": {"type": "text", "text": "hello"}}
              ]
            }
            """
            .Replace("__KILO_TS_JT__", t0.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/.local/share/kilo/kilo.db",
            export,
            KiloMapper.KiloSharedRuntimeHarnessId,
            format: "kilo-sqlite",
            parserId: "kilo-sqlite-reader");

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        Assert.Null(Assert.Single(batch.Sessions).Title);
    }

    [Fact]
    public void Map_SharedRuntimeCountsPartsUnderTheMessageModelOwnTokenizer()
    {
        long t0 = T0.ToUnixTimeMilliseconds();
        string export = """
            {
              "session_id": "sess-kilo-tok",
              "messages": [
                {"id": "msg-a1", "session_id": "sess-kilo-tok", "time_created": __KILO_TS__, "data": {"role": "assistant", "modelID": "gpt-4", "tokens": {"input": 10, "output": 2}}}
              ],
              "parts": [
                {"id": "part-a1", "message_id": "msg-a1", "time_created": __KILO_TS__, "data": {"type": "text", "text": "{\"summary\":\"こんにちは世界、今日はいい天気ですね。\"}"}}
              ]
            }
            """
            .Replace("__KILO_TS__", t0.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/.local/share/kilo/kilo.db",
            export,
            KiloMapper.KiloSharedRuntimeHarnessId,
            format: "kilo-sqlite",
            parserId: "kilo-sqlite-reader");

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("gpt-4", record.Model);
        CollectedContentPart part = Assert.Single(record.Parts);
        Assert.Null(part.PlainText);
        Assert.NotNull(part.Withheld);
        Assert.Equal(
            LocalTextTokenizer.Shared.Count(DivergentJsonText, "gpt-4").Count,
            part.Withheld!.MeasuredTokens);
    }

    [Fact]
    public void Map_SharedRuntimeExtractsSkillsAndSubagentInvocations()
    {
        long t0 = T0.ToUnixTimeMilliseconds();
        string export = """
            {
              "session_id": "sess-kilo-agent",
              "messages": [
                {"id": "msg-a1", "session_id": "sess-kilo-agent", "time_created": __KILO_TS_2__, "data": {"role": "assistant", "modelID": "claude-opus-4-6", "tokens": {"input": 60, "output": 12}}}
              ],
              "parts": [
                {"id": "p-skill", "message_id": "msg-a1", "time_created": __KILO_TS_2__, "data": {"type": "tool", "callID": "call-skill", "tool": "skill", "state": {"status": "completed", "input": {"name": "pdf-fill"}, "output": "skill applied"}}},
                {"id": "p-task", "message_id": "msg-a1", "time_created": __KILO_TS_2__, "data": {"type": "tool", "callID": "call-task", "tool": "task", "state": {"status": "completed", "input": {"subagent_type": "Explore"}, "output": "done"}}}
              ]
            }
            """
            .Replace("__KILO_TS_2__", (t0).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/.local/share/kilo/kilo.db",
            export,
            KiloMapper.KiloSharedRuntimeHarnessId,
            format: "kilo-sqlite",
            parserId: "kilo-sqlite-reader");

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        CollectedRecord parent = batch.Records.Single(static r => r.Op == RecordOp.LlmInvoke);
        Assert.Contains("pdf-fill", parent.SkillsInvoked);

        CollectedRecord agent = Assert.Single(batch.Records, static r => r.Op == RecordOp.AgentInvoke);
        Assert.Equal("Explore", agent.Agent?.AgentName);
        Assert.Equal("call-task", agent.Agent?.SpawningCallId);
        CollectedRecord taskTool = batch.Records.Single(static r =>
            r.Op == RecordOp.ToolInvoke && r.Tool != null && string.Equals(r.Tool.CallId, "call-task", StringComparison.Ordinal));
        Assert.Equal(taskTool.RecordId, agent.ParentRecordId);
    }

    [Fact]
    public void Map_LegacyUiMessagesProducesRecordsUnderLegacyHarnessId()
    {
        string uiMessages = BuildLegacyUiMessages();
        string companion = """[{"role":"user","content":[{"text":"<model>anthropic/claude-opus-4-6</model> Current Workspace Directory (/Users/hal/myproject)"}]}]""";
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/tasks/task-kilo-1/ui_messages.json",
            uiMessages,
            KiloMapper.KiloVscodeLegacyHarnessId,
            format: "kilo-ui-messages-json",
            parserId: "kilo-vscode-reader",
            companionText: companion);

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Equal("kilo-vscode-legacy", batch.Source.HarnessId);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("task-kilo-1", session.SessionKey);
        Assert.Equal("task-kilo-1", session.NativeSessionId);
        Assert.Equal("kilo-vscode-legacy", session.HarnessId);
        Assert.Equal("/Users/hal/myproject", session.Workspace?.WorkingDirectory);

        Assert.Equal(2, batch.Records.Count);
        Assert.All(batch.Records, static r => Assert.Equal("kilo-vscode-legacy", r.HarnessId));
        Assert.All(batch.Records, static r => Assert.Equal(RecordOp.LlmInvoke, r.Op));

        CollectedRecord first = batch.Records[0];
        Assert.Equal("synth:kilo-vscode-legacy:task-kilo-1:req-0", first.RecordId);
        Assert.Equal("claude-opus-4-6", first.Model);
        Assert.Equal(T0, first.StartedAt);
        Assert.Equal(120, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(30, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(5, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.CacheRead).Count);
        Assert.Equal(2, Assert.Single(first.Tokens, static t => t.TokenClass == TokenClass.CacheWrite).Count);
        Assert.NotNull(first.ReportedCost);
        Assert.Equal(0.012, first.ReportedCost.Amount, precision: 9);
        Assert.Equal("USD", first.ReportedCost.Currency);
        CollectedContentPart userPart = Assert.Single(first.Parts);
        Assert.Equal(ContentBucket.ConversationHistory, userPart.Bucket);
        Assert.Equal("please inspect the bug", userPart.PlainText);
        Assert.Equal("user", userPart.Role);

        CollectedRecord second = batch.Records[1];
        Assert.Equal("synth:kilo-vscode-legacy:task-kilo-1:req-3", second.RecordId);
        Assert.Equal(40, Assert.Single(second.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(8, Assert.Single(second.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Null(second.ReportedCost);
        Assert.Empty(second.Parts);
        Assert.Equal(BaselineUtc, second.StartedAt);
    }

    [Fact]
    public void Map_LegacyUiMessagesWithoutCompanionFallsBackToClineAutoModel()
    {
        long t0 = T0.ToUnixTimeMilliseconds();
        string uiMessages = """
            [
              {"type": "say", "say": "api_req_started", "text": "{\"tokensIn\": 10, \"tokensOut\": 4}", "ts": __KILO_TS_3__}
            ]
            """
            .Replace("__KILO_TS_3__", (t0).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/tasks/task-kilo-2/ui_messages.json",
            uiMessages,
            KiloMapper.KiloVscodeLegacyHarnessId,
            format: "kilo-ui-messages-json",
            parserId: "kilo-vscode-reader");

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("cline-auto", record.Model);
        Assert.Equal("task-kilo-2", record.SessionKey);
    }

    [Fact]
    public void Map_MalformedPayloadEmitsProblemWithoutRecords()
    {
        NativeSourceUnit shared = MakeNativeUnit(
            "/fake-home/.local/share/kilo/kilo.db",
            "{not json",
            KiloMapper.KiloSharedRuntimeHarnessId,
            format: "kilo-sqlite",
            parserId: "kilo-sqlite-reader");
        CollectedBatch sharedBatch = new KiloMapper().Map(shared, BaselineUtc);
        Assert.Empty(sharedBatch.Records);
        Assert.Empty(sharedBatch.Sessions);
        CollectedProblem sharedProblem = Assert.Single(sharedBatch.Problems);
        Assert.Equal(KiloMapper.MalformedLineCode, sharedProblem.Code);

        NativeSourceUnit legacy = MakeNativeUnit(
            "/fake-home/tasks/task-kilo-9/ui_messages.json",
            "[not json",
            KiloMapper.KiloVscodeLegacyHarnessId,
            format: "kilo-ui-messages-json",
            parserId: "kilo-vscode-reader");
        CollectedBatch legacyBatch = new KiloMapper().Map(legacy, BaselineUtc);
        Assert.Empty(legacyBatch.Records);
        Assert.Empty(legacyBatch.Sessions);
        Assert.Equal(KiloMapper.MalformedLineCode, Assert.Single(legacyBatch.Problems).Code);
    }

    [Fact]
    public void Map_NullPayloadRecordsOneWarningNamingTheSource()
    {
        const string sourceKey = "/fake-home/.local/share/kilo/kilo.db";
        NativeSourceUnit unit = new(
            Unit: new SourceUnit(
                HarnessId: KiloMapper.KiloSharedRuntimeHarnessId,
                SourceKey: sourceKey,
                ProviderId: "kilo",
                ParserId: "kilo-sqlite-reader",
                ParserContractVersion: "1",
                Format: "kilo-sqlite",
                SourceRootLabel: "kilo",
                RevisionToken: "1-1",
                LastWriteUtc: BaselineUtc,
                SizeBytes: 0),
            RawText: null,
            SizeBytes: 0,
            LastWriteUtc: BaselineUtc,
            CompanionText: null);

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(KiloMapper.MalformedLineCode, problem.Code);
        Assert.Equal("warning", problem.Severity);
        Assert.Contains(sourceKey, problem.Message, StringComparison.Ordinal);
        Assert.Contains("carried no readable content", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_WhitespacePayloadKeepsEmptyBatchWithoutProblem()
    {
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/.local/share/kilo/kilo.db",
            " \n\t ",
            KiloMapper.KiloSharedRuntimeHarnessId,
            format: "kilo-sqlite",
            parserId: "kilo-sqlite-reader");

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_EmptyUnitReturnsEmptyBatch()
    {
        NativeSourceUnit unit = MakeNativeUnit(
            "/fake-home/.local/share/kilo/kilo.db",
            "   ",
            KiloMapper.KiloSharedRuntimeHarnessId,
            format: "kilo-sqlite",
            parserId: "kilo-sqlite-reader");

        CollectedBatch batch = new KiloMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void FieldRegister_DocumentsAllKiloNativePropertiesAgainstCatalog()
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

                inSection = string.Equals(line[3..].Trim(), "kilo", StringComparison.Ordinal);
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
            typeof(KiloSharedRuntimeDocument),
            typeof(KiloMessageRow),
            typeof(KiloPartRow),
            typeof(KiloMessageData),
            typeof(KiloPartData),
            typeof(KiloLegacyDocument),
            typeof(KiloLegacyRequest),
            typeof(KiloParseProblem),
        ];

        HashSet<(string TypeName, string PropertyName)> reflected = [];
        foreach (Type nativeType in nativeTypes)
        {
            foreach (PropertyInfo property in nativeType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(static p => p.GetMethod is not null && !string.Equals(p.Name, "EqualityContract", StringComparison.Ordinal)))
            {
                (string, string) key = (nativeType.Name, property.Name);
                Assert.True(reflected.Add(key));
                Assert.True(rows.ContainsKey(key), $"Missing field-register row for '{nativeType.Name}.{property.Name}' under '## kilo'.");
            }
        }

        foreach ((string TypeName, string PropertyName) key in rows.Keys)
        {
            Assert.True(reflected.Contains(key), $"Stale field-register row '{key.TypeName}.{key.PropertyName}' under '## kilo'.");

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

    private static string BuildSharedRuntimeExport()
    {
        long t0 = T0.ToUnixTimeMilliseconds();
        return """
            {
              "session_id": "sess-kilo-reader",
              "title": "Kilo reader contract",
              "messages": [
                {"id": "msg-a3", "session_id": "sess-kilo-reader", "time_created": __KILO_TS_4__, "data": {"role": "assistant", "modelID": "claude-opus-4-6", "tokens": {"input": 7, "output": 3}}},
                {"id": "msg-u1", "session_id": "sess-kilo-reader", "time_created": __KILO_TS_5__, "data": {"role": "user"}},
                {"id": "msg-a2", "session_id": "sess-kilo-reader", "time_created": __KILO_TS_6__, "data": {"role": "assistant", "modelID": "claude-opus-4-6", "tokens_input": 300, "tokens_output": 60}},
                {"id": "msg-router", "session_id": "sess-kilo-reader", "time_created": __KILO_TS_7__, "data": {"role": "assistant", "modelID": "edenai/router-model"}},
                {"id": "msg-a1", "session_id": "sess-kilo-reader", "time_created": __KILO_TS_8__, "data": {"role": "assistant", "modelID": "claude-opus-4-6", "tokens": {"input": 100, "output": 20, "reasoning": 0, "cache": {"read": 0, "write": 0}}}},
                {"id": "msg-u2", "session_id": "sess-kilo-reader", "time_created": __KILO_TS_9__, "data": {"role": "user"}}
              ],
              "parts": [
                {"id": "part-u1-text", "message_id": "msg-u1", "time_created": __KILO_TS_5__, "data": {"type": "text", "text": "first question"}},
                {"id": "part-a1-text", "message_id": "msg-a1", "time_created": __KILO_TS_8__, "data": {"type": "text", "text": "first answer"}},
                {"id": "part-a1-tool", "message_id": "msg-a1", "time_created": __KILO_TS_8__, "data": {"type": "tool", "callID": "call-1", "tool": "bash", "state": {"status": "completed", "input": {"command": "ls"}, "output": "file.txt\n"}}},
                {"id": "part-a1-reasoning", "message_id": "msg-a1", "time_created": __KILO_TS_8__, "data": {"type": "reasoning", "text": "weighing the options"}},
                {"id": "part-router-text", "message_id": "msg-router", "time_created": __KILO_TS_7__, "data": {"type": "text", "text": "router filler answer"}},
                {"id": "part-u2-text", "message_id": "msg-u2", "time_created": __KILO_TS_9__, "data": {"type": "text", "text": "second question"}},
                {"id": "part-a2-text-1", "message_id": "msg-a2", "time_created": __KILO_TS_6__, "data": {"type": "text", "text": "second answer"}},
                {"id": "part-a2-text-2", "message_id": "msg-a2", "time_created": __KILO_TS_6__, "data": {"type": "text", "text": "second answer, continued"}},
                {"id": "part-a2-tool", "message_id": "msg-a2", "time_created": __KILO_TS_6__, "data": {"type": "tool", "callID": "call-2", "tool": "read", "state": {"status": "error", "input": {"filePath": "/nope"}, "output": "ENOENT: no such file"}}},
                {"id": "part-a2-step-start", "message_id": "msg-a2", "time_created": __KILO_TS_6__, "data": {"type": "step-start"}},
                {"id": "part-a2-step-finish", "message_id": "msg-a2", "time_created": __KILO_TS_6__, "data": {"type": "step-finish", "reason": "stop"}},
                {"id": "part-a2-patch", "message_id": "msg-a2", "time_created": __KILO_TS_6__, "data": {"type": "patch", "hash": "abc123"}},
                {"id": "part-a2-file", "message_id": "msg-a2", "time_created": __KILO_TS_6__, "data": {"type": "file", "filename": "src/app.ts"}},
                {"id": "part-a2-compaction", "message_id": "msg-a2", "time_created": __KILO_TS_6__, "data": {"type": "compaction", "auto": true}},
                {"id": "part-a3-text", "message_id": "msg-a3", "time_created": __KILO_TS_4__, "data": {"type": "text", "text": "third answer"}}
              ]
            }
            """
            .Replace("__KILO_TS_4__", (t0 + 300000).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__KILO_TS_5__", (t0).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__KILO_TS_6__", (t0 + 240000).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__KILO_TS_7__", (t0 + 120000).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__KILO_TS_8__", (t0 + 60000).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__KILO_TS_9__", (t0 + 180000).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string BuildLegacyUiMessages()
    {
        long t0 = T0.ToUnixTimeMilliseconds();
        return """
            [
              {"type": "say", "say": "text", "text": "please inspect the bug"},
              {"type": "say", "say": "api_req_started", "text": "{\"tokensIn\": 120, \"tokensOut\": 30, \"cacheReads\": 5, \"cacheWrites\": 2, \"cost\": 0.012}", "ts": __KILO_TS_10__},
              {"type": "say", "say": "api_req_started", "text": "{\"tokensIn\": 0, \"tokensOut\": 0}", "ts": __KILO_TS_11__},
              {"type": "say", "say": "api_req_started", "text": "not-json{{{", "ts": __KILO_TS_12__},
              {"type": "say", "say": "api_req_started", "text": "{\"tokensIn\": 40, \"tokensOut\": 8}", "ts": "not-a-time"}
            ]
            """
            .Replace("__KILO_TS_10__", (t0).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__KILO_TS_11__", (t0 + 60000).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__KILO_TS_12__", (t0 + 120000).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static NativeSourceUnit MakeNativeUnit(
        string path,
        string rawText,
        string harnessId,
        string format,
        string parserId,
        string? companionText = null)
    {
        SourceUnit sourceUnit = new(
            HarnessId: harnessId,
            SourceKey: Path.GetFullPath(path),
            ProviderId: "kilo",
            ParserId: parserId,
            ParserContractVersion: "1",
            Format: format,
            SourceRootLabel: "kilo",
            RevisionToken: "1-1",
            LastWriteUtc: BaselineUtc,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText));

        return new NativeSourceUnit(
            Unit: sourceUnit,
            RawText: rawText,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: BaselineUtc,
            CompanionText: companionText);
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
