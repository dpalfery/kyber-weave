using System.Globalization;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.KimiCode;
using KyberDash.Data.Harnesses.RooCode;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="KimiCodeMapper"/> and <see cref="RooCodeMapper"/> porting
/// <c>dash/src/providers/kimicode.ts</c> (Kimi Code <c>wire.jsonl</c> sessions with optional
/// <c>state.json</c> companion) and <c>dash/src/providers/roo-code.ts</c> (Roo Code cline-style
/// <c>ui_messages.json</c> tasks with optional <c>api_conversation_history.json</c> companion)
/// into typed static DTOs and canonical relational <see cref="CollectedBatch"/> records.
/// </summary>
public class KimiRooMapperTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 7, 1, 10, 5, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // Kimi Code wire fixtures
    // ------------------------------------------------------------------

    private static string KimiPrompt(string text, long timeMillis) =>
        $"{{\"type\":\"turn.prompt\",\"input\":[{{\"type\":\"text\",\"text\":{JsonQuote(text)}}}],\"origin\":{{\"kind\":\"user\"}},\"time\":{timeMillis.ToString(CultureInfo.InvariantCulture)}}}";

    private static string KimiRequest(string turnStep, string modelAlias, string model, long timeMillis) =>
        $"{{\"type\":\"llm.request\",\"kind\":\"loop\",\"provider\":\"fixture-provider\",\"model\":{JsonQuote(model)},\"modelAlias\":{JsonQuote(modelAlias)},\"maxTokens\":4096,\"messageCount\":2,\"turnStep\":{JsonQuote(turnStep)},\"time\":{timeMillis.ToString(CultureInfo.InvariantCulture)}}}";

    private static string KimiUsage(
        string modelAlias,
        long timeMillis,
        long input,
        long output,
        long cacheRead = 0,
        long cacheWrite = 0) =>
        $"{{\"type\":\"usage.record\",\"model\":{JsonQuote(modelAlias)},\"usage\":{{\"inputOther\":{input.ToString(CultureInfo.InvariantCulture)},\"output\":{output.ToString(CultureInfo.InvariantCulture)},\"inputCacheRead\":{cacheRead.ToString(CultureInfo.InvariantCulture)},\"inputCacheCreation\":{cacheWrite.ToString(CultureInfo.InvariantCulture)}}},\"usageScope\":\"turn\",\"time\":{timeMillis.ToString(CultureInfo.InvariantCulture)}}}";

    private static string KimiTool(string name, int turnId, string argsJson) =>
        $"{{\"type\":\"context.append_loop_event\",\"event\":{{\"type\":\"tool.call\",\"name\":{JsonQuote(name)},\"args\":{argsJson},\"turnId\":{turnId.ToString(CultureInfo.InvariantCulture)},\"step\":1,\"toolCallId\":\"tool-{turnId.ToString(CultureInfo.InvariantCulture)}-{name}\"}}}}";

    private static string KimiStateJson(string workDir, params string[] agentIds)
    {
        string agents = string.Join(',', agentIds.Select(static agentId =>
        {
            string parent = agentId == "main" ? "null" : "\"main\"";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"\"{agentId}\":{{\"homedir\":\"/workspace\",\"type\":\"worker\",\"parentAgentId\":{parent}}}");
        }));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"createdAt\":\"2026-07-01T10:00:00.000Z\",\"updatedAt\":\"2026-07-01T10:05:00.000Z\",\"title\":\"Sanitized fixture\",\"isCustomTitle\":false,\"workDir\":{JsonQuote(workDir)},\"lastPrompt\":\"sanitized fixture prompt\",\"agents\":{{{agents}}}}}");
    }

    private static string KimiWirePath(string sessionDirName, string agentId = "main") =>
        Path.GetFullPath(Path.Combine(
            "home", "u", ".kimi-code", "sessions", "wd_neutral-project_0123456789ab",
            sessionDirName, "agents", agentId, "wire.jsonl"));

    private static string JsonQuote(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    [Fact]
    public void Map_ParsesSingleTurnUsageWithRealModelIdAndAliasResolution()
    {
        string wire = string.Join('\n',
        [
            KimiPrompt("Summarize the neutral module.", 1782900000000),
            KimiRequest("0.1", "friendly-alias", "kimi-k3", 1782900001000),
            KimiUsage("friendly-alias", 1782900002000, input: 100, output: 40, cacheRead: 25, cacheWrite: 10),
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(
            "kimi-code",
            KimiWirePath("session_single-turn"),
            wire,
            companionText: KimiStateJson("/workspace/neutral-project", "main"));

        KimiCodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal("kimi-code", mapper.HarnessId);
        Assert.Empty(batch.Problems);
        Assert.Equal("kimi-code", batch.Source.HarnessId);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("single-turn", session.SessionKey);
        Assert.Equal("single-turn", session.NativeSessionId);
        Assert.Equal("kimi-code", session.HarnessId);
        Assert.NotNull(session.Workspace);
        Assert.Equal("/workspace/neutral-project", session.Workspace.WorkingDirectory);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal("kimi-code:kimi-k3", record.Name);
        Assert.Equal("kimi-k3", record.Model);
        Assert.NotEqual("friendly-alias", record.Model);
        Assert.Equal("synth:kimi-code:single-turn:main:3:0", record.RecordId);
        Assert.Equal("synth:kimi-code:single-turn", record.TraceId);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-01T10:00:02.000Z", CultureInfo.InvariantCulture),
            record.StartedAt);
        Assert.Equal("Summarize the neutral module.", record.Parts[0].PlainText);
        Assert.Null(record.ReportedCost);

        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 100);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 40);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead && t.Count == 25);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite && t.Count == 10);
        Assert.Equal(135, record.ReportedInputTokens);
        Assert.Equal(40, record.ReportedOutputTokens);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(1, store.CountRecords("kimi-code"));
        Assert.Equal(4, store.TokenCounts(spanId: record.RecordId).Count);
    }

    [Fact]
    public void Map_UsesUsageAliasToResolveTheExactRequestModel()
    {
        string wire = string.Join('\n',
        [
            KimiPrompt("Compare two neutral model requests.", 1782900050000),
            KimiRequest("0.1", "first-alias", "kimi-k3", 1782900051000),
            KimiRequest("0.2", "second-alias", "glm-5.2", 1782900052000),
            KimiUsage("first-alias", 1782900053000, input: 24, output: 6),
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("kimi-code", KimiWirePath("session_alias-model"), wire);

        CollectedBatch batch = new KimiCodeMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("kimi-k3", record.Model);
    }

    [Fact]
    public void Map_AttachesNormalizedToolCallsAsChildSpans()
    {
        string wire = string.Join('\n',
        [
            KimiPrompt("Inspect and verify the neutral fixture.", 1782900100000),
            KimiRequest("0.1", "glm-alias", "glm-5.2", 1782900101000),
            KimiTool("Write", 0, "{\"path\":\"notes.txt\",\"content\":\"neutral\"}"),
            KimiTool("Read", 0, "{\"path\":\"notes.txt\"}"),
            KimiTool("Bash", 0, "{\"command\":\"npm test && git status\"}"),
            KimiTool("Grep", 0, "{\"pattern\":\"neutral\"}"),
            KimiUsage("glm-alias", 1782900102000, input: 80, output: 20),
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("kimi-code", KimiWirePath("session_multi-tool"), wire);

        CollectedBatch batch = new KimiCodeMapper().Map(unit, BaselineUtc);

        Assert.Equal(5, batch.Records.Count);
        CollectedRecord parent = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, parent.Op);
        Assert.Equal("glm-5.2", parent.Model);

        string[] childNames = batch.Records.Skip(1).Select(static r => r.Tool!.ToolName).ToArray();
        Assert.Equal(["Write", "Read", "Bash", "Grep"], childNames);
        Assert.All(batch.Records.Skip(1), static r => Assert.Equal(RecordOp.ToolInvoke, r.Op));
        string parentRecordId = parent.RecordId;
        Assert.All(batch.Records.Skip(1), r => Assert.Equal(parentRecordId, r.ParentRecordId));
        CollectedRecord bashChild = Assert.Single(batch.Records.Skip(1), static r => r.Tool!.ToolName == "Bash");
        Assert.NotNull(bashChild.Tool!.Input);
        Assert.Equal(64, bashChild.Tool.Input.Sha256Fingerprint?.Length);
        Assert.True(bashChild.Tool.Input.Utf8Bytes > 0);
    }

    [Fact]
    public void Map_DoesNotCarryFailedTurnToolsIntoTheNextUsage()
    {
        string wire = string.Join('\n',
        [
            KimiPrompt("Run a neutral command in the first turn.", 1782900150000),
            KimiRequest("0.1", "first-alias", "kimi-k3", 1782900151000),
            KimiTool("Bash", 0, "{\"command\":\"npm test\"}"),
            KimiPrompt("Continue with a clean second turn.", 1782900160000),
            KimiUsage("first-alias", 1782900161000, input: 18, output: 4),
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("kimi-code", KimiWirePath("session_failed-turn-tools"), wire);

        CollectedBatch batch = new KimiCodeMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.DoesNotContain(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.Equal("Run a neutral command in the first turn.", record.Parts[0].PlainText);
    }

    [Fact]
    public void Map_KeepsMultipleTurnsWithPerTurnPromptsAndModels()
    {
        string wire = string.Join('\n',
        [
            KimiPrompt("Review the first neutral file.", 1782900200000),
            KimiRequest("0.1", "first-alias", "kimi-k3", 1782900201000),
            KimiUsage("first-alias", 1782900202000, input: 20, output: 5),
            KimiPrompt("Now review the second neutral file.", 1782900210000),
            KimiRequest("1.1", "second-alias", "glm-5.2", 1782900211000),
            KimiUsage("second-alias", 1782900212000, input: 30, output: 7),
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("kimi-code", KimiWirePath("session_multi-turn"), wire);

        CollectedBatch batch = new KimiCodeMapper().Map(unit, BaselineUtc);

        Assert.Equal(2, batch.Records.Count);
        Assert.All(batch.Records, static r => Assert.Equal("multi-turn", r.SessionKey));
        Assert.Equal(["kimi-k3", "glm-5.2"], batch.Records.Select(static r => r.Model).ToArray());
        Assert.Equal(
            ["Review the first neutral file.", "Now review the second neutral file."],
            batch.Records.Select(static r => r.Parts[0].PlainText).ToArray());
    }

    [Fact]
    public void Map_ReturnsSessionWithoutRecordsForRetryOnlyWire()
    {
        string wire = string.Join('\n',
        [
            KimiPrompt("Perform a neutral operation.", 1782900300000),
            KimiRequest("0.1", "retry-alias", "kimi-k3", 1782900301000),
            KimiRequest("0.1", "retry-alias", "kimi-k3", 1782900302000),
            KimiRequest("0.1", "retry-alias", "kimi-k3", 1782900303000),
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("kimi-code", KimiWirePath("session_failed-retry"), wire);

        CollectedBatch batch = new KimiCodeMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("failed-retry", session.SessionKey);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_SkipsMalformedLinesAndContinuesWithLaterEvents()
    {
        string wire = string.Join('\n',
        [
            KimiPrompt("Continue after malformed fixture data.", 1782900500000),
            "{not valid json",
            KimiRequest("0.1", "safe-alias", "kimi-k3", 1782900501000),
            "null",
            KimiUsage("safe-alias", 1782900502000, input: 12, output: 3),
            "{\"type\":\"context.append_loop_event\",\"event\":",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("kimi-code", KimiWirePath("session_malformed-lines"), wire);

        CollectedBatch batch = new KimiCodeMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("kimi-k3", record.Model);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 12);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 3);
        Assert.NotEmpty(batch.Problems);
        Assert.All(batch.Problems, static p => Assert.Equal(KimiCodeMapper.MalformedLineCode, p.Code));
    }

    [Fact]
    public void Map_DerivesSessionAndAgentIdsFromWirePath()
    {
        string wire = KimiPrompt("Hello from an embedded runtime.", 1782900000000) + "\n"
            + KimiRequest("0.1", "k3-agent", "k3-agent", 1782900001000) + "\n"
            + KimiUsage("k3-agent", 1782900002000, input: 100, output: 50) + "\n";

        string wirePath = Path.GetFullPath(Path.Combine(
            "home", "u", ".kimi-code", "sessions", "wd_neutral-project_0123456789ab",
            "conv-abc123def456", "agents", "helper", "wire.jsonl"));
        NativeSourceUnit unit = MakeNativeUnit("kimi-code", wirePath, wire);

        CollectedBatch batch = new KimiCodeMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("conv-abc123def456", record.SessionKey);
        Assert.Equal("synth:kimi-code:conv-abc123def456:helper:3:0", record.RecordId);
    }

    [Fact]
    public void Parse_ResolvesProviderRecordedLineageFromCompanionState()
    {
        string state = KimiStateJson("/workspace/neutral-project", "main", "agent-helper");
        KimiCodeSessionState? parsed = KimiCodeMapper.ParseState(state);

        Assert.Equal(
            new KimiCodeLineage(ParentSessionId: "main", Role: "child", Evidence: "provider-recorded"),
            KimiCodeMapper.ResolveLineage(parsed, "agent-helper"));
        Assert.Equal(
            new KimiCodeLineage(ParentSessionId: null, Role: "root", Evidence: "provider-recorded"),
            KimiCodeMapper.ResolveLineage(parsed, "main"));
        Assert.Null(KimiCodeMapper.ResolveLineage(null, "main"));
        Assert.Null(KimiCodeMapper.ResolveLineage(KimiCodeMapper.ParseState("{}"), "main"));
    }

    // ------------------------------------------------------------------
    // Roo Code task fixtures
    // ------------------------------------------------------------------

    private static string RooUiMessages(
        int tokensIn,
        int tokensOut,
        int cacheReads = 0,
        int cacheWrites = 0,
        double? cost = null,
        string? userMessage = null,
        long ts = 1700000001000) =>
        "[" + string.Join(',', BuildRooUiMessages(tokensIn, tokensOut, cacheReads, cacheWrites, cost, userMessage, ts)) + "]";

    private static IEnumerable<string> BuildRooUiMessages(
        int tokensIn,
        int tokensOut,
        int cacheReads,
        int cacheWrites,
        double? cost,
        string? userMessage,
        long ts)
    {
        if (userMessage is not null)
        {
            yield return $"{{\"type\":\"say\",\"say\":\"user_feedback\",\"text\":{JsonQuote(userMessage)},\"ts\":1700000000000}}";
        }

        string payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"tokensIn\":{tokensIn},\"tokensOut\":{tokensOut},\"cacheReads\":{cacheReads},\"cacheWrites\":{cacheWrites}");
        if (cost.HasValue)
        {
            payload += string.Create(CultureInfo.InvariantCulture, $",\"cost\":{cost.Value}");
        }

        payload += "}";
        yield return $"{{\"type\":\"say\",\"say\":\"api_req_started\",\"text\":{JsonQuote(payload)},\"ts\":{ts.ToString(CultureInfo.InvariantCulture)}}}";
    }

    private static string RooHistoryJson(string? model = null, string? workspace = null)
    {
        StringBuilder environment = new("hello\\n<environment_details>");
        if (model is not null)
        {
            _ = environment.Append(CultureInfo.InvariantCulture, $"<model>{model}</model>");
        }

        if (workspace is not null)
        {
            _ = environment.Append(CultureInfo.InvariantCulture, $"Current Workspace Directory ({workspace})");
        }

        _ = environment.Append("</environment_details>");
        return "[" +
            $"{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":{JsonQuote(environment.ToString())}}}]}}" +
            ",{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"response\"}]}]";
    }

    private static string RooTaskDir(string taskId) =>
        Path.GetFullPath(Path.Combine("home", "u", ".config", "Code", "User", "globalStorage",
            "rooveterinaryinc.roo-cline", "tasks", taskId));

    [Fact]
    public void Map_ParsesTokensCostAndUserMessageFromUiMessages()
    {
        string uiMessages = RooUiMessages(
            tokensIn: 200, tokensOut: 100, cacheReads: 50, cacheWrites: 30,
            cost: 0.05, userMessage: "fix the bug");
        string history = RooHistoryJson(model: "anthropic/claude-sonnet-4", workspace: "/workspace/neutral-project");
        NativeSourceUnit unit = MakeNativeUnit("roo-code", RooTaskDir("task-001"), uiMessages, companionText: history);

        RooCodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal("roo-code", mapper.HarnessId);
        Assert.Empty(batch.Problems);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("task-001", session.SessionKey);
        Assert.Equal("task-001", session.NativeSessionId);
        Assert.NotNull(session.Workspace);
        Assert.Equal("/workspace/neutral-project", session.Workspace.WorkingDirectory);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal("roo-code", record.HarnessId);
        Assert.Equal("claude-sonnet-4", record.Model);
        Assert.Equal("synth:roo-code:task-001:0", record.RecordId);
        Assert.Equal(
            DateTimeOffset.Parse("2023-11-14T22:13:21.000Z", CultureInfo.InvariantCulture),
            record.StartedAt);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 200);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 100);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead && t.Count == 50);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite && t.Count == 30);
        Assert.NotNull(record.ReportedCost);
        Assert.Equal(0.05, record.ReportedCost.Amount, 6);
        Assert.Equal("USD", record.ReportedCost.Currency);
        Assert.Equal("fix the bug", record.Parts[0].PlainText);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(1, store.CountRecords("roo-code"));
    }

    [Fact]
    public void Map_ExtractsBareModelNameFromHistoryEnvironmentDetails()
    {
        string uiMessages = RooUiMessages(tokensIn: 100, tokensOut: 50);
        NativeSourceUnit unit = MakeNativeUnit(
            "roo-code", RooTaskDir("task-002"), uiMessages,
            companionText: RooHistoryJson(model: "claude-sonnet-4-5"));

        CollectedBatch batch = new RooCodeMapper().Map(unit, BaselineUtc);

        Assert.Equal("claude-sonnet-4-5", Assert.Single(batch.Records).Model);
    }

    [Fact]
    public void Map_FallsBackToClineAutoWithoutModelIndicators()
    {
        string uiMessages = RooUiMessages(tokensIn: 100, tokensOut: 50);
        string history = "[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]},{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}]";
        NativeSourceUnit unit = MakeNativeUnit("roo-code", RooTaskDir("task-003"), uiMessages, companionText: history);

        CollectedBatch batch = new RooCodeMapper().Map(unit, BaselineUtc);

        Assert.Equal("cline-auto", Assert.Single(batch.Records).Model);
    }

    [Fact]
    public void Map_SkipsZeroTokenEntries()
    {
        string uiMessages = "[{\"type\":\"say\",\"say\":\"api_req_started\",\"text\":\"{\\\"tokensIn\\\":0,\\\"tokensOut\\\":0}\",\"ts\":1700000000000}]";
        NativeSourceUnit unit = MakeNativeUnit("roo-code", RooTaskDir("task-007"), uiMessages);

        CollectedBatch batch = new RooCodeMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
    }

    [Fact]
    public void Map_HandlesMissingUiMessagesGracefully()
    {
        NativeSourceUnit unit = MakeNativeUnit("roo-code", RooTaskDir("task-005"), rawText: null);

        CollectedBatch batch = new RooCodeMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_HandlesInvalidJsonGracefully()
    {
        NativeSourceUnit unit = MakeNativeUnit("roo-code", RooTaskDir("task-006"), "not valid json");

        CollectedBatch batch = new RooCodeMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(RooCodeMapper.MalformedLineCode, problem.Code);
    }

    [Fact]
    public void Map_LeavesCostUnreportedWhenHarnessReportsNone()
    {
        string uiMessages = RooUiMessages(tokensIn: 1000, tokensOut: 500);
        NativeSourceUnit unit = MakeNativeUnit("roo-code", RooTaskDir("task-008"), uiMessages, companionText: "[]");

        CollectedBatch batch = new RooCodeMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Null(record.ReportedCost);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 1000);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 500);
    }

    [Fact]
    public void Map_FallsBackToLastWriteUtcForInvalidTimestamp()
    {
        string uiMessages = RooUiMessages(tokensIn: 100, tokensOut: 50, ts: 1700000001000)
            .Replace("1700000001000", "\"not-a-timestamp\"", StringComparison.Ordinal);
        NativeSourceUnit unit = MakeNativeUnit("roo-code", RooTaskDir("task-009"), uiMessages);

        CollectedBatch batch = new RooCodeMapper().Map(unit, BaselineUtc);

        Assert.Equal(BaselineUtc, Assert.Single(batch.Records).StartedAt);
    }

    [Fact]
    public void Map_AttachesUserMessageOnlyToFirstRecord()
    {
        StringBuilder uiMessages = new("[");
        _ = uiMessages.Append("{\"type\":\"say\",\"say\":\"user_feedback\",\"text\":\"do two neutral things\",\"ts\":1700000000000},");
        _ = uiMessages.Append("{\"type\":\"say\",\"say\":\"api_req_started\",\"text\":\"{\\\"tokensIn\\\":10,\\\"tokensOut\\\":2}\",\"ts\":1700000001000},");
        _ = uiMessages.Append("{\"type\":\"say\",\"say\":\"api_req_started\",\"text\":\"{\\\"tokensIn\\\":30,\\\"tokensOut\\\":4}\",\"ts\":1700000002000}]");
        NativeSourceUnit unit = MakeNativeUnit("roo-code", RooTaskDir("task-010"), uiMessages.ToString());

        CollectedBatch batch = new RooCodeMapper().Map(unit, BaselineUtc);

        Assert.Equal(2, batch.Records.Count);
        Assert.Equal("synth:roo-code:task-010:0", batch.Records[0].RecordId);
        Assert.Equal("synth:roo-code:task-010:1", batch.Records[1].RecordId);
        Assert.Equal("do two neutral things", batch.Records[0].Parts[0].PlainText);
        Assert.Empty(batch.Records[1].Parts);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static NativeSourceUnit MakeNativeUnit(
        string harnessId,
        string path,
        string? rawText,
        string? companionText = null)
    {
        SourceUnit sourceUnit = new(
            HarnessId: harnessId,
            SourceKey: Path.GetFullPath(path),
            ProviderId: harnessId,
            ParserId: harnessId + "-reader",
            ParserContractVersion: "1",
            Format: harnessId + "-test",
            SourceRootLabel: Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty),
            RevisionToken: "1-1",
            LastWriteUtc: BaselineUtc,
            SizeBytes: rawText is null ? 0 : Encoding.UTF8.GetByteCount(rawText));

        return new NativeSourceUnit(
            Unit: sourceUnit,
            RawText: rawText,
            SizeBytes: rawText is null ? 0 : Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: BaselineUtc,
            CompanionText: companionText);
    }
}
