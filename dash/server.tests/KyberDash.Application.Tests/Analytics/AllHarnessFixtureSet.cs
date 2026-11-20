using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Application.Tests.Analytics;

/// <summary>
/// Synthetic multi-harness fixture set loading one representative <see cref="CollectedBatch"/>
/// per evidenced mapper into <see cref="InMemoryRelationalStore"/>.
/// </summary>
/// <remarks>
/// Each batch mirrors the DTO shapes its mapper emits: Codex <c>local_shell_call</c> and
/// <c>codex.tool_result</c> tool records, Copilot CLI sub-agent <c>agent.invoke</c> records with
/// <c>session_model_usage</c> snapshots, Antigravity transcript <c>tool.invoke</c> records and the
/// <c>antigravity-cli</c> <c>cache/antigravity-statusline.jsonl</c> source, OTLP <c>llm.invoke</c>
/// records (Copilot, Antigravity, OpenCode, Droid, Pi), and file-reader <c>llm.invoke</c> records
/// (Claude, Copilot VS Code, Cursor, Droid, Pi, Kilo, CommandCode, Devin, Warp, Kimi Code,
/// Roo Code, Cline). Alias harnesses enter through
/// <see cref="CanonPricing.NormalizeHarnessName"/> folding, and <c>zcode</c> is represented by a
/// <see cref="HarnessSourceRegistry.UnevidencedSourceFormatCode"/> ingest problem.
/// </remarks>
public static class AllHarnessFixtureSet
{
    /// <summary>Alias harness identifier folding onto <c>claude-code</c>.</summary>
    public const string ClaudeDesktopAlias = "claude-desktop";

    /// <summary>Alias harness identifier folding onto <c>cursor</c>.</summary>
    public const string CursorAgentAlias = "cursor-agent";

    /// <summary>Unevidenced harness identifier emitting <c>SOURCE_FORMAT_UNEVIDENCED</c>.</summary>
    public const string ZcodeHarnessId = "zcode";

    /// <summary>Locator suffix for the <c>antigravity-cli</c> statusline source.</summary>
    public const string AntigravityStatusLineLocator = "cache/antigravity-statusline.jsonl";

    /// <summary>Span ID of the numeric legacy-breaking tool result (<c>42</c> as text).</summary>
    public const string NumericToolResultSpanId = "rec-all-copilot-vscode-tool-1";

    /// <summary>Span ID of the boolean legacy-breaking content part (<c>true</c> as text).</summary>
    public const string BooleanContentSpanId = "rec-all-kilo-legacy-llm-1";

    /// <summary>Span ID of the mixed numeric/boolean JSON content part stored as text.</summary>
    public const string MixedJsonContentSpanId = "rec-all-cursor-llm-1";

    private static readonly DateTimeOffset BaseUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every evidenced recoverable harness ID: local-file harnesses plus the folded aliases that
    /// resolve onto them. Excludes OTLP-only (<c>opencode</c>, <c>copilot</c>,
    /// <c>copilot-agent</c>), classified-only (<c>claude-unclassified</c>, <c>codex-desktop</c>),
    /// unevidenced (<c>zcode</c>), and BL-3 exclusions (<c>windsurf</c>, <c>aider</c>).
    /// </summary>
    public static IReadOnlyList<string> EvidencedRecoverableHarnessIds { get; } =
    [
        "antigravity",
        "antigravity-cli",
        "claude-code",
        "cline",
        "cline-cli",
        "codex-cli",
        "commandcode",
        "copilot-cli",
        "copilot-vscode",
        "cursor",
        "devin",
        "droid",
        "kilo-shared-runtime",
        "kilo-vscode-legacy",
        "kimi-code",
        "pi",
        "roo-code",
        "warp",
    ];

    /// <summary>Builds a fresh <see cref="InMemoryRelationalStore"/> loaded with every fixture batch.</summary>
    /// <returns>Store containing all harness sessions, records, and the <c>zcode</c> problem.</returns>
    public static InMemoryRelationalStore CreateStore()
    {
        InMemoryRelationalStore store = new();
        foreach (CollectedBatch batch in CreateBatches())
        {
            store.WriteBatch(batch);
        }

        store.WriteProblem(ZcodeUnevidencedProblem());
        return store;
    }

    /// <summary>Builds every synthetic fixture batch without touching a store.</summary>
    /// <returns>One batch per evidenced mapper plus the OTLP-only mapper batches.</returns>
    public static IReadOnlyList<CollectedBatch> CreateBatches() =>
    [
        ClaudeBatch(),
        CodexBatch(),
        CopilotCliBatch(),
        CopilotOtlpBatch(),
        CopilotAgentOtlpBatch(),
        AntigravityBatch(),
        AntigravityCliStatuslineBatch(),
        OpenCodeBatch(),
        DroidBatch(),
        PiBatch(),
        CopilotVscodeBatch(),
        CursorBatch(),
        KiloBatch(),
        KiloLegacyBatch(),
        CommandCodeBatch(),
        DevinBatch(),
        WarpBatch(),
        KimiCodeBatch(),
        RooCodeBatch(),
        ClineBatch(),
        ClineCliBatch(),
    ];

    /// <summary>Builds the <c>zcode</c> unevidenced-format ingest problem.</summary>
    /// <returns>Problem carrying <see cref="HarnessSourceRegistry.UnevidencedSourceFormatCode"/>.</returns>
    public static CollectedProblem ZcodeUnevidencedProblem() => new(
        ProblemKey: "all-harness-zcode-1",
        Severity: "warning",
        Code: HarnessSourceRegistry.UnevidencedSourceFormatCode,
        Message: "zcode local source format is unevidenced; no reader is registered.",
        HarnessId: ZcodeHarnessId,
        TimestampUtc: "2026-11-19T12:00:00.000Z");

    private static CollectedBatch ClaudeBatch()
    {
        string harnessId = CanonPricing.NormalizeHarnessName(ClaudeDesktopAlias);
        const string sessionKey = "sess-all-claude";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, ".claude/projects/demo/transcript.jsonl"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "claude-desktop:project/demo/session-1",
                    Workspace: new CollectedWorkspace("/tmp/demo", "demo", "main"),
                    Title: "All-harness Claude session",
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: "rec-all-claude-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "claude.turn",
                    StartedAt: BaseUtc,
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    Model: "claude-sonnet-4-6",
                    EndedAt: BaseUtc.AddSeconds(12),
                    ReportedDurationMs: 12000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 1200),
                        new CollectedTokenCount(TokenClass.CacheRead, 400),
                        new CollectedTokenCount(TokenClass.Output, 320),
                    ],
                    ReportedCost: new CollectedCost(0.00846, "USD"),
                    Parts:
                    [
                        TextPart(0, ContentBucket.SystemPrompt, "Be concise."),
                        WithheldPart(1, ContentBucket.ConversationHistory, utf8Bytes: 480, characters: 470, measuredTokens: 118),
                    ],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, 200000)),
            ],
            Problems: []);
    }

    private static CollectedBatch CodexBatch()
    {
        const string harnessId = "codex-cli";
        const string sessionKey = "sess-all-codex";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, ".codex/sessions/2026/11/19/rollout.jsonl"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "codex-cli:rollout-1",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: "rec-all-codex-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "codex.turn",
                    StartedAt: BaseUtc.AddMinutes(1),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    Model: "gpt-5.2",
                    EndedAt: BaseUtc.AddMinutes(1).AddSeconds(9),
                    ReportedDurationMs: 9000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 900),
                        new CollectedTokenCount(TokenClass.Output, 210),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserPrompt, "Summarize the failing test output.")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "rec-all-codex-tool-1",
                    HarnessId: harnessId,
                    Op: RecordOp.ToolInvoke,
                    Kind: RecordKind.Client,
                    Name: "local_shell_call",
                    StartedAt: BaseUtc.AddMinutes(2),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    Model: null,
                    EndedAt: BaseUtc.AddMinutes(2).AddSeconds(2),
                    ReportedDurationMs: 2000,
                    Tokens: [],
                    ReportedCost: null,
                    Parts: [],
                    Tool: new CollectedToolCall(
                        CallId: "call-shell-1",
                        ToolName: "local_shell",
                        McpServerName: null,
                        McpToolName: null,
                        Input: new CollectedToolContent(null, 44, 44, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", null),
                        Result: new CollectedToolContent("on branch main", 14, 14, null, null),
                        Succeeded: true,
                        Error: null,
                        ExitCode: 0),
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "rec-all-codex-tool-result-1",
                    HarnessId: harnessId,
                    Op: RecordOp.ToolInvoke,
                    Kind: RecordKind.Internal,
                    Name: "codex.tool_result",
                    StartedAt: BaseUtc.AddMinutes(3),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    Model: null,
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens: [],
                    ReportedCost: null,
                    Parts: [],
                    Tool: new CollectedToolCall(
                        CallId: "call-shell-1",
                        ToolName: "local_shell",
                        McpServerName: null,
                        McpToolName: null,
                        Input: null,
                        Result: new CollectedToolContent(null, 44, 44, null, null),
                        Succeeded: true,
                        Error: null,
                        ExitCode: 0),
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);
    }

    private static CollectedBatch CopilotCliBatch()
    {
        const string harnessId = "copilot-cli";
        const string sessionKey = "sess-all-copilot-cli";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, ".copilot/session-state/events.jsonl"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "copilot-cli:session-1",
                    Workspace: new CollectedWorkspace("/tmp/repo", "demo-repo", "feature/x"),
                    Title: "All-harness Copilot CLI session",
                    ReportedUsage:
                    [
                        new CollectedModelUsage(0, "gpt-5", TokenClass.Input, 1500),
                        new CollectedModelUsage(0, "gpt-5", TokenClass.Output, 260),
                    ]),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: "rec-all-copilot-cli-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "copilot.chat",
                    StartedAt: BaseUtc.AddMinutes(4),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "cccccccccccccccccccccccccccccccc",
                    Model: "gpt-5",
                    EndedAt: BaseUtc.AddMinutes(4).AddSeconds(7),
                    ReportedDurationMs: 7000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 1500),
                        new CollectedTokenCount(TokenClass.CacheRead, 600),
                        new CollectedTokenCount(TokenClass.Output, 260),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserPrompt, "Refactor this method.")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "rec-all-copilot-cli-agent-1",
                    HarnessId: harnessId,
                    Op: RecordOp.AgentInvoke,
                    Kind: RecordKind.Client,
                    Name: "subagent.completed",
                    StartedAt: BaseUtc.AddMinutes(5),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "cccccccccccccccccccccccccccccccc",
                    Model: null,
                    EndedAt: BaseUtc.AddMinutes(5).AddSeconds(30),
                    ReportedDurationMs: 30000,
                    Tokens: [],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: new CollectedAgentInvocation("researcher", "tool-call-7", true, null),
                    SkillsInvoked: [],
                    AgentInstanceId: "agent-7",
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "rec-all-copilot-cli-tool-1",
                    HarnessId: harnessId,
                    Op: RecordOp.ToolInvoke,
                    Kind: RecordKind.Client,
                    Name: "copilot.tool",
                    StartedAt: BaseUtc.AddMinutes(6),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "cccccccccccccccccccccccccccccccc",
                    Model: null,
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens: [],
                    ReportedCost: null,
                    Parts: [],
                    Tool: new CollectedToolCall(
                        CallId: "tool-call-7",
                        ToolName: "read_file",
                        McpServerName: null,
                        McpToolName: null,
                        Input: new CollectedToolContent(null, 22, 22, null, null),
                        Result: new CollectedToolContent("export const x = 1;", 19, 19, null, null),
                        Succeeded: true,
                        Error: null,
                        ExitCode: null),
                    Agent: null,
                    SkillsInvoked: ["debugging"],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);
    }

    private static CollectedBatch CopilotOtlpBatch()
    {
        const string harnessId = "copilot";
        const string sessionKey = "sess-all-copilot";
        return OtlpBatch(
            harnessId,
            sessionKey,
            "copilot-host",
            [
                new CollectedRecord(
                    RecordId: "rec-all-copilot-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "copilot.chat",
                    StartedAt: BaseUtc.AddMinutes(7),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "dddddddddddddddddddddddddddddddd",
                    Model: "gpt-5",
                    EndedAt: BaseUtc.AddMinutes(7).AddSeconds(5),
                    ReportedDurationMs: 5000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 1000),
                        new CollectedTokenCount(TokenClass.CacheRead, 600),
                        new CollectedTokenCount(TokenClass.CacheWrite, 100),
                        new CollectedTokenCount(TokenClass.Output, 200),
                    ],
                    ReportedCost: null,
                    Parts:
                    [
                        TextPart(0, ContentBucket.SystemPrompt, "Be concise."),
                        WithheldPart(1, ContentBucket.ConversationHistory, utf8Bytes: 320, characters: 315, measuredTokens: 80),
                    ],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ]);
    }

    private static CollectedBatch CopilotAgentOtlpBatch()
    {
        const string harnessId = "copilot-agent";
        const string sessionKey = "sess-all-copilot-agent";
        return OtlpBatch(
            harnessId,
            sessionKey,
            "copilot-agent-host",
            [
                new CollectedRecord(
                    RecordId: "rec-all-copilot-agent-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "copilot-agent.run",
                    StartedAt: BaseUtc.AddMinutes(8),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                    Model: "gpt-5",
                    EndedAt: BaseUtc.AddMinutes(8).AddSeconds(6),
                    ReportedDurationMs: 6000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 700),
                        new CollectedTokenCount(TokenClass.Output, 140),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserPrompt, "Run the migration.")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ]);
    }

    private static CollectedBatch AntigravityBatch()
    {
        const string harnessId = "antigravity";
        const string sessionKey = "sess-all-antigravity";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, ".gemini/antigravity/transcripts/demo.jsonl"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "antigravity:conversation-1",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: "rec-all-antigravity-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Internal,
                    Name: "antigravity.planner",
                    StartedAt: BaseUtc.AddMinutes(9),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "ffffffffffffffffffffffffffffffff",
                    Model: "gemini-3-pro",
                    EndedAt: BaseUtc.AddMinutes(9).AddSeconds(11),
                    ReportedDurationMs: 11000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 2100),
                        new CollectedTokenCount(TokenClass.Output, 380),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserPrompt, "Plan the refactor.")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "rec-all-antigravity-tool-1",
                    HarnessId: harnessId,
                    Op: RecordOp.ToolInvoke,
                    Kind: RecordKind.Client,
                    Name: "PLANNER_RESPONSE.tool_calls",
                    StartedAt: BaseUtc.AddMinutes(10),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "ffffffffffffffffffffffffffffffff",
                    Model: null,
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens: [],
                    ReportedCost: null,
                    Parts: [],
                    Tool: new CollectedToolCall(
                        CallId: "ag-tool-1",
                        ToolName: "read_file",
                        McpServerName: null,
                        McpToolName: null,
                        Input: new CollectedToolContent(null, 21, 21, null, null),
                        Result: new CollectedToolContent("# Demo", 6, 6, null, null),
                        Succeeded: true,
                        Error: null,
                        ExitCode: null),
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);
    }

    private static CollectedBatch AntigravityCliStatuslineBatch()
    {
        const string harnessId = "antigravity-cli";
        const string sessionKey = "sess-all-antigravity-cli";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, AntigravityStatusLineLocator),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "antigravity-cli:conversation-9",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: "rec-all-antigravity-cli-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "antigravity-cli.status",
                    StartedAt: BaseUtc.AddMinutes(11),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "10101010101010101010101010101010",
                    Model: "gemini-3-flash",
                    EndedAt: BaseUtc.AddMinutes(11).AddSeconds(4),
                    ReportedDurationMs: 4000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 320),
                        new CollectedTokenCount(TokenClass.Output, 90),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserPrompt, "Status snapshot.")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);
    }

    private static CollectedBatch OpenCodeBatch()
    {
        const string harnessId = "opencode";
        const string sessionKey = "sess-all-opencode";
        return OtlpBatch(
            harnessId,
            sessionKey,
            "opencode-host",
            [
                new CollectedRecord(
                    RecordId: "rec-all-opencode-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "opencode.turn",
                    StartedAt: BaseUtc.AddMinutes(12),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "20202020202020202020202020202020",
                    Model: "claude-sonnet-4-6",
                    EndedAt: BaseUtc.AddMinutes(12).AddSeconds(8),
                    ReportedDurationMs: 8000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 1100),
                        new CollectedTokenCount(TokenClass.Output, 240),
                    ],
                    ReportedCost: new CollectedCost(0.004, "USD"),
                    Parts: [TextPart(0, ContentBucket.UserPrompt, "Explain this diff.")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ]);
    }

    private static CollectedBatch DroidBatch()
    {
        const string harnessId = "droid";
        const string sessionKey = "sess-all-droid";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, ".factory/sessions/demo/session.jsonl"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "droid:session-1",
                    Workspace: null,
                    Title: "All-harness Droid session",
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: "rec-all-droid-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "droid.turn",
                    StartedAt: BaseUtc.AddMinutes(13),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "30303030303030303030303030303030",
                    Model: "droid-1",
                    EndedAt: BaseUtc.AddMinutes(13).AddSeconds(6),
                    ReportedDurationMs: 6000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 800),
                        new CollectedTokenCount(TokenClass.Output, 190),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserPrompt, "Fix the failing test.")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "rec-all-droid-tool-1",
                    HarnessId: harnessId,
                    Op: RecordOp.ToolInvoke,
                    Kind: RecordKind.Client,
                    Name: "factory.tool",
                    StartedAt: BaseUtc.AddMinutes(14),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "30303030303030303030303030303030",
                    Model: null,
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens: [],
                    ReportedCost: null,
                    Parts: [],
                    Tool: new CollectedToolCall(
                        CallId: "droid-call-1",
                        ToolName: "edit_file",
                        McpServerName: null,
                        McpToolName: null,
                        Input: new CollectedToolContent(null, 16, 16, null, null),
                        Result: new CollectedToolContent("patched", 7, 7, null, null),
                        Succeeded: true,
                        Error: null,
                        ExitCode: null),
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);
    }

    private static CollectedBatch PiBatch()
    {
        const string harnessId = "pi";
        const string sessionKey = "sess-all-pi";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, ".pi/agent/sessions/demo.jsonl"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "pi:session-1",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: "rec-all-pi-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "pi.turn",
                    StartedAt: BaseUtc.AddMinutes(15),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "40404040404040404040404040404040",
                    Model: "pi-model-1",
                    EndedAt: BaseUtc.AddMinutes(15).AddSeconds(5),
                    ReportedDurationMs: 5000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 500),
                        new CollectedTokenCount(TokenClass.CacheRead, 200),
                        new CollectedTokenCount(TokenClass.Output, 120),
                    ],
                    ReportedCost: null,
                    Parts:
                    [
                        TextPart(0, ContentBucket.InstructionContext, "debugging"),
                        TextPart(1, ContentBucket.UserPrompt, "Why is this slow?"),
                    ],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: ["debugging"],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);
    }

    private static CollectedBatch CopilotVscodeBatch()
    {
        const string harnessId = "copilot-vscode";
        const string sessionKey = "sess-all-copilot-vscode";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, "copilot-vscode/chat-session.json"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "copilot-vscode:session-1",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: "rec-all-copilot-vscode-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "copilot-vscode.chat",
                    StartedAt: BaseUtc.AddMinutes(16),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "50505050505050505050505050505050",
                    Model: "gpt-5",
                    EndedAt: BaseUtc.AddMinutes(16).AddSeconds(4),
                    ReportedDurationMs: 4000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 640),
                        new CollectedTokenCount(TokenClass.Output, 150),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserPrompt, "Complete the function.")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: NumericToolResultSpanId,
                    HarnessId: harnessId,
                    Op: RecordOp.ToolInvoke,
                    Kind: RecordKind.Client,
                    Name: "copilot-vscode.tool",
                    StartedAt: BaseUtc.AddMinutes(17),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "50505050505050505050505050505050",
                    Model: null,
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens: [],
                    ReportedCost: null,
                    Parts: [],
                    Tool: new CollectedToolCall(
                        CallId: "vscode-call-1",
                        ToolName: "run_tests",
                        McpServerName: null,
                        McpToolName: null,
                        Input: null,
                        Result: new CollectedToolContent("42", 2, 2, null, null),
                        Succeeded: true,
                        Error: null,
                        ExitCode: 0),
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);
    }

    private static CollectedBatch CursorBatch()
    {
        string harnessId = CanonPricing.NormalizeHarnessName(CursorAgentAlias);
        const string sessionKey = "sess-all-cursor";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, "cursor/composer-data.json"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "cursor-agent:composer-1",
                    Workspace: null,
                    Title: "All-harness Cursor session",
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: MixedJsonContentSpanId,
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "cursor.composer",
                    StartedAt: BaseUtc.AddMinutes(18),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "60606060606060606060606060606060",
                    Model: "composer-1",
                    EndedAt: BaseUtc.AddMinutes(18).AddSeconds(10),
                    ReportedDurationMs: 10000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 1800),
                        new CollectedTokenCount(TokenClass.Output, 420),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserPrompt, "Legacy content_json held count 3 and ok false; stored here as text.")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);
    }

    private static CollectedBatch KiloBatch()
    {
        const string harnessId = "kilo-shared-runtime";
        const string sessionKey = "sess-all-kilo";
        return LlmOnlyBatch(
            harnessId,
            sessionKey,
            "kilo/kilo.db",
            "rec-all-kilo-llm-1",
            "kilo.task",
            19,
            "70707070707070707070707070707070",
            "kilo-model-1",
            "Review the pull request.");
    }

    private static CollectedBatch KiloLegacyBatch()
    {
        const string harnessId = "kilo-vscode-legacy";
        const string sessionKey = "sess-all-kilo-legacy";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, "kilo/tasks/demo/ui_messages.json"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "kilo-vscode-legacy:task-1",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: BooleanContentSpanId,
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "kilo.task",
                    StartedAt: BaseUtc.AddMinutes(20),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "80808080808080808080808080808080",
                    Model: "kilo-model-1",
                    EndedAt: BaseUtc.AddMinutes(20).AddSeconds(6),
                    ReportedDurationMs: 6000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 450),
                        new CollectedTokenCount(TokenClass.Output, 110),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserMessage, "true")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);
    }

    private static CollectedBatch CommandCodeBatch()
    {
        const string harnessId = "commandcode";
        const string sessionKey = "sess-all-commandcode";
        return new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, ".commandcode/projects/demo/session.jsonl"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: "commandcode:session-1",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: "rec-all-commandcode-llm-1",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Internal,
                    Name: "commandcode.turn",
                    StartedAt: BaseUtc.AddMinutes(21),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: "90909090909090909090909090909090",
                    Model: "commandcode-model-1",
                    EndedAt: BaseUtc.AddMinutes(21).AddSeconds(3),
                    ReportedDurationMs: 3000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 380),
                        new CollectedTokenCount(TokenClass.Output, 95),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserPrompt, "Run the command.")],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);
    }

    private static CollectedBatch DevinBatch() =>
        LlmOnlyBatch(
            "devin",
            "sess-all-devin",
            ".local/share/devin/cli/transcripts/demo.json",
            "rec-all-devin-llm-1",
            "devin.session",
            22,
            "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1",
            "devin-model-1",
            "Implement the endpoint.");

    private static CollectedBatch WarpBatch() =>
        LlmOnlyBatch(
            "warp",
            "sess-all-warp",
            "warp/warp.sqlite",
            "rec-all-warp-llm-1",
            "warp.block",
            23,
            "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2",
            "warp-model-1",
            "Explain the error.");

    private static CollectedBatch KimiCodeBatch() =>
        LlmOnlyBatch(
            "kimi-code",
            "sess-all-kimi",
            ".kimi-code/sessions/wire.jsonl",
            "rec-all-kimi-llm-1",
            "kimi.turn",
            24,
            "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3",
            "kimi-model-1",
            "Draft the summary.");

    private static CollectedBatch RooCodeBatch() =>
        LlmOnlyBatch(
            "roo-code",
            "sess-all-roo",
            "roo/tasks/demo/ui_messages.json",
            "rec-all-roo-llm-1",
            "roo.task",
            25,
            "d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4",
            "roo-model-1",
            "Add the test.");

    private static CollectedBatch ClineBatch() =>
        LlmOnlyBatch(
            "cline",
            "sess-all-cline",
            "cline/tasks/demo/ui_messages.json",
            "rec-all-cline-llm-1",
            "cline.task",
            26,
            "e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5",
            "cline-model-1",
            "Update the docs.");

    private static CollectedBatch ClineCliBatch() =>
        LlmOnlyBatch(
            "cline-cli",
            "sess-all-cline-cli",
            ".cline/data/sessions/demo.messages.json",
            "rec-all-cline-cli-llm-1",
            "cline-cli.task",
            27,
            "f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6",
            "cline-model-1",
            "Check the build.");

    private static CollectedBatch OtlpBatch(
        string harnessId,
        string sessionKey,
        string locator,
        IReadOnlyList<CollectedRecord> records) =>
        new(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, locator),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: harnessId + ":session-1",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []);

    private static CollectedBatch LlmOnlyBatch(
        string harnessId,
        string sessionKey,
        string locator,
        string recordId,
        string name,
        int minuteOffset,
        string traceId,
        string model,
        string prompt) =>
        new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, locator),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: harnessId + ":session-1",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: recordId,
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: name,
                    StartedAt: BaseUtc.AddMinutes(minuteOffset),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionKey,
                    ParentRecordId: null,
                    TraceId: traceId,
                    Model: model,
                    EndedAt: BaseUtc.AddMinutes(minuteOffset).AddSeconds(5),
                    ReportedDurationMs: 5000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 300),
                        new CollectedTokenCount(TokenClass.Output, 80),
                    ],
                    ReportedCost: null,
                    Parts: [TextPart(0, ContentBucket.UserPrompt, prompt)],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            Problems: []);

    private static CollectedContentPart TextPart(int position, ContentBucket bucket, string text) =>
        new(
            Position: position,
            Bucket: bucket,
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static CollectedContentPart WithheldPart(
        int position,
        ContentBucket bucket,
        long utf8Bytes,
        long characters,
        long measuredTokens) =>
        new(
            Position: position,
            Bucket: bucket,
            PlainText: null,
            Withheld: new CollectedWithheldText(utf8Bytes, characters, measuredTokens),
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);
}
