using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Analytics;

/// <summary>
/// The run comparison the engine serves on <c>GET /api/kyber/compare/runs</c>: each turn's
/// canonical token total, the phase-aligned pairs, per-run sides, aggregate totals, and the
/// verdict are all engine decisions. A turn whose tokens were not measured keeps its measured
/// pieces, carries an explicit not-measurable reason instead of a total, and withholds every
/// aggregate it would have polluted — a withheld figure is never a fabricated zero.
/// </summary>
public sealed class CanonRunComparisonTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private const string RunA = "derived:claude-code:sess-cmp-a";
    private const string RunB = "derived:claude-code:sess-cmp-b";

    [Fact]
    public void FullTokenRunsServeCanonicalTurnTotalsAndMeasuredSides()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-cmp-a",
            [
                Turn("span-cmp-a1", "sess-cmp-a", 0, inputTokens: 400, cacheRead: 100, outputTokens: 60),
                Turn("span-cmp-a2", "sess-cmp-a", 2, inputTokens: 300, cacheRead: 0, outputTokens: 40, cacheCreation: 50),
            ]);
        WriteSession(
            store,
            "sess-cmp-b",
            [Turn("span-cmp-b1", "sess-cmp-b", 0, inputTokens: 1_000, cacheRead: 0, outputTokens: 100)]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        JsonElement comparison = bridge.GetCompareRuns(RunA, RunB)!.Value;

        JsonElement runA = comparison.GetProperty("runA");
        Assert.Equal("measured", runA.GetProperty("availability").GetString());
        Assert.Equal(950, runA.GetProperty("totalTokens").GetInt64());
        Assert.Equal(2, runA.GetProperty("turnCount").GetInt32());
        JsonElement runB = comparison.GetProperty("runB");
        Assert.Equal("measured", runB.GetProperty("availability").GetString());
        Assert.Equal(1_100, runB.GetProperty("totalTokens").GetInt64());
        Assert.Equal(1, runB.GetProperty("turnCount").GetInt32());

        JsonElement totals = comparison.GetProperty("totals");
        Assert.Equal("measured", totals.GetProperty("availability").GetString());
        Assert.Equal(950, totals.GetProperty("tokensA").GetInt64());
        Assert.Equal(1_100, totals.GetProperty("tokensB").GetInt64());
        Assert.Equal(150, totals.GetProperty("tokenDelta").GetInt64());
        Assert.Equal(2, totals.GetProperty("turnCountA").GetInt32());
        Assert.Equal(1, totals.GetProperty("turnCountB").GetInt32());
        Assert.Equal(-1, totals.GetProperty("turnDelta").GetInt32());

        // Run A's two turns split across position-inferred phases — the last turn resolves — and
        // Run B's single turn explores: slot 0 compares, Run A's resolution slot stays unpaired.
        JsonElement pairs = comparison.GetProperty("pairs");
        Assert.Equal(2, pairs.GetArrayLength());
        Assert.Equal("exploration", pairs[0].GetProperty("phase").GetString());
        Assert.Equal(560, pairs[0].GetProperty("runATurn").GetProperty("tokens").GetProperty("all").GetInt64());
        Assert.Equal(1_100, pairs[0].GetProperty("runBTurn").GetProperty("tokens").GetProperty("all").GetInt64());
        JsonElement comparedSignal = Assert.Single(
            pairs[0].GetProperty("signals").EnumerateArray(),
            static signal => signal.GetProperty("name").GetString() == "total_tokens");
        Assert.Equal("compared", comparedSignal.GetProperty("status").GetString());
        Assert.Equal(540, comparedSignal.GetProperty("delta").GetInt64());

        Assert.Equal("resolution", pairs[1].GetProperty("phase").GetString());
        Assert.Equal(390, pairs[1].GetProperty("runATurn").GetProperty("tokens").GetProperty("all").GetInt64());
        Assert.Equal(JsonValueKind.Null, pairs[1].GetProperty("runBTurn").ValueKind);
        JsonElement missingSignal = Assert.Single(
            pairs[1].GetProperty("signals").EnumerateArray(),
            static signal => signal.GetProperty("name").GetString() == "total_tokens");
        Assert.Equal("missing_in_b", missingSignal.GetProperty("status").GetString());

        // Every measurable turn carries the canonical total and no not-measurable marker.
        Assert.True(pairs[0].GetProperty("runATurn").GetProperty("tokens").TryGetProperty("all", out _));
        Assert.False(pairs[0].GetProperty("runATurn").GetProperty("tokens").TryGetProperty("allReason", out _));

        JsonElement exploration = comparison.GetProperty("phaseSummaries").GetProperty("exploration");
        Assert.Equal(1, exploration.GetProperty("turnsA").GetInt32());
        Assert.Equal(1, exploration.GetProperty("turnsB").GetInt32());
        Assert.Equal(560, exploration.GetProperty("tokensA").GetInt64());
        Assert.Equal(540, exploration.GetProperty("tokenDelta").GetInt64());

        JsonElement resolution = comparison.GetProperty("phaseSummaries").GetProperty("resolution");
        Assert.Equal(1, resolution.GetProperty("turnsA").GetInt32());
        Assert.Equal(0, resolution.GetProperty("turnsB").GetInt32());
        Assert.Equal(390, resolution.GetProperty("tokensA").GetInt64());
        Assert.Equal(-390, resolution.GetProperty("tokenDelta").GetInt64());

        JsonElement verdict = comparison.GetProperty("verdict");
        Assert.Equal("insufficient_history", verdict.GetProperty("status").GetString());
        Assert.Equal("unavailable", verdict.GetProperty("historyAvailability").GetString());
        Assert.False(verdict.GetProperty("canPromote").GetBoolean());
        Assert.False(verdict.TryGetProperty("completedPairCount", out _));
    }

    [Fact]
    public void PartialTokenRunIsNotMeasurableAndNeverZero()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-cmp-a",
            [
                Turn("span-cmp-a1", "sess-cmp-a", 0, inputTokens: 100, cacheRead: 0, outputTokens: 10),
                // Output only: no input class was measured, so the canonical total is withheld.
                Turn(
                    "span-cmp-a2",
                    "sess-cmp-a",
                    2,
                    tokenCounts: [new CollectedTokenCount(TokenClass.Output, 30)]),
            ]);
        WriteSession(
            store,
            "sess-cmp-b",
            [Turn("span-cmp-b1", "sess-cmp-b", 0, inputTokens: 50, cacheRead: 0, outputTokens: 5)]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        JsonElement comparison = bridge.GetCompareRuns(RunA, RunB)!.Value;

        // The side withholds its total, names why, and still reports the resolved turn count.
        JsonElement runA = comparison.GetProperty("runA");
        Assert.Equal("unavailable", runA.GetProperty("availability").GetString());
        Assert.False(runA.TryGetProperty("totalTokens", out _));
        Assert.Equal(2, runA.GetProperty("turnCount").GetInt32());
        Assert.Contains("telemetry", runA.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(runA.GetProperty("reason").GetString(), runA.GetProperty("metricsReason").GetString());
        Assert.Equal("measured", comparison.GetProperty("runB").GetProperty("availability").GetString());
        Assert.Equal(55, comparison.GetProperty("runB").GetProperty("totalTokens").GetInt64());

        // The unmeasured turn keeps every measured piece, and the marker travels with the tokens.
        JsonElement pairs = comparison.GetProperty("pairs");
        Assert.Equal(2, pairs.GetArrayLength());
        JsonElement unmeasuredTokens = pairs[1].GetProperty("runATurn").GetProperty("tokens");
        Assert.False(unmeasuredTokens.TryGetProperty("all", out _));
        Assert.Equal(
            "token telemetry unavailable for this turn",
            unmeasuredTokens.GetProperty("allReason").GetString());
        Assert.Equal(30, unmeasuredTokens.GetProperty("output").GetInt64());
        // A measured zero stays zero — it is not the same state as the withheld total.
        Assert.Equal(0, pairs[0].GetProperty("runATurn").GetProperty("tokens").GetProperty("cacheRead").GetInt64());

        JsonElement missingSignal = Assert.Single(
            pairs[1].GetProperty("signals").EnumerateArray(),
            static signal => signal.GetProperty("name").GetString() == "total_tokens");
        Assert.Equal("missing_in_b", missingSignal.GetProperty("status").GetString());
        Assert.False(missingSignal.TryGetProperty("runAValue", out _));

        // Aggregate totals stay withheld with a reason; the phase summary follows the same rule.
        JsonElement totals = comparison.GetProperty("totals");
        Assert.Equal("unavailable", totals.GetProperty("availability").GetString());
        Assert.False(totals.TryGetProperty("tokenDelta", out _));
        Assert.Contains("not measurable", totals.GetProperty("reason").GetString(), StringComparison.Ordinal);

        JsonElement resolution = comparison.GetProperty("phaseSummaries").GetProperty("resolution");
        Assert.Equal(1, resolution.GetProperty("turnsA").GetInt32());
        Assert.Equal(0, resolution.GetProperty("turnsB").GetInt32());
        Assert.False(resolution.TryGetProperty("tokensA", out _));
        Assert.False(resolution.TryGetProperty("tokensB", out _));
        Assert.False(resolution.TryGetProperty("tokenDelta", out _));
        Assert.True(resolution.TryGetProperty("tokensUnavailableReason", out JsonElement gap));
        Assert.Contains("telemetry", gap.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnequalPhaseCountsAlignByPhaseWithMissingTurnSignals()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-cmp-a",
            [
                Turn("span-cmp-a1", "sess-cmp-a", 0, inputTokens: 100, outputTokens: 10),
                Turn("span-cmp-a2", "sess-cmp-a", 2, inputTokens: 200, outputTokens: 20),
                Turn("span-cmp-a3", "sess-cmp-a", 4, inputTokens: 300, outputTokens: 30),
                ToolRecord("span-cmp-a1-tool", "sess-cmp-a", 1, "grep_search"),
                ToolRecord("span-cmp-a2-tool", "sess-cmp-a", 3, "write_to_file"),
                ToolRecord("span-cmp-a3-tool", "sess-cmp-a", 5, "run_tests"),
            ]);
        WriteSession(
            store,
            "sess-cmp-b",
            [
                Turn("span-cmp-b1", "sess-cmp-b", 0, inputTokens: 50, outputTokens: 5),
                ToolRecord("span-cmp-b1-tool", "sess-cmp-b", 1, "grep_search"),
            ]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        JsonElement comparison = bridge.GetCompareRuns(RunA, RunB)!.Value;

        // Run A spans three phases, Run B one: each extra phase pairs against a missing turn.
        JsonElement pairs = comparison.GetProperty("pairs");
        Assert.Equal(3, pairs.GetArrayLength());
        Assert.Equal("exploration", pairs[0].GetProperty("phase").GetString());
        Assert.Equal("implementation", pairs[1].GetProperty("phase").GetString());
        Assert.Equal("verification", pairs[2].GetProperty("phase").GetString());

        Assert.Equal(JsonValueKind.Null, pairs[1].GetProperty("runBTurn").ValueKind);
        JsonElement missingInB = Assert.Single(
            pairs[1].GetProperty("signals").EnumerateArray(),
            static signal => signal.GetProperty("name").GetString() == "total_tokens");
        Assert.Equal("missing_in_b", missingInB.GetProperty("status").GetString());
        Assert.Equal(
            "Turn missing in Run B for this phase slot",
            missingInB.GetProperty("reason").GetString());
        Assert.Contains(
            "Run B required no turns in this phase slot",
            pairs[1].GetProperty("reading").GetString(),
            StringComparison.Ordinal);

        // The turn carries the tool evidence the phase was inferred from, so a surface never
        // re-derives it.
        JsonElement implementationTurn = pairs[1].GetProperty("runATurn");
        Assert.Equal("implementation", implementationTurn.GetProperty("phase").GetString());
        Assert.Contains(
            "write_to_file",
            implementationTurn.GetProperty("tools").EnumerateArray().Select(static tool => tool.GetString()));

        JsonElement phaseSummaries = comparison.GetProperty("phaseSummaries");
        Assert.Equal(1, phaseSummaries.GetProperty("exploration").GetProperty("turnsA").GetInt32());
        Assert.Equal(1, phaseSummaries.GetProperty("exploration").GetProperty("turnsB").GetInt32());
        Assert.Equal(1, phaseSummaries.GetProperty("implementation").GetProperty("turnsA").GetInt32());
        Assert.Equal(0, phaseSummaries.GetProperty("implementation").GetProperty("turnsB").GetInt32());
        Assert.Equal(1, phaseSummaries.GetProperty("verification").GetProperty("turnsA").GetInt32());
        Assert.Equal(0, phaseSummaries.GetProperty("verification").GetProperty("turnsB").GetInt32());
        Assert.Equal(0, phaseSummaries.GetProperty("resolution").GetProperty("turnsA").GetInt32());

        Assert.Equal(3, comparison.GetProperty("verdict").GetProperty("pairCount").GetInt32());
    }

    [Fact]
    public void MissingRunAnswersNullSoTheRouteReports404()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-cmp-b",
            [Turn("span-cmp-b1", "sess-cmp-b", 0, inputTokens: 50, outputTokens: 5)]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        Assert.Null(bridge.GetCompareRuns("derived:claude-code:no-such-run", RunB));
    }

    private static CanonProjectionService CreateProjection(InMemoryRelationalStore store, FakePricingRepository pricing) =>
        new(
            store,
            store,
            store,
            new CanonSessionProjectorEngine(store, store, store, pricing),
            new CanonRollupsEngine(store, store, pricing));

    private static KyberBridgeEngine CreateBridge(InMemoryRelationalStore store, FakePricingRepository pricing)
    {
        return new KyberBridgeEngine(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing),
            new CanonRollupsEngine(store, store, pricing),
            clock: () => FixedTimestamp,
            instanceId: "test-instance");
    }

    private static void WriteSession(
        InMemoryRelationalStore store,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records,
        string harnessId = "claude-code")
    {
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: sessionKey,
                    Workspace: new CollectedWorkspace("/tmp/working-directory", "kyber-weave", "kw-task/compare"),
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
        long inputTokens = 0,
        long cacheRead = 0,
        long outputTokens = 0,
        long cacheCreation = 0,
        IReadOnlyList<CollectedTokenCount>? tokenCounts = null)
    {
        List<CollectedTokenCount> tokens = tokenCounts is null
            ?
            [
                new CollectedTokenCount(TokenClass.Input, inputTokens),
                new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
                new CollectedTokenCount(TokenClass.CacheWrite, cacheCreation),
                new CollectedTokenCount(TokenClass.Output, outputTokens),
            ]
            : [.. tokenCounts];

        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: "claude-code",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: "claude-sonnet-4-6",
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens: tokens,
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: tokenCounts is null ? inputTokens + cacheRead : null,
            ReportedOutputTokens: tokenCounts is null ? outputTokens : null);
    }

    private static CollectedRecord ToolRecord(
        string spanId,
        string sessionKey,
        int minuteOffset,
        string toolName)
    {
        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: "claude-code",
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Client,
            Name: "tool.invoke",
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: new CollectedToolCall(
                CallId: spanId + "-call",
                ToolName: toolName,
                McpServerName: null,
                McpToolName: null,
                Input: null,
                Result: null,
                Succeeded: true,
                Error: null,
                ExitCode: null),
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: null,
            ReportedOutputTokens: null);
    }
}
