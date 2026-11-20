using KyberDash.Application.Tests;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Verifies the relational <see cref="CanonRollupsEngine"/> harness rollup projection: rollups are
/// written as typed <c>harness_rollup</c> rows with one <c>harness_rollup_metric</c> row per
/// measurable dimension, harnesses without sessions leave no metric rows behind, and the BL-3
/// pruned harnesses (<c>aider</c>, <c>windsurf</c>) never gain rollup rows.
/// </summary>
public class RollupProjectionTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An observed harness with measurable telemetry gains one typed rollup row whose metric rows
    /// carry the measured values; every emitted metric row is non-zero.
    /// </summary>
    [Fact]
    public void ProjectRunsAndHarnesses_WritesTypedMetricRowsForObservedHarness()
    {
        InMemoryRelationalStore store = new();
        CollectedRecord first = Turn(
            "turn-1",
            sessionKey: "sess-rollup",
            minuteOffset: 0,
            inputTokens: 700,
            cacheRead: 200,
            outputTokens: 120,
            parts: [TextPart(0, "system_prompt", LongText(600)), TextPart(1, "conversation_history", "Fix the login bug")],
            definitions: [new CollectedToolDefinition(0, "read_file", "fs", 120), new CollectedToolDefinition(1, "web_search", "fs", 80)],
            declaredContextWindow: 200_000);
        CollectedRecord second = Turn(
            "turn-2",
            sessionKey: "sess-rollup",
            minuteOffset: 1,
            inputTokens: 900,
            cacheRead: 300,
            outputTokens: 90,
            parts: [TextPart(0, "conversation_history", "Second turn")]);
        CollectedRecord toolCall = Turn(
            "tool-1",
            sessionKey: "sess-rollup",
            minuteOffset: 1,
            name: "read_file",
            op: RecordOp.ToolInvoke,
            parentSpanId: "turn-2",
            tool: new CollectedToolCall("call-1", "read_file", null, null, null, null, true, null, null));
        WriteSession(store, "sess-rollup", [first, second, toolCall], title: "Fix the login bug");

        Assert.Equal(1, NewProjector(store).ProjectIncremental().Built);

        CanonRollupsEngine engine = NewRollups(store);
        IReadOnlyList<HarnessRollupRow> rollups = engine.ProjectRunsAndHarnesses();

        HarnessRollupRow claude = Assert.Single(rollups, static row => row.Harness == "claude-code");
        Assert.Equal(1, claude.SampleCount);
        Assert.Equal(0.5, claude.FieldCoverage);

        StoredHarnessRollup? stored = ((IProjectionStore)store).GetHarnessRollup("claude-code");
        Assert.NotNull(stored);
        Assert.Equal(1, stored!.SessionCount);
        Assert.Equal(1, stored.RunCount);
        Assert.Equal(1, stored.ExecutionCount);

        StoredHarnessRollupMetric median = Assert.Single(stored.Metrics, static metric => metric.Metric == "context_pressure_median");
        Assert.True(median.Value > 0);
        StoredHarnessRollupMetric p95 = Assert.Single(stored.Metrics, static metric => metric.Metric == "context_pressure_p95");
        Assert.True(p95.Value > 0);
        StoredHarnessRollupMetric cache = Assert.Single(stored.Metrics, static metric => metric.Metric == "cache_hit_rate");
        Assert.InRange(cache.Value, 0.0, 1.0);
        Assert.True(cache.Value > 0);
        StoredHarnessRollupMetric yield = Assert.Single(stored.Metrics, static metric => metric.Metric == "tool_yield");
        Assert.InRange(yield.Value, 0.0, 1.0);
        Assert.True(yield.Value > 0);

        Assert.DoesNotContain(stored.Metrics, static metric => metric.Metric == "field_coverage");
        Assert.DoesNotContain(stored.Metrics, static metric => metric.Value == 0);
        Assert.Equal(stored.Metrics.Count, store.CountRows("harness_rollup_metric"));
    }

    /// <summary>
    /// Harnesses without sessions gain no rollup row and therefore no metric rows; an observed
    /// harness with unmeasurable dimensions omits those metric rows instead of writing zeroes.
    /// </summary>
    [Fact]
    public void ProjectRunsAndHarnesses_LeavesAbsentHarnessesWithoutMetricRows()
    {
        InMemoryRelationalStore store = new();
        WriteSession(
            store,
            "sess-plain",
            [Turn("turn-1", sessionKey: "sess-plain", minuteOffset: 0, inputTokens: 500, outputTokens: 40)]);

        Assert.Equal(1, NewProjector(store).ProjectIncremental().Built);
        IReadOnlyList<HarnessRollupRow> rollups = NewRollups(store).ProjectRunsAndHarnesses();

        Assert.Single(rollups);
        Assert.Equal("claude-code", rollups[0].Harness);

        Assert.Null(((IProjectionStore)store).GetHarnessRollup("codex"));
        Assert.Equal([rollups[0].Harness], ((IProjectionStore)store).ListHarnessRollups().Select(static row => row.HarnessId));

        StoredHarnessRollup? stored = ((IProjectionStore)store).GetHarnessRollup("claude-code");
        Assert.NotNull(stored);
        Assert.Empty(stored!.Metrics);
        Assert.Equal(0.0, rollups[0].FieldCoverage);
        Assert.DoesNotContain(stored.Metrics, static metric => metric.Metric == "context_pressure_median");
        Assert.DoesNotContain(stored.Metrics, static metric => metric.Metric == "cache_hit_rate");
        Assert.DoesNotContain(stored.Metrics, static metric => metric.Metric == "tool_yield");
        Assert.DoesNotContain(stored.Metrics, static metric => metric.Value == 0);
        Assert.Equal(stored.Metrics.Count, store.CountRows("harness_rollup_metric"));
    }

    /// <summary>
    /// BL-3: stale <c>aider</c> and <c>windsurf</c> rollup rows are pruned and sessions attributed
    /// to those harnesses never gain rollup or metric rows.
    /// </summary>
    [Fact]
    public void ProjectRunsAndHarnesses_PrunesAiderAndWindsurfRollups()
    {
        InMemoryRelationalStore store = new();
        WriteSession(
            store,
            "sess-aider",
            [Turn("turn-a", sessionKey: "sess-aider", minuteOffset: 0, inputTokens: 300, outputTokens: 30, harnessId: "aider")],
            harnessId: "aider");
        WriteSession(
            store,
            "sess-codex",
            [Turn("turn-b", sessionKey: "sess-codex", minuteOffset: 0, inputTokens: 400, outputTokens: 40, harnessId: "codex")],
            harnessId: "codex");
        WriteSession(
            store,
            "sess-windsurf",
            [Turn("turn-c", sessionKey: "sess-windsurf", minuteOffset: 0, inputTokens: 250, outputTokens: 25, harnessId: "windsurf")],
            harnessId: "windsurf");
        ((IProjectionStore)store).ReplaceHarnessRollups(
        [
            new StoredHarnessRollup("aider", 0, 0, 0, 0, 0, []),
            new StoredHarnessRollup("windsurf", 0, 0, 0, 0, 0, []),
        ]);

        Assert.Equal(3, NewProjector(store).ProjectIncremental().Built);
        IReadOnlyList<HarnessRollupRow> rollups = NewRollups(store).ProjectRunsAndHarnesses();

        Assert.DoesNotContain(rollups, static row => row.Harness == "aider");
        Assert.DoesNotContain(rollups, static row => row.Harness == "windsurf");
        Assert.Contains(rollups, static row => row.Harness == "codex");

        Assert.Null(((IProjectionStore)store).GetHarnessRollup("aider"));
        Assert.Null(((IProjectionStore)store).GetHarnessRollup("windsurf"));
        Assert.DoesNotContain(
            store.GetTableRows("harness_rollup_metric"),
            static row => row.Cells.Any(static cell => cell.TextValue is "aider" or "windsurf"));
    }

    /// <summary>
    /// An empty relational store projects no rollup rows and no metric rows.
    /// </summary>
    [Fact]
    public void ProjectRunsAndHarnesses_EmptyStoreWritesNoRollupOrMetricRows()
    {
        InMemoryRelationalStore store = new();

        IReadOnlyList<HarnessRollupRow> rollups = NewRollups(store).ProjectRunsAndHarnesses();

        Assert.Empty(rollups);
        Assert.Empty(((IProjectionStore)store).ListHarnessRollups());
        Assert.Equal(0, store.CountRows("harness_rollup"));
        Assert.Equal(0, store.CountRows("harness_rollup_metric"));
    }

    private static CanonSessionProjectorEngine NewProjector(InMemoryRelationalStore store) =>
        new(store, store, store, new FakePricingRepository());

    private static CanonRollupsEngine NewRollups(InMemoryRelationalStore store) =>
        new(store, store, new FakePricingRepository());

    private static void WriteSession(
        InMemoryRelationalStore store,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records,
        string harnessId = "claude-code",
        string? title = null)
    {
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: sessionKey,
                    Workspace: null,
                    Title: title,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));
    }

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        int minuteOffset,
        long inputTokens = 0,
        long cacheRead = 0,
        long outputTokens = 0,
        string? parentSpanId = null,
        string name = "chat.turn",
        RecordOp op = RecordOp.LlmInvoke,
        RecordStatus status = RecordStatus.Ok,
        string? model = "claude-sonnet-4-6",
        IReadOnlyList<CollectedContentPart>? parts = null,
        IReadOnlyList<CollectedToolDefinition>? definitions = null,
        long? declaredContextWindow = null,
        CollectedAgentInvocation? agent = null,
        CollectedToolCall? tool = null,
        string harnessId = "claude-code")
    {
        List<CollectedTokenCount> tokens =
        [
            new CollectedTokenCount(TokenClass.Input, inputTokens),
            new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
            new CollectedTokenCount(TokenClass.CacheWrite, 0),
            new CollectedTokenCount(TokenClass.Output, outputTokens),
        ];

        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: op,
            Kind: RecordKind.Client,
            Name: name,
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: status,
            SessionKey: sessionKey,
            ParentRecordId: parentSpanId,
            TraceId: null,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens: tokens,
            ReportedCost: null,
            Parts: parts ?? [],
            Tool: tool,
            Agent: agent,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: definitions ?? [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: declaredContextWindow),
            ReportedInputTokens: inputTokens + cacheRead,
            ReportedOutputTokens: outputTokens);
    }

    private static CollectedContentPart TextPart(int position, string bucket, string text) =>
        new(
            Position: position,
            Bucket: BucketOf(bucket),
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static ContentBucket BucketOf(string bucket) => bucket switch
    {
        "system_prompt" => ContentBucket.SystemPrompt,
        "conversation_history" => ContentBucket.ConversationHistory,
        "user_prompt" => ContentBucket.UserPrompt,
        "tool_result_content" => ContentBucket.ToolResultContent,
        _ => throw new ArgumentException($"Unexpected bucket '{bucket}'.", nameof(bucket)),
    };

    private static string LongText(int length) => new('x', length);
}
