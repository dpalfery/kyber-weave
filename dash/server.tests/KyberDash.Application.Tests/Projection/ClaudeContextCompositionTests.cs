using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Application.Tests;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Claude;
using KyberDash.Data.Repositories;
using KyberDash.Data.Tokenization;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Verifies cumulative Claude Code multi-turn context reconstruction, compaction boundary
/// hydration and reset, <c>#agent-</c> subagent linkage and parent exclusion, &gt;64 KiB
/// plain+withheld token conservation, and <see cref="CanonSynth.MeasurabilityFor"/>-backed
/// <c>missingBuckets</c> / <c>residualReasons</c> on <see cref="KyberBridgeEngine.GetSessionCharts"/>.
/// </summary>
public sealed class ClaudeContextCompositionTests : IDisposable
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory tempDir = new();

    /// <summary>Releases any temporary SQLite directories created by the test.</summary>
    public void Dispose()
    {
        tempDir.Dispose();
    }

    /// <summary>
    /// <see cref="CanonRecordHydration.Hydrate"/> surfaces <c>AgentInstanceId == "claude:compact_boundary"</c>
    /// onto <c>CanonicalRecord.Raw["claude.compact_boundary"]</c> (<see langword="true"/>) without emitting
    /// synthetic <see cref="ContentPart"/> text, across both <see cref="InMemoryRelationalStore"/> and
    /// temp-file <see cref="SqliteRelationalStore"/> round-trips, and resets cumulative projection at the boundary.
    /// </summary>
    [Fact]
    public void Hydrate_SurfacesCompactBoundaryMarkerAndResetsProjection_InMemoryAndSqlite()
    {
        CollectedRecord turn0 = Turn(
            "turn-0",
            sessionKey: "sess-compact",
            minuteOffset: 0,
            inputTokens: 400,
            outputTokens: 50,
            parts:
            [
                TextPart(0, ContentBucket.ConversationHistory, "Pre-compaction alpha beta gamma", role: "user"),
                TextPart(1, ContentBucket.ConversationHistory, "Pre-compaction assistant reply delta", role: "assistant"),
            ]);
        CollectedRecord turn1 = Turn(
            "turn-1",
            sessionKey: "sess-compact",
            minuteOffset: 1,
            inputTokens: 700,
            outputTokens: 40,
            parts:
            [
                TextPart(0, ContentBucket.ConversationHistory, "More pre-compaction context epsilon zeta", role: "user"),
            ]);
        CollectedRecord turn2Boundary = Turn(
            "turn-2",
            sessionKey: "sess-compact",
            minuteOffset: 2,
            inputTokens: 250,
            outputTokens: 30,
            agentInstanceId: "claude:compact_boundary",
            parts:
            [
                TextPart(0, ContentBucket.ConversationHistory, "Post-compaction summary only", role: "user"),
            ]);

        InMemoryRelationalStore memoryStore = new();
        WriteSession(memoryStore, "sess-compact", [turn0, turn1, turn2Boundary]);

        string dbPath = tempDir.Combine("compact-roundtrip.db");
        SqliteRelationalStore sqliteStore = new(dbPath, new SqliteCircuitBreaker());
        WriteSession(sqliteStore, "sess-compact", [turn0, turn1, turn2Boundary]);

        AssertCompactionHydrationAndReset(memoryStore, memoryStore, memoryStore);
        AssertCompactionHydrationAndReset(sqliteStore, sqliteStore, sqliteStore);
    }

    /// <summary>
    /// Multi-turn Claude sessions accumulate prompt-eligible buckets (<c>system_prompt</c>,
    /// <c>instruction_context</c>, <c>conversation_history</c>, <c>tool_result_content</c>) across
    /// turns &lt; k in a linear O(T) pass by reducing each turn's own parts once without quadratically
    /// re-tokenizing prior raw text.
    /// </summary>
    [Fact]
    public void ProjectIncremental_AccumulatesClaudePromptBucketsLinearlyWithoutRetokenizingPriorTurns()
    {
        InMemoryRelationalStore store = new();
        CountingFakeTokenizer tokenizer = new();

        CollectedRecord turn0 = Turn(
            "turn-0",
            sessionKey: "sess-linear",
            minuteOffset: 0,
            inputTokens: 1_000,
            outputTokens: 100,
            parts:
            [
                TextPart(0, ContentBucket.SystemPrompt, "sys one two", role: "system"),
                TextPart(1, ContentBucket.InstructionContext, "inst three", role: "user"),
                TextPart(2, ContentBucket.ConversationHistory, "user zero four five", role: "user"),
                TextPart(3, ContentBucket.ConversationHistory, "assistant zero six seven eight", role: "assistant"),
                TextPart(4, ContentBucket.ToolResultContent, "result zero nine", role: "user"),
            ]);
        CollectedRecord turn1 = Turn(
            "turn-1",
            sessionKey: "sess-linear",
            minuteOffset: 1,
            inputTokens: 2_000,
            outputTokens: 120,
            parts:
            [
                TextPart(0, ContentBucket.ConversationHistory, "user one ten <system-reminder>reminder eleven twelve</system-reminder>", role: "user"),
                TextPart(1, ContentBucket.ConversationHistory, "assistant one thirteen fourteen", role: "assistant"),
                TextPart(2, ContentBucket.ToolResultContent, "result one fifteen", role: "user"),
            ]);
        CollectedRecord turn2 = Turn(
            "turn-2",
            sessionKey: "sess-linear",
            minuteOffset: 2,
            inputTokens: 3_000,
            outputTokens: 140,
            parts:
            [
                TextPart(0, ContentBucket.ConversationHistory, "user two sixteen", role: "user"),
                TextPart(1, ContentBucket.ConversationHistory, "assistant two seventeen eighteen nineteen", role: "assistant"),
            ]);

        WriteSession(store, "sess-linear", [turn0, turn1, turn2]);
        CanonSessionProjectorEngine engine = new(store, store, store, new FakePricingRepository(), tokenizer);
        ProjectionPassReport report = engine.ProjectIncremental();

        Assert.Equal(1, report.Built);
        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("sess-linear")!;
        Assert.NotNull(projection.Context);
        Assert.Equal(3, projection.Context!.Turns.Count);

        // Turn 0: own system (3) + own instruction (2) + own user history (4) + own tool result (3); assistant (5) excluded.
        Assert.Equal(3L, BucketTokens(projection.Context.Turns[0], "system_prompt"));
        Assert.Equal(2L, BucketTokens(projection.Context.Turns[0], "instruction_context"));
        Assert.Equal(4L, BucketTokens(projection.Context.Turns[0], "conversation_history"));
        Assert.Equal(3L, BucketTokens(projection.Context.Turns[0], "tool_result_content"));

        // Turn 1: carried system (3) + carried inst (2) + stripped reminder (3) = 5 +
        //         history = turn0 user (4) + turn0 assistant (5) + turn1 user remainder (3) = 12 +
        //         tool_result = turn0 (3) + turn1 (3) = 6; turn1 assistant (4) excluded.
        Assert.Equal(3L, BucketTokens(projection.Context.Turns[1], "system_prompt"));
        Assert.Equal(5L, BucketTokens(projection.Context.Turns[1], "instruction_context"));
        Assert.Equal(12L, BucketTokens(projection.Context.Turns[1], "conversation_history"));
        Assert.Equal(6L, BucketTokens(projection.Context.Turns[1], "tool_result_content"));
        Assert.True(BucketTokens(projection.Context.Turns[1], "conversation_history") > 3L);

        // Turn 2: carried system (3) + carried inst (5) +
        //         history = turn1 history (12) + turn1 assistant (4) + turn2 user (3) = 19 +
        //         tool_result = 6; turn2 assistant (5) excluded.
        Assert.Equal(3L, BucketTokens(projection.Context.Turns[2], "system_prompt"));
        Assert.Equal(5L, BucketTokens(projection.Context.Turns[2], "instruction_context"));
        Assert.Equal(19L, BucketTokens(projection.Context.Turns[2], "conversation_history"));
        Assert.Equal(6L, BucketTokens(projection.Context.Turns[2], "tool_result_content"));

        // Linear O(T) guarantee: each turn's raw text is tokenized at most once, never re-tokenized on later turns.
        Assert.Equal(1, tokenizer.CountCallsFor("sys one two"));
        Assert.Equal(1, tokenizer.CountCallsFor("user zero four five"));
        Assert.Equal(1, tokenizer.CountCallsFor("assistant zero six seven eight"));
        Assert.Equal(1, tokenizer.CountCallsFor("result zero nine"));
    }

    /// <summary>
    /// Turn <c>k</c>'s own assistant output, thinking, and <c>tool_use</c> parts are excluded from
    /// Turn <c>k</c>'s own input buckets, then carried into Turn <c>k + 1</c>'s conversation history.
    /// </summary>
    [Fact]
    public void ProjectIncremental_ExcludesTurnKOwnAssistantOutputAndThinkingFromTurnKInputBuckets()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"user\",\"sessionId\":\"sess-own\",\"timestamp\":\"2026-11-19T12:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"alpha beta\"}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-own\",\"timestamp\":\"2026-11-19T12:00:01.000Z\",\"message\":{\"id\":\"msg-1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"think one two three\"},{\"type\":\"text\",\"text\":\"reply four five\"},{\"type\":\"tool_use\",\"id\":\"tc-1\",\"name\":\"read_file\",\"input\":{\"path\":\"a.cs\"}}],\"usage\":{\"input_tokens\":100,\"output_tokens\":40}}}",
            "{\"type\":\"user\",\"sessionId\":\"sess-own\",\"timestamp\":\"2026-11-19T12:00:02.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tc-1\",\"content\":\"file body\"}]}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-own\",\"timestamp\":\"2026-11-19T12:00:03.000Z\",\"message\":{\"id\":\"msg-2\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"second thought six seven\"},{\"type\":\"text\",\"text\":\"final eight nine ten\"}],\"usage\":{\"input_tokens\":200,\"output_tokens\":25}}}",
        ]);

        CollectedBatch batch = new ClaudeTranscriptMapper().Map(CreateClaudeUnit("/tmp/.claude/projects/repo/sess-own.jsonl", jsonl), TurnBase);
        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        FakeTextTokenizer tokenizer = new();
        CanonSessionProjectorEngine engine = new(store, store, store, new FakePricingRepository(), tokenizer);
        _ = engine.ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("sess-own")!;
        Assert.NotNull(projection.Context);
        Assert.Equal(2, projection.Context!.Turns.Count);

        long expectedTurn0User = tokenizer.Count("alpha beta", "claude-sonnet-4-5").Count;
        long expectedTurn0Thinking = tokenizer.Count("think one two three", "claude-sonnet-4-5").Count;
        long expectedTurn0Reply = tokenizer.Count("reply four five", "claude-sonnet-4-5").Count;
        string? turn0ToolText = batch.Records[0].Parts.Last(static p => p.Role == "assistant").PlainText;
        long expectedTurn0ToolUse = tokenizer.Count(turn0ToolText!, "claude-sonnet-4-5").Count;

        // Turn 0 has ONLY Turn 0's user prompt, excluding its thinking/reply/tool_use.
        Assert.Equal(expectedTurn0User, BucketTokens(projection.Context.Turns[0], "conversation_history"));

        // Turn 1 accumulates all of Turn 0 (user + thinking + reply + tool_use), excludes Turn 1's own thinking & final text.
        long expectedTurn1History = expectedTurn0User + expectedTurn0Thinking + expectedTurn0Reply + expectedTurn0ToolUse;
        Assert.Equal(expectedTurn1History, BucketTokens(projection.Context.Turns[1], "conversation_history"));
        Assert.Equal(tokenizer.Count("file body", "claude-sonnet-4-5").Count, BucketTokens(projection.Context.Turns[1], "tool_result_content"));
    }

    /// <summary>
    /// <c>${parentSessionId}#agent-{bareId}</c> subagents persist <c>IsSubagent = true</c> and
    /// <c>ParentSessionId = parentSessionId</c> while staying excluded from parent context turns.
    /// </summary>
    [Fact]
    public void ProjectIncremental_LinksAgentKeySubagentsToParentAndExcludesFromParentContext()
    {
        string parentJsonl = string.Join('\n',
        [
            "{\"type\":\"user\",\"sessionId\":\"sess-parent\",\"timestamp\":\"2026-11-19T12:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"Parent prompt one two\"}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-parent\",\"timestamp\":\"2026-11-19T12:00:01.000Z\",\"message\":{\"id\":\"msg-p1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Parent reply\"}],\"usage\":{\"input_tokens\":100,\"output_tokens\":20}}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-parent\",\"isSidechain\":true,\"agentId\":\"sub99\",\"timestamp\":\"2026-11-19T12:00:02.000Z\",\"message\":{\"id\":\"msg-sub1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Subagent secret sidechain work\"}],\"usage\":{\"input_tokens\":500,\"output_tokens\":50}}}",
        ]);
        string nestedSubagentJsonl = string.Join('\n',
        [
            "{\"type\":\"user\",\"timestamp\":\"2026-11-19T12:01:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"Nested subagent prompt\"}}",
            "{\"type\":\"assistant\",\"timestamp\":\"2026-11-19T12:01:01.000Z\",\"message\":{\"id\":\"msg-sub2\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Nested subagent reply\"}],\"usage\":{\"input_tokens\":300,\"output_tokens\":30}}}",
        ]);

        ClaudeTranscriptMapper mapper = new();
        CollectedBatch parentBatch = mapper.Map(CreateClaudeUnit("/tmp/.claude/projects/repo/sess-parent.jsonl", parentJsonl), TurnBase);
        CollectedBatch nestedBatch = mapper.Map(CreateClaudeUnit("/tmp/.claude/projects/repo/sess-parent/subagents/agent-nested7.jsonl", nestedSubagentJsonl), TurnBase);

        InMemoryRelationalStore store = new();
        store.WriteBatch(parentBatch);
        store.WriteBatch(nestedBatch);

        CanonSessionProjectorEngine engine = new(store, store, store, new FakePricingRepository(), new FakeTextTokenizer());
        ProjectionPassReport report = engine.ProjectIncremental();

        Assert.Equal(3, report.Built);

        StoredSessionProjection parent = ((IProjectionStore)store).GetSessionProjection("sess-parent")!;
        Assert.False(parent.IsSubagent);
        Assert.Null(parent.ParentSessionId);
        Assert.NotNull(parent.Context);
        StoredSessionContextTurn parentTurn = Assert.Single(parent.Context!.Turns);
        Assert.Equal(100L, parentTurn.InputTokens);
        Assert.Equal(4L, BucketTokens(parentTurn, "conversation_history"));

        StoredSessionProjection sidechain = ((IProjectionStore)store).GetSessionProjection("sess-parent#agent-sub99")!;
        Assert.True(sidechain.IsSubagent);
        Assert.Equal("sess-parent", sidechain.ParentSessionId);

        StoredSessionProjection nested = ((IProjectionStore)store).GetSessionProjection("sess-parent#agent-nested7")!;
        Assert.True(nested.IsSubagent);
        Assert.Equal("sess-parent", nested.ParentSessionId);
        Assert.NotNull(nested.Context);
        Assert.Equal(3L, BucketTokens(Assert.Single(nested.Context!.Turns), "conversation_history"));

        // Side-agent turns sharing the parent session key are also excluded from parent context turns.
        InMemoryRelationalStore sideAgentStore = new();
        CollectedRecord mainRecord = Turn(
            "main-turn-1",
            sessionKey: "sess-side-parent",
            minuteOffset: 0,
            inputTokens: 100,
            outputTokens: 10,
            parts: [TextPart(0, ContentBucket.ConversationHistory, "Main thread only", role: "user")]);
        CollectedRecord sideTurn = Turn(
            "side-turn-1",
            sessionKey: "sess-side-parent",
            minuteOffset: 1,
            inputTokens: 500,
            outputTokens: 50,
            parts: [TextPart(0, ContentBucket.ConversationHistory, "Background side agent work", role: "user")]) with
        {
            Agent = new CollectedAgentInvocation("backgroundTodoAgent", null, true, null),
        };
        WriteSession(sideAgentStore, "sess-side-parent", [mainRecord, sideTurn]);
        _ = new CanonSessionProjectorEngine(sideAgentStore, sideAgentStore, sideAgentStore, new FakePricingRepository(), new FakeTextTokenizer()).ProjectIncremental();
        StoredSessionProjection sideParent = ((IProjectionStore)sideAgentStore).GetSessionProjection("sess-side-parent")!;
        Assert.NotNull(sideParent.Context);
        Assert.Single(sideParent.Context!.Turns);
        Assert.Equal(3L, BucketTokens(sideParent.Context.Turns[0], "conversation_history"));
    }

    /// <summary>
    /// &gt;64 KiB plain + tail-only <c>Withheld</c> parts conserve tokens accurately without
    /// double-counting the first 64 KiB, and match full-text tokenization when split cleanly.
    /// </summary>
    [Fact]
    public void ProjectIncremental_OversizedPlainAndTailWithheldParts_ConserveTokensWithoutDoublingFirst64KiB()
    {
        string beforeReminder = new('a', 63_000);
        string reminderBody = new('b', 4_000);
        string afterReminder = new('c', 5_000);
        string fullText = $"{beforeReminder}<system-reminder>{reminderBody}</system-reminder>{afterReminder}";
        string expectedTail = $"<system-reminder>{reminderBody}</system-reminder>{afterReminder}";

        long fullTokens = LocalTextTokenizer.Shared.Count(fullText, "claude-sonnet-4-5").Count;
        long prefixTokens = LocalTextTokenizer.Shared.Count(beforeReminder, "claude-sonnet-4-5").Count;
        long tailTokens = LocalTextTokenizer.Shared.Count(expectedTail, "claude-sonnet-4-5").Count;

        string jsonl = string.Join('\n',
        [
            $"{{\"type\":\"user\",\"sessionId\":\"sess-64k\",\"timestamp\":\"2026-11-19T12:00:00.000Z\",\"message\":{{\"role\":\"user\",\"content\":{JsonSerializer.Serialize(fullText)}}}}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-64k\",\"timestamp\":\"2026-11-19T12:00:01.000Z\",\"message\":{\"id\":\"msg-1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"usage\":{\"input_tokens\":50000,\"output_tokens\":10}}}",
            "{\"type\":\"user\",\"sessionId\":\"sess-64k\",\"timestamp\":\"2026-11-19T12:00:02.000Z\",\"message\":{\"role\":\"user\",\"content\":\"follow up\"}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-64k\",\"timestamp\":\"2026-11-19T12:00:03.000Z\",\"message\":{\"id\":\"msg-2\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"usage\":{\"input_tokens\":51000,\"output_tokens\":10}}}",
        ]);

        CollectedBatch batch = new ClaudeTranscriptMapper().Map(CreateClaudeUnit("/tmp/.claude/projects/repo/sess-64k.jsonl", jsonl), TurnBase);
        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        CanonSessionProjectorEngine engine = new(store, store, store, new FakePricingRepository(), LocalTextTokenizer.Shared);
        _ = engine.ProjectIncremental();

        StoredSessionProjection projection = ((IProjectionStore)store).GetSessionProjection("sess-64k")!;
        Assert.NotNull(projection.Context);
        Assert.Equal(2, projection.Context!.Turns.Count);

        long turn0History = BucketTokens(projection.Context.Turns[0], "conversation_history");
        Assert.Equal(prefixTokens + tailTokens, turn0History);
        Assert.Equal(fullTokens, turn0History);
        Assert.True(turn0History < prefixTokens + fullTokens);

        long okTokens = LocalTextTokenizer.Shared.Count("ok", "claude-sonnet-4-5").Count;
        long followUpTokens = LocalTextTokenizer.Shared.Count("follow up", "claude-sonnet-4-5").Count;
        long turn1History = BucketTokens(projection.Context.Turns[1], "conversation_history");
        Assert.Equal(turn0History + okTokens + followUpTokens, turn1History);
    }

    /// <summary>
    /// <see cref="KyberBridgeEngine.GetSessionCharts"/> emits <c>missingBuckets</c> and plain-language
    /// <c>residualReasons</c> derived from <see cref="CanonSynth.MeasurabilityFor"/> for Claude sessions
    /// that omit <c>system_prompt</c> and <c>tool_definitions</c>, and omits them for non-omitted harnesses.
    /// </summary>
    [Fact]
    public void GetSessionCharts_EmitsMissingBucketsAndResidualReasonsForClaude_AndOmitsForNonOmittedHarnesses()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();

        WriteSession(
            store,
            "sess-claude-charts",
            [
                Turn(
                    "c-turn-1",
                    sessionKey: "sess-claude-charts",
                    minuteOffset: 0,
                    inputTokens: 1_500,
                    outputTokens: 50,
                    parts: [TextPart(0, ContentBucket.ConversationHistory, "hello claude", role: "user")]),
            ],
            harnessId: "claude-code");

        WriteSession(
            store,
            "sess-opencode-charts",
            [
                Turn(
                    "o-turn-1",
                    sessionKey: "sess-opencode-charts",
                    minuteOffset: 0,
                    inputTokens: 500,
                    outputTokens: 30,
                    harnessId: "opencode",
                    parts:
                    [
                        TextPart(0, ContentBucket.SystemPrompt, "system rules", role: "system"),
                        TextPart(1, ContentBucket.ConversationHistory, "hello opencode", role: "user"),
                    ],
                    definitions: [new CollectedToolDefinition(0, "read_file", null, 50)]),
            ],
            harnessId: "opencode");

        CanonSessionProjectorEngine projector = new(store, store, store, pricing, new FakeTextTokenizer());
        _ = projector.ProjectIncremental();
        KyberBridgeEngine bridge = CreateBridge(store, pricing, projector);

        JsonElement claudeCharts = bridge.GetSessionCharts("sess-claude-charts")!.Value;
        JsonElement claudeContext = claudeCharts.GetProperty("context");
        Assert.True(claudeContext.GetProperty("measurable").GetBoolean());

        List<string> missingBuckets = [.. claudeContext.GetProperty("missingBuckets").EnumerateArray().Select(static e => e.GetString()!)];
        Assert.Equal(["system_prompt", "tool_definitions"], missingBuckets);

        IReadOnlyDictionary<string, JsonElement> expectedMeasurability = CanonSynth.MeasurabilityFor("claude-code");
        List<string> expectedReasons =
        [
            expectedMeasurability["system_prompt"].GetProperty("reason").GetString()!,
            expectedMeasurability["tool_definitions"].GetProperty("reason").GetString()!,
        ];
        List<string> actualReasons = [.. claudeContext.GetProperty("residualReasons").EnumerateArray().Select(static e => e.GetString()!)];
        Assert.Equal(expectedReasons, actualReasons);

        JsonElement opencodeCharts = bridge.GetSessionCharts("sess-opencode-charts")!.Value;
        JsonElement opencodeContext = opencodeCharts.GetProperty("context");
        Assert.True(opencodeContext.GetProperty("measurable").GetBoolean());
        Assert.False(opencodeContext.TryGetProperty("missingBuckets", out _));
        Assert.False(opencodeContext.TryGetProperty("residualReasons", out _));

        // A Claude session that DID capture system_prompt and tool_definitions omits missingBuckets/residualReasons too.
        WriteSession(
            store,
            "sess-claude-complete",
            [
                Turn(
                    "cc-turn-1",
                    sessionKey: "sess-claude-complete",
                    minuteOffset: 0,
                    inputTokens: 500,
                    outputTokens: 30,
                    parts:
                    [
                        TextPart(0, ContentBucket.SystemPrompt, "claude system prompt", role: "system"),
                        TextPart(1, ContentBucket.ConversationHistory, "hello complete claude", role: "user"),
                    ],
                    definitions: [new CollectedToolDefinition(0, "read_file", null, 50)]),
            ],
            harnessId: "claude-code");
        _ = projector.ProjectIncremental();
        JsonElement completeClaudeContext = bridge.GetSessionCharts("sess-claude-complete")!.Value.GetProperty("context");
        Assert.True(completeClaudeContext.GetProperty("measurable").GetBoolean());
        Assert.False(completeClaudeContext.TryGetProperty("missingBuckets", out _));
        Assert.False(completeClaudeContext.TryGetProperty("residualReasons", out _));
    }

    /// <summary>
    /// End-to-end in-memory refresh -&gt; projection -&gt; <see cref="KyberBridgeEngine.GetSessionCharts"/>
    /// yields populated multi-turn message and tool-result buckets rather than ~100% residual, handles
    /// compaction resets, links nested subagents, and exposes omission telemetry.
    /// </summary>
    [Fact]
    public void EndToEnd_InMemoryRefreshProjectAndCharts_ProducesRealisticNonResidualClaudeShares()
    {
        string homeRoot = tempDir.Combine("home");
        string stateRoot = Path.Combine(homeRoot, ".kyberdash");
        string transcriptPath = Path.Combine(homeRoot, ".claude", "projects", "repo", "sess-e2e.jsonl");
        string subagentPath = Path.Combine(homeRoot, ".claude", "projects", "repo", "sess-e2e", "subagents", "agent-e2e1.jsonl");

        string turn0User = string.Join(' ', Enumerable.Repeat("word", 180));
        string turn0Assistant = string.Join(' ', Enumerable.Repeat("reply", 120));
        string turn1Result = string.Join(' ', Enumerable.Repeat("output", 150));
        string mainJsonl = string.Join('\n',
        [
            $"{{\"type\":\"user\",\"sessionId\":\"sess-e2e\",\"timestamp\":\"2026-11-19T11:50:00.000Z\",\"message\":{{\"role\":\"user\",\"content\":{JsonSerializer.Serialize(turn0User)}}}}}",
            $"{{\"type\":\"assistant\",\"sessionId\":\"sess-e2e\",\"timestamp\":\"2026-11-19T11:50:01.000Z\",\"message\":{{\"id\":\"msg-e1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(turn0Assistant)}}},{{\"type\":\"tool_use\",\"id\":\"tc-e1\",\"name\":\"read_file\",\"input\":{{\"path\":\"main.cs\"}}}}],\"usage\":{{\"input_tokens\":200,\"output_tokens\":130}}}}}}",
            $"{{\"type\":\"user\",\"sessionId\":\"sess-e2e\",\"timestamp\":\"2026-11-19T11:50:02.000Z\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"tool_result\",\"tool_use_id\":\"tc-e1\",\"content\":{JsonSerializer.Serialize(turn1Result)}}}]}}}}",
            "{\"type\":\"assistant\",\"sessionId\":\"sess-e2e\",\"timestamp\":\"2026-11-19T11:50:03.000Z\",\"message\":{\"id\":\"msg-e2\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"finished\"}],\"usage\":{\"input_tokens\":480,\"output_tokens\":15}}}",
        ]);
        string subagentJsonl =
            "{\"type\":\"user\",\"timestamp\":\"2026-11-19T11:51:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"subagent task\"}}\n" +
            "{\"type\":\"assistant\",\"timestamp\":\"2026-11-19T11:51:01.000Z\",\"message\":{\"id\":\"msg-sub\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"subagent done\"}],\"usage\":{\"input_tokens\":20,\"output_tokens\":5}}}";

        FakeHarnessFileSystem fs = new();
        fs.AddFile(transcriptPath, mainJsonl, TurnBase.AddMinutes(-5));
        fs.AddFile(subagentPath, subagentJsonl, TurnBase.AddMinutes(-4));

        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        FakeTextTokenizer tokenizer = new();
        CanonSessionProjectorEngine projector = new(store, store, store, pricing, tokenizer);
        CanonRollupsEngine rollups = new(store, store, pricing);
        CanonRefreshEngine refreshEngine = new(
            store,
            store,
            store,
            new HarnessSourceRegistry(),
            [new ClaudeTranscriptMapper()],
            fs,
            projector,
            rollups,
            new DashOptions { StateDirectory = stateRoot },
            homeRoot,
            () => TurnBase);

        FullReingestReport ingest = refreshEngine.FullReingest(historyWeeks: 2);
        Assert.Equal(0, ingest.ExitCode);

        KyberBridgeEngine bridge = CreateBridge(store, pricing, projector, rollups);
        JsonElement charts = bridge.GetSessionCharts("sess-e2e")!.Value;
        JsonElement context = charts.GetProperty("context");

        Assert.True(context.GetProperty("measurable").GetBoolean());
        JsonElement[] turns = [.. context.GetProperty("turns").EnumerateArray()];
        Assert.Equal(2, turns.Length);

        JsonElement turn1 = turns[1];
        long turn1Input = turn1.GetProperty("inputTokens").GetInt64();
        long turn1Bucketed = turn1.GetProperty("bucketedTokens").GetInt64();
        long turn1Residual = turn1.GetProperty("residual").GetProperty("tokens").GetInt64();
        Assert.True(turn1Bucketed > turn1Residual, $"Expected bucketed ({turn1Bucketed}) > residual ({turn1Residual}) on Turn 2 of {turn1Input} input tokens.");
        Assert.True(turn1.GetProperty("buckets").GetProperty("conversation_history").GetInt64() > 180L);
        Assert.Equal(150L, turn1.GetProperty("buckets").GetProperty("tool_result_content").GetInt64());

        Assert.Equal(
            ["system_prompt", "tool_definitions"],
            [.. context.GetProperty("missingBuckets").EnumerateArray().Select(static e => e.GetString()!)]);
        Assert.NotEmpty(context.GetProperty("residualReasons").EnumerateArray());

        StoredSessionProjection subagentProjection = ((IProjectionStore)store).GetSessionProjection("sess-e2e#agent-e2e1")!;
        Assert.True(subagentProjection.IsSubagent);
        Assert.Equal("sess-e2e", subagentProjection.ParentSessionId);
    }

    /// <summary>
    /// An empty Claude session with zero records projects safely with no turns and returns
    /// <see langword="null"/> from <see cref="KyberBridgeEngine.GetSessionCharts"/>.
    /// </summary>
    [Fact]
    public void ProjectIncremental_EmptyClaudeSession_ReturnsEmptyTurnsSafely()
    {
        InMemoryRelationalStore store = new();
        WriteSession(store, "sess-empty-claude", [], harnessId: "claude-code");
        FakePricingRepository pricing = new();
        CanonSessionProjectorEngine projector = new(store, store, store, pricing, new FakeTextTokenizer());

        ProjectionPassReport report = projector.ProjectIncremental();
        Assert.Equal(0, report.Built);

        KyberBridgeEngine bridge = CreateBridge(store, pricing, projector);
        Assert.Null(bridge.GetSessionCharts("sess-empty-claude"));
    }

    private static void AssertCompactionHydrationAndReset(
        IRelationalStoreReader reader,
        IProjectionStore projections,
        IOperationalStore operationalStore)
    {
        IReadOnlyList<CollectedRecord> collected = reader.SessionRecordsForRebuild("sess-compact");
        Assert.Equal(3, collected.Count);

        List<CanonicalRecord> hydrated = [.. collected.Select(CanonRecordHydration.Hydrate)];
        CanonicalRecord pre = hydrated.Single(static r => r.SpanId == "turn-0");
        Assert.False(pre.Raw?.ContainsKey("claude.compact_boundary") ?? false);

        CanonicalRecord boundary = hydrated.Single(static r => r.SpanId == "turn-2");
        Assert.NotNull(boundary.Raw);
        Assert.True(boundary.Raw!.TryGetValue("claude.compact_boundary", out JsonElement boundaryEl));
        Assert.Equal(JsonValueKind.True, boundaryEl.ValueKind);
        Assert.NotNull(boundary.Parts);
        Assert.Single(boundary.Parts!);
        Assert.Equal("Post-compaction summary only", boundary.Parts[0].Text);

        CanonSessionProjectorEngine engine = new(reader, projections, operationalStore, new FakePricingRepository(), new FakeTextTokenizer());
        _ = engine.ProjectIncremental();

        StoredSessionProjection projection = projections.GetSessionProjection("sess-compact")!;
        Assert.NotNull(projection.Context);
        Assert.Equal(3, projection.Context!.Turns.Count);
        Assert.Equal(4L, BucketTokens(projection.Context.Turns[0], "conversation_history"));
        Assert.Equal(13L, BucketTokens(projection.Context.Turns[1], "conversation_history"));
        Assert.Equal(3L, BucketTokens(projection.Context.Turns[2], "conversation_history"));
    }

    private static long BucketTokens(StoredSessionContextTurn turn, string bucket) =>
        turn.Buckets.FirstOrDefault(b => string.Equals(b.Bucket, bucket, StringComparison.Ordinal))?.Tokens ?? 0L;

    private static KyberBridgeEngine CreateBridge(
        InMemoryRelationalStore store,
        FakePricingRepository pricing,
        CanonSessionProjectorEngine projector,
        CanonRollupsEngine? rollups = null) =>
        new(
            store,
            store,
            store,
            pricing,
            projector,
            rollups ?? new CanonRollupsEngine(store, store, pricing),
            clock: () => TurnBase.AddHours(1),
            instanceId: "test-instance");

    private static void WriteSession(
        IRelationalStoreWriter writer,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records,
        string harnessId = "claude-code")
    {
        writer.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.File, $"file:{harnessId}:{sessionKey}"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: sessionKey,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));
    }

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        int minuteOffset,
        long inputTokens,
        long outputTokens,
        string harnessId = "claude-code",
        string? agentInstanceId = null,
        IReadOnlyList<CollectedContentPart>? parts = null,
        IReadOnlyList<CollectedToolDefinition>? definitions = null) =>
        new(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Internal,
            Name: "chat.turn",
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: $"trace:{sessionKey}",
            Model: "claude-sonnet-4-5",
            EndedAt: null,
            ReportedDurationMs: 100,
            Tokens:
            [
                new CollectedTokenCount(TokenClass.Input, inputTokens),
                new CollectedTokenCount(TokenClass.Output, outputTokens),
            ],
            ReportedCost: null,
            Parts: parts ?? [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: agentInstanceId,
            ToolDefinitions: definitions ?? [],
            Signals: new CollectedRecordSignals(null, null, null, 200_000),
            ReportedInputTokens: inputTokens,
            ReportedOutputTokens: outputTokens);

    private static CollectedContentPart TextPart(
        int position,
        ContentBucket bucket,
        string text,
        string? role = null) =>
        new(
            Position: position,
            Bucket: bucket,
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: role,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static NativeSourceUnit CreateClaudeUnit(string sourceKey, string rawText) =>
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
            SizeBytes: Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: TurnBase);

    private sealed class CountingFakeTokenizer : ITextTokenizer
    {
        private readonly FakeTextTokenizer inner = new();
        private readonly Dictionary<string, int> callCounts = new(StringComparer.Ordinal);

        public TokenCount Count(string text, string? model)
        {
            callCounts[text] = callCounts.GetValueOrDefault(text) + 1;
            return inner.Count(text, model);
        }

        public int CountCallsFor(string text) => callCounts.GetValueOrDefault(text);
    }

    private sealed class FakeHarnessFileSystem : IHarnessFileSystem
    {
        private readonly Dictionary<string, (string Content, long LengthBytes, DateTimeOffset LastWriteUtc)> files = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = new(StringComparer.Ordinal);

        public void AddFile(string path, string content, DateTimeOffset lastWriteUtc)
        {
            string normalized = Path.GetFullPath(path);
            files[normalized] = (content, Encoding.UTF8.GetByteCount(content), lastWriteUtc);

            string? dir = Path.GetDirectoryName(normalized);
            while (!string.IsNullOrEmpty(dir))
            {
                _ = directories.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        public bool DirectoryExists(string path) => directories.Contains(Path.GetFullPath(path));

        public bool FileExists(string path) => files.ContainsKey(Path.GetFullPath(path));

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive)
        {
            string normalizedRoot = Path.GetFullPath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            List<string> matches = [];
            foreach (string filePath in files.Keys.OrderBy(static k => k, StringComparer.Ordinal))
            {
                string? parent = Path.GetDirectoryName(filePath);
                if (parent is null)
                {
                    continue;
                }

                bool inScope = recursive
                    ? string.Equals(parent, normalizedRoot, StringComparison.Ordinal)
                      || parent.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    : string.Equals(parent, normalizedRoot, StringComparison.Ordinal);

                if (inScope && MatchesPattern(Path.GetFileName(filePath), searchPattern))
                {
                    matches.Add(filePath);
                }
            }

            return matches;
        }

        public IEnumerable<string> EnumerateDirectories(string directoryPath)
        {
            string normalizedRoot = Path.GetFullPath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            return directories
                .Where(d => string.Equals(Path.GetDirectoryName(d), normalizedRoot, StringComparison.Ordinal))
                .OrderBy(static d => d, StringComparer.Ordinal)
                .ToList();
        }

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            if (files.TryGetValue(Path.GetFullPath(filePath), out (string Content, long LengthBytes, DateTimeOffset LastWriteUtc) entry))
            {
                lengthBytes = entry.LengthBytes;
                lastWriteUtc = entry.LastWriteUtc;
                return true;
            }

            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath) => files[Path.GetFullPath(filePath)].Content;

        public string ReadAllText(string filePath, long maxBytes)
        {
            string text = ReadAllText(filePath);
            return Encoding.UTF8.GetByteCount(text) <= maxBytes
                ? text
                : throw new InvalidDataException($"'{filePath}' holds more than {maxBytes} bytes.");
        }

        public string GetFullPath(string path) => Path.GetFullPath(path);

        private static bool MatchesPattern(string fileName, string pattern)
        {
            if (string.Equals(pattern, "*", StringComparison.Ordinal))
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
