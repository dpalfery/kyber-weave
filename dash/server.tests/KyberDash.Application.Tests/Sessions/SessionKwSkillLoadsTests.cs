using System.Text;
using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Sessions;

/// <summary>
/// The KW skills section lists the session's KW skill loads (D17) in call order, one row per
/// <c>load_skill</c> call to the kw-docs server, with the skill name read from the reply's header
/// (its first <c>skill: &lt;name&gt;</c> line before the first blank line), the session's agent, and
/// the record's start time. The call's input is never read. The harness skills section is
/// unchanged: a KW load is a tool call, never a harness skill invocation.
/// </summary>
public sealed class SessionKwSkillLoadsTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private const string SessionKey = "sess-kw";
    private const string AgentName = "kw-dev";

    /// <summary>Reader methods that materialize conversation text, so the KW skills path may not issue them.</summary>
    private static readonly string[] ContentBearingReads =
    [
        nameof(InMemoryRelationalStore.SessionRecordsForRebuild),
        nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance),
        nameof(InMemoryRelationalStore.ToolCalls),
        nameof(InMemoryRelationalStore.ToolDefinitions),
        nameof(InMemoryRelationalStore.AgentInvocations),
        nameof(InMemoryRelationalStore.SkillsInvoked),
        nameof(InMemoryRelationalStore.TurnContentParts),
    ];

    /// <summary>
    /// The unsplit tool-name spellings of a kw-docs load: the five D17 names, and the two a harness
    /// writes when the same server is still registered under its retired key <c>kyber-weave</c>.
    /// </summary>
    public static readonly TheoryData<string> UnsplitLoadToolNames = new()
    {
        "mcp__kw-docs__load_skill",
        "mcp__kw_docs__load_skill",
        "kw-docs_load_skill",
        "kw-docs__load_skill",
        "kw-docs/load_skill",
        "mcp__kyber-weave__load_skill",
        "mcp__kyber_weave__load_skill",
    };

    /// <summary>
    /// A load the MCP split records as the kw-docs server's <c>load_skill</c> is a load, and the
    /// row carries the skill named in its reply header, the session's agent, and the record's start time.
    /// </summary>
    [Fact]
    public void AnMcpSplitLoadSkillCallIsALoadWithSkillAgentSessionAndTime()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, SessionKey, [LoadRecord("span-load-1", 3, Call("call-1", "load_skill", "kw-docs", "load_skill", "skill: csharp-dev\n\n# csharp-dev\nGuidance."))]);
        ProjectWithAgent(store, pricing, SessionKey, AgentName);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        JsonElement page = bridge.GetSessionKwSkills(SessionKey)!.Value;

        JsonElement[] rows = [.. page.GetProperty("rows").EnumerateArray()];
        JsonElement row = Assert.Single(rows);
        Assert.Equal("csharp-dev", row.GetProperty("skill").GetString());
        Assert.Equal(AgentName, row.GetProperty("agent").GetString());
        Assert.Equal(SessionKey, row.GetProperty("session_id").GetString());
        Assert.Equal("span-load-1", row.GetProperty("span_id").GetString());
        Assert.Equal("2026-11-19T10:03:00.000Z", row.GetProperty("loaded_at").GetString());
        Assert.Equal(1, page.GetProperty("total").GetInt32());
    }

    /// <summary>Each unsplit D17 tool-name spelling of a kw-docs load is a load.</summary>
    [Theory]
    [MemberData(nameof(UnsplitLoadToolNames))]
    public void AnUnsplitToolNameSpellingOfAKwDocsLoadIsALoad(string toolName)
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, SessionKey, [LoadRecord("span-load-1", 0, Call("call-1", toolName, null, null, "skill: csharp-dev\n\nGuidance."))]);
        ProjectWithAgent(store, pricing, SessionKey, AgentName);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        JsonElement page = bridge.GetSessionKwSkills(SessionKey)!.Value;

        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal("csharp-dev", page.GetProperty("rows")[0].GetProperty("skill").GetString());
    }

    /// <summary>
    /// Machines installed before the rename still register the kw-docs server under its retired key
    /// <c>kyber-weave</c>; a split <c>load_skill</c> call to it under that key is a load.
    /// </summary>
    [Theory]
    [InlineData("kyber-weave")]
    [InlineData("Kyber_Weave")]
    public void ALoadSkillCallToTheServerRegisteredAsKyberWeaveIsALoad(string serverName)
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, SessionKey, [LoadRecord("span-load-1", 0, Call("call-1", $"mcp__{serverName}__load_skill", serverName, "load_skill", "skill: csharp-dev\n\nGuidance."))]);
        ProjectWithAgent(store, pricing, SessionKey, AgentName);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        JsonElement page = bridge.GetSessionKwSkills(SessionKey)!.Value;

        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal("csharp-dev", page.GetProperty("rows")[0].GetProperty("skill").GetString());
    }

    /// <summary>A <c>load_skill</c> call on another MCP server is not a KW skill load.</summary>
    [Fact]
    public void AnotherServersLoadSkillIsNotALoad()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, SessionKey, [LoadRecord("span-other-1", 0, Call("call-1", "load_skill", "other-server", "load_skill", "skill: csharp-dev\n\nGuidance."))]);
        ProjectWithAgent(store, pricing, SessionKey, AgentName);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        JsonElement page = bridge.GetSessionKwSkills(SessionKey)!.Value;

        Assert.Equal(0, page.GetProperty("total").GetInt32());
        Assert.Empty(page.GetProperty("rows").EnumerateArray().ToList());
    }

    /// <summary>
    /// A load whose reply is missing, has no skill header, has a malformed header, or names a skill
    /// that fails the name pattern or its length limit answers a null skill, not a dropped row.
    /// </summary>
    [Fact]
    public void AMissingOrUnrecognizedReplyGivesANullSkill()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, SessionKey,
        [
            LoadRecord("span-noreply", 0, Call("call-1", "load_skill", "kw-docs", "load_skill", null)),
            LoadRecord("span-noheader", 1, Call("call-2", "load_skill", "kw-docs", "load_skill", "Skill body without a header.")),
            LoadRecord("span-malformed", 2, Call("call-3", "load_skill", "kw-docs", "load_skill", "skill csharp-dev\n\nGuidance.")),
            LoadRecord("span-badname", 3, Call("call-4", "load_skill", "kw-docs", "load_skill", "skill: Csharp Dev\n\nGuidance.")),
            LoadRecord("span-toolong", 4, Call("call-5", "load_skill", "kw-docs", "load_skill", $"skill: {new string('a', 65)}\n\nGuidance.")),
        ]);
        ProjectWithAgent(store, pricing, SessionKey, AgentName);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        JsonElement page = bridge.GetSessionKwSkills(SessionKey)!.Value;

        JsonElement[] rows = [.. page.GetProperty("rows").EnumerateArray()];
        Assert.Equal(5, rows.Length);
        Assert.All(rows, static row => Assert.Equal(JsonValueKind.Null, row.GetProperty("skill").ValueKind));
        Assert.Equal(5, page.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// Only the reply's header is read: a <c>skill:</c> line after the first blank line is not the
    /// skill, the first header line decides, and a CRLF line ending is tolerated.
    /// </summary>
    [Fact]
    public void TheSkillLineIsReadOnlyFromTheReplyHeader()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, SessionKey,
        [
            LoadRecord("span-body", 0, Call("call-1", "load_skill", "kw-docs", "load_skill", "Intro text.\n\nskill: csharp-dev\n")),
            LoadRecord("span-first", 1, Call("call-2", "load_skill", "kw-docs", "load_skill", "skill: csharp-dev\r\nskill: dal-dev\r\n\r\nGuidance.")),
        ]);
        ProjectWithAgent(store, pricing, SessionKey, AgentName);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        JsonElement page = bridge.GetSessionKwSkills(SessionKey)!.Value;

        JsonElement[] rows = [.. page.GetProperty("rows").EnumerateArray()];
        Assert.Equal("csharp-dev", rows[0].GetProperty("skill").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("skill").ValueKind);

        JsonElement ascPage = bridge.GetSessionKwSkills(SessionKey, order: "asc")!.Value;
        JsonElement[] ascRows = [.. ascPage.GetProperty("rows").EnumerateArray()];
        Assert.Equal(JsonValueKind.Null, ascRows[0].GetProperty("skill").ValueKind);
        Assert.Equal("csharp-dev", ascRows[1].GetProperty("skill").GetString());
    }

    /// <summary>A session with no KW loads answers an empty page with a zero total.</summary>
    [Fact]
    public void ASessionWithNoLoadsAnswersAnEmptyPage()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, SessionKey, [LoadRecord("span-read-1", 0, Call("call-1", "read_file", null, null, "path=a.txt"))]);
        ProjectWithAgent(store, pricing, SessionKey, AgentName);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        JsonElement page = bridge.GetSessionKwSkills(SessionKey)!.Value;

        Assert.Empty(page.GetProperty("rows").EnumerateArray().ToList());
        Assert.Equal(0, page.GetProperty("total").GetInt32());
        Assert.Equal(25, page.GetProperty("limit").GetInt32());
        Assert.Equal(0, page.GetProperty("offset").GetInt32());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
    }

    /// <summary>Pages come in call order at 25 rows, and every page carries the true total.</summary>
    [Fact]
    public void PagesAreInCallOrderWithTheTotal()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        List<CollectedRecord> records = [];
        for (int index = 0; index < 30; index++)
        {
            records.Add(LoadRecord(
                $"span-load-{index:D2}",
                index,
                Call($"call-{index:D2}", "load_skill", "kw-docs", "load_skill", $"skill: skill-{index:D2}\n\nGuidance.")));
        }

        WriteSession(store, SessionKey, records);
        ProjectWithAgent(store, pricing, SessionKey, AgentName);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();
        store.ClearReadLog();

        JsonElement first = bridge.GetSessionKwSkills(SessionKey)!.Value;
        JsonElement[] firstRows = [.. first.GetProperty("rows").EnumerateArray()];
        Assert.Equal(25, firstRows.Length);
        for (int index = 0; index < firstRows.Length; index++)
        {
            Assert.Equal($"skill-{29 - index:D2}", firstRows[index].GetProperty("skill").GetString());
        }

        Assert.Equal(30, first.GetProperty("total").GetInt32());
        Assert.Equal(1, first.GetProperty("page").GetInt32());
        Assert.Equal(0, first.GetProperty("offset").GetInt32());
        Assert.Equal("desc", first.GetProperty("order").GetString());

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionKwSkillLoads)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionHeadersByKeys)],
            store.SessionProjectionReadLog
                .Where(static read => read is not ("ListSessionProjections" or "ListHarnessRollups"))
                .ToList());
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));

        JsonElement second = bridge.GetSessionKwSkills(SessionKey, page: 2)!.Value;
        JsonElement[] secondRows = [.. second.GetProperty("rows").EnumerateArray()];
        Assert.Equal(5, secondRows.Length);
        Assert.Equal("skill-04", secondRows[0].GetProperty("skill").GetString());
        Assert.Equal(30, second.GetProperty("total").GetInt32());
        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.Equal(25, second.GetProperty("offset").GetInt32());
        Assert.Equal("desc", second.GetProperty("order").GetString());

        JsonElement asc = bridge.GetSessionKwSkills(SessionKey, page: 1, order: "asc")!.Value;
        JsonElement[] ascRows = [.. asc.GetProperty("rows").EnumerateArray()];
        Assert.Equal(25, ascRows.Length);
        Assert.Equal("skill-00", ascRows[0].GetProperty("skill").GetString());
        Assert.Equal("skill-24", ascRows[^1].GetProperty("skill").GetString());
        Assert.Equal("asc", asc.GetProperty("order").GetString());
    }

    /// <summary>Pages support limits 50 and 100 in both sort orders without materializing content.</summary>
    [Fact]
    public void PagesSupportLimits50And100()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        List<CollectedRecord> records = [];
        for (int index = 0; index < 120; index++)
        {
            records.Add(LoadRecord(
                $"span-load-{index:D3}",
                index,
                Call($"call-{index:D3}", "load_skill", "kw-docs", "load_skill", $"skill: skill-{index:D3}\n\nGuidance.")));
        }

        WriteSession(store, SessionKey, records);
        ProjectWithAgent(store, pricing, SessionKey, AgentName);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();
        store.ClearReadLog();

        JsonElement desc50Page1 = bridge.GetSessionKwSkills(SessionKey, page: 1, limit: 50, order: "desc")!.Value;
        JsonElement[] desc50Page1Rows = [.. desc50Page1.GetProperty("rows").EnumerateArray()];
        Assert.Equal(50, desc50Page1Rows.Length);
        Assert.Equal("skill-119", desc50Page1Rows[0].GetProperty("skill").GetString());
        Assert.Equal("skill-070", desc50Page1Rows[^1].GetProperty("skill").GetString());
        Assert.Equal(120, desc50Page1.GetProperty("total").GetInt32());
        Assert.Equal(50, desc50Page1.GetProperty("limit").GetInt32());
        Assert.Equal(0, desc50Page1.GetProperty("offset").GetInt32());
        Assert.Equal(1, desc50Page1.GetProperty("page").GetInt32());
        Assert.Equal("desc", desc50Page1.GetProperty("order").GetString());

        JsonElement desc50Page3 = bridge.GetSessionKwSkills(SessionKey, page: 3, limit: 50, order: "desc")!.Value;
        JsonElement[] desc50Page3Rows = [.. desc50Page3.GetProperty("rows").EnumerateArray()];
        Assert.Equal(20, desc50Page3Rows.Length);
        Assert.Equal("skill-019", desc50Page3Rows[0].GetProperty("skill").GetString());
        Assert.Equal("skill-000", desc50Page3Rows[^1].GetProperty("skill").GetString());
        Assert.Equal(120, desc50Page3.GetProperty("total").GetInt32());
        Assert.Equal(50, desc50Page3.GetProperty("limit").GetInt32());
        Assert.Equal(100, desc50Page3.GetProperty("offset").GetInt32());
        Assert.Equal(3, desc50Page3.GetProperty("page").GetInt32());

        JsonElement desc100Page1 = bridge.GetSessionKwSkills(SessionKey, page: 1, limit: 100, order: "desc")!.Value;
        JsonElement[] desc100Page1Rows = [.. desc100Page1.GetProperty("rows").EnumerateArray()];
        Assert.Equal(100, desc100Page1Rows.Length);
        Assert.Equal("skill-119", desc100Page1Rows[0].GetProperty("skill").GetString());
        Assert.Equal("skill-020", desc100Page1Rows[^1].GetProperty("skill").GetString());
        Assert.Equal(120, desc100Page1.GetProperty("total").GetInt32());
        Assert.Equal(100, desc100Page1.GetProperty("limit").GetInt32());
        Assert.Equal(0, desc100Page1.GetProperty("offset").GetInt32());
        Assert.Equal(1, desc100Page1.GetProperty("page").GetInt32());

        JsonElement asc100Page2 = bridge.GetSessionKwSkills(SessionKey, page: 2, limit: 100, order: "asc")!.Value;
        JsonElement[] asc100Page2Rows = [.. asc100Page2.GetProperty("rows").EnumerateArray()];
        Assert.Equal(20, asc100Page2Rows.Length);
        Assert.Equal("skill-100", asc100Page2Rows[0].GetProperty("skill").GetString());
        Assert.Equal("skill-119", asc100Page2Rows[^1].GetProperty("skill").GetString());
        Assert.Equal(120, asc100Page2.GetProperty("total").GetInt32());
        Assert.Equal(100, asc100Page2.GetProperty("limit").GetInt32());
        Assert.Equal(100, asc100Page2.GetProperty("offset").GetInt32());
        Assert.Equal(2, asc100Page2.GetProperty("page").GetInt32());
        Assert.Equal("asc", asc100Page2.GetProperty("order").GetString());

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.All(
            store.ReadLog.Where(static read => read != "CountRecords"),
            static read => Assert.Equal(nameof(InMemoryRelationalStore.SessionKwSkillLoads), read));
        Assert.Equal(4, store.ReadLog.Count(static read => read == nameof(InMemoryRelationalStore.SessionKwSkillLoads)));
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.GetSessionProjection), store.SessionProjectionReadLog);
    }

    /// <summary>An unknown session answers no page, so the route answers 404.</summary>
    [Fact]
    public void AnUnknownSessionIs404()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        Assert.Null(bridge.GetSessionKwSkills("sess-absent"));
    }

    /// <summary>
    /// A KW load is not a harness skill invocation: the harness skills section still answers the
    /// session as having invoked no skills.
    /// </summary>
    [Fact]
    public void AKwSkillLoadIsNotAHarnessSkillInvocation()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, SessionKey, [LoadRecord("span-load-1", 0, Call("call-1", "load_skill", "kw-docs", "load_skill", "skill: csharp-dev\n\nGuidance."))]);
        ProjectWithAgent(store, pricing, SessionKey, AgentName);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        JsonElement kwPage = bridge.GetSessionKwSkills(SessionKey)!.Value;
        JsonElement skillsSection = bridge.GetSessionSkills(SessionKey)!.Value;

        Assert.Equal(1, kwPage.GetProperty("total").GetInt32());
        Assert.Equal(0, skillsSection.GetProperty("total").GetInt32());
        Assert.Empty(skillsSection.GetProperty("rows").EnumerateArray().ToList());
    }

    /// <summary>Builds a tool call whose input is never seeded (the store refuses JSON input) and whose reply is the given plain text.</summary>
    private static CollectedToolCall Call(string callId, string toolName, string? serverName, string? serverToolName, string? reply) => new(
        CallId: callId,
        ToolName: toolName,
        McpServerName: serverName,
        McpToolName: serverToolName,
        Input: null,
        Result: reply is null ? null : Content(reply),
        Succeeded: true,
        Error: null,
        ExitCode: null);

    private static CollectedToolContent Content(string text) =>
        new(text, Encoding.UTF8.GetByteCount(text), text.Length, null, null);

    private static CollectedRecord LoadRecord(string spanId, int minuteOffset, CollectedToolCall tool) => new(
        RecordId: spanId,
        HarnessId: "claude-code",
        Op: RecordOp.LlmInvoke,
        Kind: RecordKind.Client,
        Name: "tool.call",
        StartedAt: TurnBase.AddMinutes(minuteOffset),
        Status: RecordStatus.Ok,
        SessionKey: SessionKey,
        ParentRecordId: null,
        TraceId: null,
        Model: "claude-sonnet-4-6",
        EndedAt: null,
        ReportedDurationMs: 1_000,
        Tokens: [],
        ReportedCost: null,
        Parts: [],
        Tool: tool,
        Agent: null,
        SkillsInvoked: [],
        AgentInstanceId: null,
        ToolDefinitions: [],
        Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
        ReportedInputTokens: null,
        ReportedOutputTokens: null);

    /// <summary>Projects the seeded session and pins its agent name, which the projector derives from span attributes.</summary>
    private static void ProjectWithAgent(InMemoryRelationalStore store, FakePricingRepository pricing, string sessionKey, string agentName)
    {
        _ = CreateProjection(store, pricing).ProjectAll();
        StoredSessionProjection existing = store.GetSessionProjection(sessionKey)
            ?? throw new InvalidOperationException($"No projection for {sessionKey}.");
        ((IProjectionStore)store).ReplaceSessionProjection(existing with { AgentName = agentName });
    }

    private static KyberBridgeEngine CreateBridge(InMemoryRelationalStore store, FakePricingRepository pricing) =>
        new(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing),
            clock: () => TurnBase.AddHours(2),
            instanceId: "test-instance");

    private static CanonProjectionService CreateProjection(InMemoryRelationalStore store, FakePricingRepository pricing) =>
        new(
            store,
            store,
            store,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing));

    private static void WriteSession(InMemoryRelationalStore store, string sessionKey, IReadOnlyList<CollectedRecord> records)
    {
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: "claude-code",
                    NativeSessionId: sessionKey,
                    Workspace: null,
                    Title: "kw loads session",
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));
    }
}
