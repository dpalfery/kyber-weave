using KyberDash.Core.Exceptions;
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
/// Read-path parity for the relational KyberDash store: <see cref="KyberBridgeEngine"/> and
/// <see cref="AnalyticsService"/> answer every analytics and dashboard query from
/// <see cref="IRelationalStoreReader"/>, <see cref="IProjectionStore"/>, and
/// <see cref="IOperationalStore"/>, isolate a
/// failing session as <c>PROJECTION_FAILED</c> while the remaining sessions answer, and report
/// harness-reported and read-time published costs separately without blending them.
/// </summary>
public sealed class AnalyticsReadPathTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A session whose records span two harness ids (a harness renamed mid-life) counts once in
    /// the grouped query and outcomes, under the session's own harness, matching the
    /// pre-migration per-session aggregate rows.
    /// </summary>
    [Fact]
    public void AnalyticsQueryCountsAMultiHarnessSessionOnceUnderItsSessionHarness()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        // The codex session is written first so both harness rows exist before the shared
        // session's claude-code batch links codex records: the record table's harness foreign
        // key requires the harness row, exactly as a renamed harness already registered would.
        WriteSession(store, "sess-solo", [Turn("span-solo-1", "sess-solo", 0, harnessId: "codex", inputTokens: 100, outputTokens: 10)], harnessId: "codex");
        WriteSession(
            store,
            "sess-shared",
            [
                Turn("span-shared-1", "sess-shared", 0, harnessId: "claude-code", inputTokens: 400, outputTokens: 40),
                Turn("span-shared-2", "sess-shared", 1, harnessId: "codex", inputTokens: 400, outputTokens: 40),
            ],
            harnessId: "claude-code");

        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        (int queryStatus, JsonElement queried) = bridge.ExecuteAnalytics("query", null);
        Assert.Equal(200, queryStatus);
        List<JsonElement> groups = [.. queried.GetProperty("data").GetProperty("groups").EnumerateArray()];
        Assert.Equal(2, groups.Count);
        JsonElement claudeGroup = Assert.Single(groups, static group => group.GetProperty("key").GetProperty("harness").GetString() == "claude-code");
        JsonElement codexGroup = Assert.Single(groups, static group => group.GetProperty("key").GetProperty("harness").GetString() == "codex");
        Assert.Equal(1, claudeGroup.GetProperty("sessions").GetInt32());
        Assert.Equal(1, codexGroup.GetProperty("sessions").GetInt32());

        (int outcomesStatus, JsonElement outcomes) = bridge.ExecuteAnalytics("outcomes", null);
        Assert.Equal(200, outcomesStatus);
        List<string> outcomeSessions =
        [
            .. outcomes.GetProperty("data").GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("sessionId").GetString() ?? string.Empty),
        ];
        Assert.Equal(["sess-shared", "sess-solo"], [.. outcomeSessions.Order(StringComparer.Ordinal)]);
    }

    /// <summary>The analytics status, catalog, and query operations answer over the relational reader.</summary>
    [Fact]
    public void AnalyticsStatusCatalogAndQueryAnswerFromTheRelationalStore()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, "sess-a", [Turn("turn-a1", "sess-a", 0, inputTokens: 400, outputTokens: 60)]);
        WriteSession(store, "sess-b", [Turn("turn-b1", "sess-b", 0, inputTokens: 1_500, outputTokens: 120, harnessId: "codex")], harnessId: "codex");

        CanonProjectionService projection = CreateProjection(store, pricing);
        (ProjectionPassReport pass, _) = projection.ProjectAll();
        Assert.Equal(2, pass.Built);

        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics("status", null);
        Assert.Equal(200, statusCode);
        JsonElement value = envelope.GetProperty("data").GetProperty("value");
        Assert.True(value.GetProperty("ready").GetBoolean());
        Assert.Equal(2, value.GetProperty("sessionCount").GetInt32());
        Assert.Equal(2, value.GetProperty("recordCount").GetInt32());
        Assert.Equal(0, value.GetProperty("findingCount").GetInt32());
        Assert.Equal("test-instance", envelope.GetProperty("freshness").GetProperty("instanceId").GetString());

        (int catalogStatus, JsonElement catalog) = bridge.ExecuteAnalytics("catalog", null);
        Assert.Equal(200, catalogStatus);
        Assert.Equal(JsonValueKind.Array, catalog.GetProperty("data").GetProperty("operations").ValueKind);
        List<string> harnesses = [.. catalog.GetProperty("data").GetProperty("source").GetProperty("harnesses").EnumerateArray().Select(static h => h.GetString() ?? string.Empty)];
        Assert.Contains("claude-code", harnesses, StringComparer.Ordinal);
        Assert.Contains("codex", harnesses, StringComparer.Ordinal);

        (int queryStatus, JsonElement queried) = bridge.ExecuteAnalytics("query", Json(new { harness = "codex" }));
        Assert.Equal(200, queryStatus);
        JsonElement group = Assert.Single(queried.GetProperty("data").GetProperty("groups").EnumerateArray());
        Assert.Equal("codex", group.GetProperty("key").GetProperty("harness").GetString());
        Assert.Equal(1, group.GetProperty("sessions").GetInt32());
    }

    /// <summary><see cref="AnalyticsService"/> delegates to the relational bridge and rejects unknown operations.</summary>
    [Fact]
    public void AnalyticsServiceDelegatesOperationsAndRejectsUnknownOnes()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, "sess-a", [Turn("turn-a1", "sess-a", 0, inputTokens: 400, outputTokens: 60)]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        AnalyticsService service = new(bridge);

        AnalyticsExecutionResult direct = service.Execute("status", null);

        Assert.Equal(200, direct.StatusCode);
        Assert.Equal(direct.Envelope.GetRawText(), bridge.ExecuteAnalytics("status", null).Envelope.GetRawText());
        Assert.True(AnalyticsService.IsSupported("status"));
        Assert.False(AnalyticsService.IsSupported("bogus"));
        Assert.Equal(400, AnalyticsService.Unsupported("bogus").StatusCode);
        Assert.Equal("fake", bridge.GetModelCatalog().GetProperty("source").GetString());
    }

    /// <summary>Session figures come from typed aggregates and reported/published costs stay separate.</summary>
    [Fact]
    public void GetSessionPageAnswersTypedFiguresAndNeverBlendsReportedAndPublishedCosts()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new(snapshot: new Dictionary<string, ModelCosts>(StringComparer.Ordinal)
        {
            ["claude-sonnet-4-6"] = new ModelCosts(
                InputCostPerToken: 0.000003,
                OutputCostPerToken: 0.000015,
                CacheWriteCostPerToken: 0.00000375,
                CacheReadCostPerToken: 0.0000003,
                WebSearchCostPerRequest: 0,
                FastMultiplier: 1,
                CacheWriteCostIsExplicit: true),
        });
        WriteSession(
            store,
            "sess-cost",
            [Turn("turn-c1", "sess-cost", 0, inputTokens: 400, outputTokens: 60, reportedCostUsd: 0.4)],
            repo: "kyber-weave",
            branch: "kw-task/T43");
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        JsonElement node = Assert.Single(bridge.GetSessionPage().GetProperty("sessions").EnumerateArray());

        Assert.Equal("sess-cost", node.GetProperty("session_id").GetString());
        Assert.Equal("claude-code", node.GetProperty("harness").GetString());
        Assert.Equal("kyber-weave", node.GetProperty("repo").GetString());
        Assert.Equal("kw-task/T43", node.GetProperty("branch").GetString());
        Assert.Equal(1, node.GetProperty("turn_count").GetInt32());
        Assert.Equal(1, node.GetProperty("request_count").GetInt32());
        Assert.Equal(400, node.GetProperty("total_input").GetInt64());
        Assert.Equal(60, node.GetProperty("total_output").GetInt64());
        Assert.Equal(["claude-sonnet-4-6"], [.. node.GetProperty("models").EnumerateArray().Select(static m => m.GetString())]);

        JsonElement cost = node.GetProperty("cost");
        Assert.Equal("published", cost.GetProperty("basis").GetString());
        Assert.Equal("priced", cost.GetProperty("status").GetString());
        Assert.Equal("USD", cost.GetProperty("currency").GetString());
        Assert.Equal(0.0021, cost.GetProperty("value").GetDouble(), precision: 6);

        JsonElement reported = node.GetProperty("cost_reported");
        Assert.Equal("harness", reported.GetProperty("basis").GetString());
        Assert.Equal(0.4, reported.GetProperty("value").GetDouble(), precision: 6);

        double costUsd = node.GetProperty("cost_usd").GetDouble();
        Assert.Equal(0.0021, costUsd, precision: 6);
        Assert.NotEqual(0.4, costUsd);

        JsonElement mismatch = node.GetProperty("cost_basis_mismatch");
        Assert.Equal("COST_BASIS_MISMATCH", mismatch.GetProperty("code").GetString());
        Dictionary<string, double?> totalsByBasis = mismatch.GetProperty("totalsByBasis").EnumerateArray()
            .ToDictionary(static t => t.GetProperty("basis").GetString()!, static t => (double?)t.GetProperty("total").GetDouble(), StringComparer.Ordinal);
        Assert.NotNull(totalsByBasis["harness"]);
        Assert.Equal(0.4, totalsByBasis["harness"]!.Value, precision: 6);
        Assert.NotNull(totalsByBasis["published"]);
        Assert.Equal(0.0021, totalsByBasis["published"]!.Value, precision: 6);
        Assert.False(node.TryGetProperty("projection_status", out _));

        JsonElement summary = bridge.GetSessionSummary("sess-cost")!.Value;
        Assert.Equal(400, summary.GetProperty("summary").GetProperty("total_input").GetInt64());
        Assert.Equal(0.4, summary.GetProperty("summary").GetProperty("cost_reported").GetProperty("value").GetDouble(), precision: 6);
        Assert.Equal(0.0021, summary.GetProperty("summary").GetProperty("cost").GetProperty("value").GetDouble(), precision: 6);
    }

    /// <summary>
    /// The sessions-list read model agrees, field for field, with the per-session summary read
    /// model: <see cref="InMemoryRelationalStore.SessionListFigures(string?, IModelPricingLookup?)"/> equals
    /// <see cref="InMemoryRelationalStore.SessionSummaryFigures"/> per session, on a token-less
    /// session (token totals stay absent, never zero), a zero-token session (zero stays zero), a
    /// multi-harness session (every attributed harness's turns count), an empty session, and a
    /// content-bearing session.
    /// </summary>
    [Fact]
    public void SessionListFiguresAgreeWithThePerSessionSummaryFigures()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new(snapshot: new Dictionary<string, ModelCosts>(StringComparer.Ordinal)
        {
            ["claude-sonnet-4-6"] = new ModelCosts(
                InputCostPerToken: 0.000003,
                OutputCostPerToken: 0.000015,
                CacheWriteCostPerToken: 0.00000375,
                CacheReadCostPerToken: 0.0000003,
                WebSearchCostPerRequest: 0,
                FastMultiplier: 1,
                CacheWriteCostIsExplicit: true),
        });
        WriteSession(store, "sess-tokenless", [Turn("span-tl-1", "sess-tokenless", 0, tokenCounts: [])]);
        WriteSession(store, "sess-zero-tokens", [Turn("span-zt-1", "sess-zero-tokens", 0, inputTokens: 0, outputTokens: 0)]);
        // A session key attributed to two harnesses: the codex turn lands first so the session
        // row upserts to claude-code with the second write, and the figures must still count
        // both harnesses' turns — the per-session read the list replaced did.
        WriteSession(
            store,
            "sess-multi",
            [Turn("span-mu-cx", "sess-multi", 1, inputTokens: 200, outputTokens: 20, model: "gpt-5.1-codex", harnessId: "codex")],
            harnessId: "codex");
        WriteSession(store, "sess-multi", [Turn("span-mu-cc", "sess-multi", 0, inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-empty-list", []);
        WriteSession(
            store,
            "sess-list-content",
            [Turn("span-lc-1", "sess-list-content", 0, inputTokens: 400, outputTokens: 60, reportedCostUsd: 0.4,
                parts: [TextPart(0, "user_prompt", "content-bearing prompt text")])],
            repo: "kyber-weave",
            branch: "kw-task/T2");
        _ = CreateProjection(store, pricing).ProjectAll();

        IReadOnlyList<SessionListFigure> figures = store.SessionListFigures();
        string[] expectedKeys =
        [
            "sess-empty-list", "sess-list-content", "sess-multi", "sess-tokenless", "sess-zero-tokens",
        ];
        Assert.Equal(expectedKeys, figures.Select(static figure => figure.SessionKey).Order(StringComparer.Ordinal));

        foreach (SessionListFigure figure in figures)
        {
            SessionSummaryFigure? summary = store.SessionSummaryFigures(figure.SessionKey);
            Assert.NotNull(summary);
            AssertFigureMatchesSummary(figure, summary, store.GetSession(figure.SessionKey));
        }

        // The absent-versus-zero trap: a turn with no token rows leaves the totals absent, and a
        // turn with zero-valued rows leaves them zero; neither may leak into the other.
        SessionListFigure tokenless = Assert.Single(figures, static f => f.SessionKey == "sess-tokenless");
        Assert.Equal(1, tokenless.TurnCount);
        Assert.Null(tokenless.TotalInput);
        Assert.Null(tokenless.TotalOutput);
        SessionListFigure zeroed = Assert.Single(figures, static f => f.SessionKey == "sess-zero-tokens");
        Assert.Equal(0, zeroed.TotalInput);
        Assert.Equal(0, zeroed.TotalOutput);

        // The multi-harness session keeps every attributed harness's figures.
        SessionListFigure multi = Assert.Single(figures, static f => f.SessionKey == "sess-multi");
        Assert.Equal("claude-code", multi.HarnessId);
        Assert.Equal(2, multi.TurnCount);
        Assert.Equal(300, multi.TotalInput);
        Assert.Equal(["claude-sonnet-4-6", "gpt-5.1-codex"], multi.Models);

        // A harness filter narrows the session rows without thinning any session's figures.
        IReadOnlyList<SessionListFigure> claudeFigures = store.SessionListFigures("claude-code");
        Assert.Equal(expectedKeys, claudeFigures.Select(static figure => figure.SessionKey).Order(StringComparer.Ordinal));
        Assert.All(claudeFigures, static figure => Assert.Equal("claude-code", figure.HarnessId));
        SessionListFigure filteredMulti = Assert.Single(claudeFigures, static f => f.SessionKey == "sess-multi");
        Assert.Equal(2, filteredMulti.TurnCount);
        Assert.Equal(300, filteredMulti.TotalInput);
        Assert.Empty(store.SessionListFigures("codex"));
    }

    /// <summary>
    /// The sessions list answers from the content-free read model: during
    /// <see cref="KyberBridgeEngine.GetSessionPage"/> the instrumented fake records zero
    /// content-bearing reads and zero materialized content bytes, and the harness filter (set
    /// and unset), the empty store, and the limit keep answering the same session rows.
    /// </summary>
    [Fact]
    public void TheSessionsListNeverIssuesAContentRead()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-list-cc",
            [Turn("span-lcc-1", "sess-list-cc", 0, inputTokens: 400, outputTokens: 60,
                parts: [TextPart(0, "user_prompt", "content-bearing prompt text")])],
            repo: "kyber-weave",
            branch: "kw-task/T2");
        WriteSession(
            store,
            "sess-list-cx",
            [Turn("span-lcx-1", "sess-list-cx", 1, inputTokens: 100, outputTokens: 10, harnessId: "codex")],
            harnessId: "codex");
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        string[] contentBearingReads =
        [
            nameof(InMemoryRelationalStore.SessionRecordsForRebuild),
            nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance),
            nameof(InMemoryRelationalStore.ToolCalls),
            nameof(InMemoryRelationalStore.ToolDefinitions),
            nameof(InMemoryRelationalStore.AgentInvocations),
            nameof(InMemoryRelationalStore.SkillsInvoked),
        ];

        store.ClearReadLog();
        JsonElement unfiltered = bridge.GetSessionPage().GetProperty("sessions");
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => contentBearingReads.Contains(read));
        Assert.Equal(
            ["sess-list-cx", "sess-list-cc"],
            unfiltered.EnumerateArray().Select(static node => node.GetProperty("session_id").GetString()).ToList());
        JsonElement ccNode = Assert.Single(
            unfiltered.EnumerateArray(),
            static node => node.GetProperty("session_id").GetString() == "sess-list-cc");
        Assert.Equal("claude-code", ccNode.GetProperty("harness").GetString());
        Assert.Equal("kyber-weave", ccNode.GetProperty("repo").GetString());
        Assert.Equal(1, ccNode.GetProperty("turn_count").GetInt32());
        Assert.Equal(400, ccNode.GetProperty("total_input").GetInt64());
        Assert.False(ccNode.TryGetProperty("projection_status", out _));

        store.ClearReadLog();
        JsonElement filtered = bridge.GetSessionPage(harness: "codex").GetProperty("sessions");
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => contentBearingReads.Contains(read));
        Assert.Equal(
            ["sess-list-cx"],
            filtered.EnumerateArray().Select(static node => node.GetProperty("session_id").GetString()).ToList());

        store.ClearReadLog();
        JsonElement limited = bridge.GetSessionPage(limit: 1).GetProperty("sessions");
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => contentBearingReads.Contains(read));
        Assert.Equal(
            ["sess-list-cx"],
            limited.EnumerateArray().Select(static node => node.GetProperty("session_id").GetString()).ToList());

        InMemoryRelationalStore emptyStore = new();
        KyberBridgeEngine emptyBridge = CreateBridge(emptyStore, pricing: new FakePricingRepository());
        emptyStore.ClearReadLog();
        JsonElement none = emptyBridge.GetSessionPage().GetProperty("sessions");
        Assert.Equal(0, emptyStore.MaterializedContentBytes);
        Assert.DoesNotContain(emptyStore.ReadLog, read => contentBearingReads.Contains(read));
        Assert.Equal(0, none.GetArrayLength());
    }

    /// <summary>
    /// Selecting a large session answers its per-turn figures from the content-free read model:
    /// the turn figure set carries the same per-turn summaries the detail payload used to pay
    /// for, and reading it materializes no content, so a session with many large turns never
    /// pulls its text out of the store.
    /// </summary>
    [Fact]
    public void SessionTurnFiguresAnswerTheTurnListWithoutContentReads()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-detail-many";
        WriteSession(
            store,
            sessionKey,
            [.. Enumerable.Range(0, 128).Select(static i => Turn(
                $"turn-many-{i:D3}",
                sessionKey,
                i,
                inputTokens: 1_000 + i,
                cacheRead: 200 + i,
                outputTokens: 100 + i,
                model: i % 2 == 0 ? "claude-sonnet-4-6" : "gpt-5.1-codex",
                reportedCostUsd: 0.01,
                parts: [TextPart(0, "user_prompt", new string('x', 8_192))]))]);
        _ = CreateProjection(store, pricing).ProjectAll();

        IModelPricingLookup lookup = pricing.LoadPricingTables();

        store.ClearReadLog();
        SessionTurnFigureSet turnSet = store.SessionTurnFigures(sessionKey, lookup)!;

        Assert.Equal(128, turnSet.Turns.Count);
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        Assert.Equal([nameof(InMemoryRelationalStore.SessionTurnFigures)], store.ReadLog);

        // The turn set and the single-turn read answer the same figure for the same index.
        Assert.Equivalent(turnSet.Turns[0], store.TurnFigures(sessionKey, 0, lookup), strict: true);
        Assert.Equivalent(turnSet.Turns[127], store.TurnFigures(sessionKey, 127, lookup), strict: true);
        Assert.Null(store.SessionTurnFigures("missing", lookup));

        // The summary figures answer standalone, and the section pages project them: the summary
        // section carries the totals and the charts section the turn list, each from its own
        // content-free read.
        store.ClearReadLog();
        SessionSummaryFigure summary = store.SessionSummaryFigures(sessionKey, lookup)!;

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal([nameof(InMemoryRelationalStore.SessionSummaryFigures)], store.ReadLog);

        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        JsonElement summaryPayload = bridge.GetSessionSummary(sessionKey)!.Value;
        Assert.Equal(summary.TurnCount, summaryPayload.GetProperty("summary").GetProperty("turn_count").GetInt32());
        Assert.Equal(summary.TotalInput, summaryPayload.GetProperty("summary").GetProperty("total_input").GetInt64());
        Assert.Equal(summary.TotalOutput, summaryPayload.GetProperty("summary").GetProperty("total_output").GetInt64());
        Assert.Equal(summary.DurationMs, summaryPayload.GetProperty("summary").GetProperty("duration_ms").GetInt64());
        Assert.Equal(summary.Problems.Count, summaryPayload.GetProperty("problems_count").GetInt32());
        JsonElement chartsPayload = bridge.GetSessionCharts(sessionKey)!.Value;
        Assert.Equal(turnSet.Turns.Count, chartsPayload.GetProperty("turns").GetArrayLength());
        Assert.Equal(
            turnSet.Turns[1].SpanId,
            bridge.GetTurnFigures(sessionKey, 1)!.Value.GetProperty("turn").GetProperty("spanId").GetString());
        Assert.Null(bridge.GetTurnFigures(sessionKey, turnSet.Turns.Count));
    }

    /// <summary>
    /// The bounded summary page answers from the content-free summary read model alone: no turn
    /// summary is read, no content is materialized, and the summary section payload carries the
    /// same totals the figures hold.
    /// </summary>
    [Fact]
    public void SessionSummaryAnswersWithoutTurnOrContentReads()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-summary-many";
        WriteSession(
            store,
            sessionKey,
            [.. Enumerable.Range(0, 128).Select(static i => Turn(
                $"turn-summary-{i:D3}",
                sessionKey,
                i,
                inputTokens: 800 + i,
                cacheRead: 50 + i,
                outputTokens: 40 + i,
                reportedCostUsd: 0.02,
                parts: [TextPart(0, "tool_result_content", new string('y', 8_192))]))]);
        _ = CreateProjection(store, pricing).ProjectAll();

        IModelPricingLookup lookup = pricing.LoadPricingTables();

        store.ClearReadLog();
        SessionSummaryFigure summary = store.SessionSummaryFigures(sessionKey, lookup)!;

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        Assert.Equal([nameof(InMemoryRelationalStore.SessionSummaryFigures)], store.ReadLog);
        Assert.Null(store.SessionSummaryFigures("missing", lookup));

        // The summary section payload answers from that same read model — the bounded header
        // the detail route used to serve, from the summary figures alone.
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        JsonElement payload = bridge.GetSessionSummary(sessionKey)!.Value;
        Assert.Equal(summary.TurnCount, payload.GetProperty("summary").GetProperty("turn_count").GetInt32());
        Assert.Equal(summary.TotalInput, payload.GetProperty("summary").GetProperty("total_input").GetInt64());
        Assert.Equal(summary.TotalOutput, payload.GetProperty("summary").GetProperty("total_output").GetInt64());
    }

    /// <summary>
    /// One turn's figures answer scoped to its index — the same figure the turn list carries —
    /// and an index outside the session answers nothing without reading content.
    /// </summary>
    [Fact]
    public void TurnFiguresAnswerOneTurnScopedToItsIndex()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-turn-figures";
        WriteSession(
            store,
            sessionKey,
            [
                Turn("turn-fig-0", sessionKey, 0, inputTokens: 100, outputTokens: 10, parts: [TextPart(0, "user_prompt", "first turn text")]),
                Turn("turn-fig-1", sessionKey, 1, inputTokens: 200, outputTokens: 20, parts: [TextPart(0, "user_prompt", "second turn text")]),
                Turn("turn-fig-2", sessionKey, 2, inputTokens: 300, outputTokens: 30, parts: [TextPart(0, "user_prompt", "third turn text")]),
            ]);
        _ = CreateProjection(store, pricing).ProjectAll();

        IModelPricingLookup lookup = pricing.LoadPricingTables();

        store.ClearReadLog();
        ReadTimeTurnSummary second = store.TurnFigures(sessionKey, 1, lookup)!;

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        Assert.Equal(1, second.Position);
        Assert.Equal("turn-fig-1", second.SpanId);
        Assert.Equal(200, second.InputTokens);
        Assert.Equal(20, second.OutputTokens);
        Assert.Null(store.TurnFigures(sessionKey, 3, lookup));
        Assert.Null(store.TurnFigures(sessionKey, -1, lookup));
        Assert.Null(store.TurnFigures("missing", 0, lookup));
    }

    /// <summary>
    /// The session sections answer without materializing content: the summary, charts, and tools
    /// reads touch no content-bearing column and no content bytes for a session with many large
    /// turns, while the payloads still carry their summary, turn, tool, and request figures. The
    /// requests section's prompt previews ride the page-scoped preview read, bounded at 200
    /// characters even when each turn carries four kilobytes of text.
    /// </summary>
    [Fact]
    public void SectionRoutesAnswerWithoutContentReads()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-detail-page";
        List<CollectedRecord> turns =
        [
            .. Enumerable.Range(0, 128).Select(static i => Turn(
                $"turn-page-{i:D3}",
                sessionKey,
                i,
                inputTokens: 1_000 + i,
                cacheRead: 200 + i,
                outputTokens: 100 + i,
                model: i % 2 == 0 ? "claude-sonnet-4-6" : "gpt-5.1-codex",
                reportedCostUsd: 0.01,
                parts: [TextPart(0, "user_prompt", new string('x', 8_192))])),
        ];
        turns[0] = WithToolCall(turns[0]);
        WriteSession(store, sessionKey, turns);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        store.ClearReadLog();
        JsonElement summary = bridge.GetSessionSummary(sessionKey)!.Value;
        JsonElement charts = bridge.GetSessionCharts(sessionKey)!.Value;
        JsonElement tools = bridge.GetSessionTools(sessionKey)!.Value;

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        Assert.Equal(128, charts.GetProperty("turns").GetArrayLength());
        Assert.Equal(128, summary.GetProperty("summary").GetProperty("turn_count").GetInt32());
        Assert.True(summary.GetProperty("summary").GetProperty("total_input").GetInt64() > 0);
        Assert.Equal(0, summary.GetProperty("problems_count").GetInt32());
        JsonElement tool = Assert.Single(tools.GetProperty("tools").EnumerateArray());
        Assert.Equal("read", tool.GetProperty("name").GetString());
        Assert.Equal(1, tool.GetProperty("invocations").GetInt32());
        Assert.False(summary.TryGetProperty("projection_status", out _));

        // The requests section answers through the same content-free path plus its one scoped
        // preview read: every row's prompt is the page's own 200-character preview, never the
        // session's conversation.
        store.ClearReadLog();
        JsonElement requests = bridge.GetSessionRequests(sessionKey)!.Value;
        Assert.NotEmpty(requests.GetProperty("requests").EnumerateArray());
        Assert.Contains(nameof(InMemoryRelationalStore.RequestPromptPreviews), store.ReadLog);
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        Assert.All(
            requests.GetProperty("requests").EnumerateArray(),
            static row => Assert.Equal(200, row.GetProperty("prompt").GetString()!.Length));
    }

    /// <summary>Attaches one tool invocation and one tool definition, both metadata only.</summary>
    private static CollectedRecord WithToolCall(CollectedRecord record) =>
        record with
        {
            Tool = new CollectedToolCall(
                CallId: "call-detail-page",
                ToolName: "read",
                McpServerName: null,
                McpToolName: null,
                Input: new CollectedToolContent("tool input text", 15, 15, null, null),
                Result: new CollectedToolContent("tool result text", 16, 16, null, null),
                Succeeded: true,
                Error: null,
                ExitCode: 0),
            ToolDefinitions = [new CollectedToolDefinition(0, "read", null, 120)],
        };

    /// <summary>Reader methods that materialize conversation text, so a path may not issue them.</summary>
    private static readonly string[] ContentBearingReads =
    [
        nameof(InMemoryRelationalStore.SessionRecordsForRebuild),
        nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance),
        nameof(InMemoryRelationalStore.ToolCalls),
        nameof(InMemoryRelationalStore.ToolDefinitions),
        nameof(InMemoryRelationalStore.AgentInvocations),
        nameof(InMemoryRelationalStore.SkillsInvoked),
    ];

    /// <summary>Cross-checks the sessions-list figure against the per-session summary figure over every field both carry.</summary>
    private static void AssertFigureMatchesSummary(
        SessionListFigure figure,
        SessionSummaryFigure summary,
        CollectedSession? session)
    {
        Assert.Equal(summary.SessionKey, figure.SessionKey);
        Assert.Equal(summary.HarnessId, figure.HarnessId);
        Assert.Equal(summary.TurnCount, figure.TurnCount);
        Assert.Equal(summary.RequestCount, figure.RequestCount);
        Assert.Equal(summary.TotalInput, figure.TotalInput);
        Assert.Equal(summary.TotalOutput, figure.TotalOutput);
        Assert.Equal(summary.Models, figure.Models);
        Assert.Equivalent(summary.ReportedCost, figure.ReportedCost, strict: true);
        Assert.Equivalent(summary.PublishedCost, figure.PublishedCost, strict: true);
        Assert.Equivalent(summary.CostBasisMismatch, figure.CostBasisMismatch, strict: true);
        Assert.Equal(summary.Problems.Count, figure.ProblemsCount);
        Assert.Equal(session?.Workspace?.Repository, figure.Repo);
        Assert.Equal(session?.Workspace?.Branch, figure.Branch);
    }

    /// <summary>Summary and charts sections, and span attributes, answer from typed rows with no store JSON.</summary>
    [Fact]
    public void SectionRoutesAnswerTypedSummaryTurnsAndSpanAttributes()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-detail",
            [Turn("turn-d1", "sess-detail", 0, inputTokens: 400, outputTokens: 60, reportedCostUsd: 0.4)],
            title: "detail session");
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        JsonElement summary = bridge.GetSessionSummary("sess-detail")!.Value;
        Assert.Equal("sess-detail", summary.GetProperty("session_id").GetString());
        Assert.Equal(0, summary.GetProperty("problems_count").GetInt32());
        Assert.Equal(400, summary.GetProperty("summary").GetProperty("total_input").GetInt64());
        Assert.True(summary.GetProperty("summary").TryGetProperty("coverage", out _));

        JsonElement charts = bridge.GetSessionCharts("sess-detail")!.Value;
        Assert.Equal("sess-detail", charts.GetProperty("sessionId").GetString());
        JsonElement turn = Assert.Single(charts.GetProperty("turns").EnumerateArray());
        Assert.Equal(0, turn.GetProperty("turnIndex").GetInt32());
        Assert.Equal("turn-d1", turn.GetProperty("spanId").GetString());
        Assert.StartsWith("2026-11-19T10:00:00", turn.GetProperty("timestamp").GetString(), StringComparison.Ordinal);
        Assert.Equal(400, turn.GetProperty("inputTokens").GetInt64());
        Assert.Equal(60, turn.GetProperty("outputTokens").GetInt64());
        Assert.Equal(460, turn.GetProperty("tokens").GetInt64());
        Assert.Equal("harness", turn.GetProperty("costReported").GetProperty("basis").GetString());

        JsonElement attributes = bridge.GetSpanAttributes("turn-d1")!.Value;
        Assert.Equal("turn-d1", attributes.GetProperty("spanId").GetString());
        Assert.Equal("llm.invoke", attributes.GetProperty("op").GetString());
        Assert.Equal("client", attributes.GetProperty("kind").GetString());
        Assert.Equal(400, attributes.GetProperty("tokens").GetProperty("freshInput").GetInt64());
        Assert.Equal("claude-sonnet-4-6", attributes.GetProperty("attributes").GetProperty("gen_ai.response.model").GetString());
    }

    /// <summary>
    /// Turn figures and bounded turn content answer from typed turn summaries. The bounded
    /// content payload's <c>truncated</c> flag counts scoped parts — the budgeted read leaving at
    /// least one part behind — while <c>content</c>/<c>turn</c> keep the legacy byte-budget slice
    /// of the serialized turn row.
    /// </summary>
    [Fact]
    public void GetTurnFiguresAndTurnContentAnswerBoundedTurnPayloads()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-turns",
            [
                Turn(
                    "turn-t1",
                    "sess-turns",
                    0,
                    inputTokens: 400,
                    outputTokens: 60,
                    parts:
                    [
                        TextPart(0, "user_prompt", new string('a', 80)),
                        TextPart(1, "tool_result_content", "hi"),
                    ]),
            ]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        JsonElement figures = bridge.GetTurnFigures("sess-turns", 0)!.Value;
        Assert.Equal("sess-turns", figures.GetProperty("sessionId").GetString());
        Assert.Equal(0, figures.GetProperty("turnIndex").GetInt32());
        Assert.Equal("turn-t1", figures.GetProperty("turn").GetProperty("spanId").GetString());
        Assert.Null(bridge.GetTurnFigures("sess-turns", 5));
        Assert.Null(bridge.GetTurnFigures("missing", 0));

        // The first part's 80 bytes cross the 64-byte budget, so the cut keeps that part alone
        // and leaves the second behind: `truncated` is the parts-count verdict, and the legacy
        // content slice of the serialized turn row still answers under its own byte budget.
        JsonElement bounded = bridge.GetTurnContent("sess-turns", 0, budget: 64, full: false)!.Value;
        Assert.True(bounded.GetProperty("truncated").GetBoolean());
        Assert.Equal(64, bounded.GetProperty("budget").GetInt32());
        Assert.True(bounded.GetProperty("totalBytes").GetInt64() > 64);
        Assert.False(string.IsNullOrEmpty(bounded.GetProperty("content").GetString()));
        Assert.Equal(JsonValueKind.Null, bounded.GetProperty("turn").ValueKind);
        Assert.Equal(
            ["user_prompt"],
            bounded.GetProperty("parts").EnumerateArray()
                .Select(static part => part.GetProperty("part").GetString()).ToList());

        // The lossless full read answers every part and never reports truncation.
        JsonElement full = bridge.GetTurnContent("sess-turns", 0, full: true)!.Value;
        Assert.False(full.GetProperty("truncated").GetBoolean());
        Assert.Equal("turn-t1", full.GetProperty("turn").GetProperty("spanId").GetString());
        Assert.Equal(2, full.GetProperty("parts").GetArrayLength());
    }

    /// <summary>Session content answers one flat entry per captured part and none for a partless span.</summary>
    [Fact]
    public void GetSessionContentAnswersFlatPartsPerSpanFromTypedParts()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, "sess-content",
            [
                Turn("span-empty", "sess-content", 0),
                Turn(
                    "span-parts",
                    "sess-content",
                    1,
                    parts:
                    [
                        TextPart(0, "user_prompt", "hello", reportedTokens: 3),
                        TextPart(1, "tool_result_content", "hi"),
                    ]),
            ]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        JsonElement none = bridge.GetSessionContent("sess-content", spanId: "span-empty", full: true)!.Value;
        Assert.Equal(0, none.GetProperty("parts").GetArrayLength());

        JsonElement flat = bridge.GetSessionContent("sess-content", spanId: "span-parts", full: true)!.Value;
        Assert.Equal(2, flat.GetProperty("parts").GetArrayLength());
        JsonElement prompt = flat.GetProperty("parts")[0];
        Assert.Equal("span-parts", prompt.GetProperty("spanId").GetString());
        Assert.Equal("user_prompt", prompt.GetProperty("part").GetString());
        Assert.Equal("hello", prompt.GetProperty("text").GetString());
        Assert.Equal(3, prompt.GetProperty("tokens").GetInt64());

        JsonElement filtered = bridge.GetSessionContent("sess-content", spanId: "span-parts", part: "tool_result_content", full: true)!.Value;
        JsonElement entry = Assert.Single(filtered.GetProperty("parts").EnumerateArray());
        Assert.Equal("tool_result_content", entry.GetProperty("part").GetString());
        Assert.Equal("hi", entry.GetProperty("text").GetString());

        Assert.Null(bridge.GetSessionContent("missing", full: true));
    }

    /// <summary>
    /// The content view is the one request path that reads content text, and it reads only the
    /// turn, span, and part it was asked for: the scoped read answers that scope, honours the
    /// budget and the full flag, and both content routes reach it without falling back to a broad
    /// record or part read.
    /// </summary>
    [Fact]
    public void TurnContentReadsOnlyTheRequestedTurnsParts()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-content-scope";
        WriteSession(
            store,
            sessionKey,
            [
                Turn("span-one", sessionKey, 0, inputTokens: 100, parts: [TextPart(0, "user_prompt", "first turn prompt")]),
                Turn(
                    "span-two",
                    sessionKey,
                    1,
                    inputTokens: 200,
                    parts:
                    [
                        TextPart(0, "user_prompt", "second turn prompt"),
                        TextPart(1, "tool_result_content", "second turn tool result"),
                    ]),
                Turn("span-three", sessionKey, 2, inputTokens: 300, parts: [TextPart(0, "user_prompt", "third turn prompt")]),
            ]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        IReadOnlyList<StoredContentPart> secondTurn = store.TurnContentParts(sessionKey, turnIndex: 1);
        Assert.Equal(
            ["second turn prompt", "second turn tool result"],
            secondTurn.Select(static part => part.Part.PlainText).ToList());
        Assert.All(secondTurn, part => Assert.Equal("span-two", part.SpanId));
        Assert.Equal(41, store.MaterializedContentBytes);

        store.ClearReadLog();
        StoredContentPart resultPart = Assert.Single(store.TurnContentParts(sessionKey, spanId: "span-two", part: "tool_result_content"));
        Assert.Equal("span-two", resultPart.SpanId);
        Assert.Equal("second turn tool result", resultPart.Part.PlainText);
        Assert.Equal(23, store.MaterializedContentBytes);

        store.ClearReadLog();
        StoredContentPart bounded = Assert.Single(store.TurnContentParts(sessionKey, turnIndex: 1, budget: 10));
        Assert.Equal("second turn prompt", bounded.Part.PlainText);
        Assert.Equal(18, store.MaterializedContentBytes);

        store.ClearReadLog();
        Assert.Equal(2, store.TurnContentParts(sessionKey, turnIndex: 1, budget: 10, full: true).Count);
        Assert.Equal(41, store.MaterializedContentBytes);

        Assert.Empty(store.TurnContentParts(sessionKey, turnIndex: 9));
        Assert.Empty(store.TurnContentParts("sess-absent", turnIndex: 0));

        store.ClearReadLog();
        JsonElement scoped = bridge.GetSessionContent(sessionKey, spanId: "span-two", full: true)!.Value;
        Assert.Equal(
            ["span-two", "span-two"],
            scoped.GetProperty("parts").EnumerateArray().Select(static part => part.GetProperty("spanId").GetString()).ToList());
        Assert.Equal(41, store.MaterializedContentBytes);
        Assert.Contains(nameof(InMemoryRelationalStore.TurnContentParts), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.SessionRecordsForRebuild), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance), store.ReadLog);

        store.ClearReadLog();
        JsonElement turnContent = bridge.GetTurnContent(sessionKey, 1, full: true)!.Value;
        Assert.Equal(
            ["user_prompt", "tool_result_content"],
            turnContent.GetProperty("parts").EnumerateArray().Select(static part => part.GetProperty("part").GetString()).ToList());
        Assert.Equal(41, store.MaterializedContentBytes);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.SessionRecordsForRebuild), store.ReadLog);
    }

    /// <summary>
    /// Every request path except the content view and the two scoped prompt reads leaves content
    /// unread. The sweep calls the list, summary, charts, turn-figure, span-attribute, timeline,
    /// coverage, findings, problems, quarantine, harness, run, report, and analytics paths over
    /// the instrumented fake against a session whose turns carry four kilobytes of text each,
    /// and each path records zero materialized content bytes and no content-bearing read.
    /// </summary>
    [Fact]
    public void EveryOtherPathLeavesContentUnread()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-sweep";
        WriteSession(
            store,
            sessionKey,
            [
                Turn(
                    "span-sweep-1",
                    sessionKey,
                    0,
                    inputTokens: 400,
                    cacheRead: 100,
                    outputTokens: 60,
                    reportedCostUsd: 0.01,
                    parts: [TextPart(0, "user_prompt", new string('p', 4_096))],
                    declaredContextWindow: 2_000),
                Turn(
                    "span-sweep-2",
                    sessionKey,
                    1,
                    inputTokens: 1_800,
                    outputTokens: 120,
                    parts: [TextPart(0, "tool_result_content", new string('r', 4_096))],
                    declaredContextWindow: 2_000),
            ],
            repo: "kyber-weave",
            branch: "kw-task/T4",
            title: "sweep session");
        WriteSession(
            store,
            "sess-sweep-droid",
            [
                Turn(
                    "span-sweep-droid",
                    "sess-sweep-droid",
                    0,
                    harnessId: "droid",
                    parts: [WithheldPart(0, "user_prompt")]),
            ],
            harnessId: "droid");
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        string[] contentBearingReads =
        [
            nameof(InMemoryRelationalStore.SessionRecordsForRebuild),
            nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance),
            nameof(InMemoryRelationalStore.ToolCalls),
            nameof(InMemoryRelationalStore.ToolDefinitions),
            nameof(InMemoryRelationalStore.AgentInvocations),
            nameof(InMemoryRelationalStore.SkillsInvoked),
        ];

        (string Path, Action Call)[] paths =
        [
            ("sessions list", () => _ = bridge.GetSessionPage()),
            ("sessions list filtered", () => _ = bridge.GetSessionPage(harness: "claude-code")),
            ("session summary", () => _ = bridge.GetSessionSummary(sessionKey)),
            ("session charts", () => _ = bridge.GetSessionCharts(sessionKey)),
            ("session reconciliation", () => _ = bridge.GetSessionReconciliation(sessionKey)),
            ("session problems", () => _ = bridge.GetSessionProblems(sessionKey)),
            ("session requests (droid consent withheld)", () => _ = bridge.GetSessionRequests("sess-sweep-droid")),
            ("session timeline", () => _ = bridge.GetSessionTimeline(sessionKey)),
            ("session timeline level", () => _ = bridge.GetSessionTimeline(sessionKey, "(auxiliary)")),
            ("turn figures", () => _ = bridge.GetTurnFigures(sessionKey, 0)),
            ("span attributes", () => _ = bridge.GetSpanAttributes("span-sweep-1")),
            ("span attributes (droid consent withheld)", () => _ = bridge.GetSpanAttributes("span-sweep-droid")),
            ("timeline", () => _ = bridge.GetTimeline(sessionKey)),
            ("coverage", () => _ = bridge.GetCoverage()),
            ("findings", () => _ = bridge.GetFindings(sessionId: sessionKey)),
            ("problems", () => _ = bridge.GetProblemsPage(null, 10, 0, 1)),
            ("quarantine", () => _ = bridge.GetQuarantinePage(null, 10, 0, 1)),
            ("harnesses", () => _ = bridge.GetHarnesses()),
            ("runs", () => _ = bridge.GetRuns()),
            ("run detail", () => _ = bridge.GetRunById("derived:claude-code:sess-sweep")),
            ("harness detail", () => _ = bridge.GetHarnessById("claude-code")),
            ("compare", () => _ = bridge.GetCompare()),
            ("compare runs", () => _ = bridge.GetCompareRuns("derived:claude-code:sess-sweep", "derived:claude-code:sess-sweep")),
            ("report", () => _ = bridge.GetReport()),
            ("analytics status", () => _ = bridge.ExecuteAnalytics("status", null)),
            ("analytics catalog", () => _ = bridge.ExecuteAnalytics("catalog", null)),
            ("analytics query", () => _ = bridge.ExecuteAnalytics("query", null)),
            ("analytics outcomes", () => _ = bridge.ExecuteAnalytics("outcomes", null)),
            ("analytics findings", () => _ = bridge.ExecuteAnalytics("findings", null)),
        ];

        Dictionary<string, List<string>> readsByPath = new(StringComparer.Ordinal);
        foreach ((string path, Action call) in paths)
        {
            store.ClearReadLog();
            call();
            readsByPath[path] = [.. store.ReadLog];
            Assert.True(
                store.MaterializedContentBytes == 0,
                $"the {path} path materialized {store.MaterializedContentBytes} content bytes");
            Assert.DoesNotContain(store.ReadLog, read => contentBearingReads.Contains(read));
        }

        // The scoped content reads answer only the path that displays them: the span inspector's
        // prompt read on the span-attributes path, and the requests section's prompt previews on
        // the requests path. Both droid spans' prompts were withheld for consent, so the sweep
        // still materializes nothing while recording the reads.
        Assert.Equal(
            ["session requests (droid consent withheld)", "span attributes (droid consent withheld)"],
            readsByPath
                .Where(static entry => entry.Value.Contains(nameof(InMemoryRelationalStore.SpanPromptText), StringComparer.Ordinal)
                    || entry.Value.Contains(nameof(InMemoryRelationalStore.RequestPromptPreviews), StringComparer.Ordinal))
                .Select(static entry => entry.Key)
                .ToList());
    }

    /// <summary>
    /// The run scorecard and the run comparison price session totals and tool names only, so both
    /// answer from the content-free summary figures: the run detail page and the compare view
    /// materialize no conversation text for a session whose turns carry kilobytes each, and the
    /// scorecard still reports the session's tool yield and cache efficiency.
    /// </summary>
    [Fact]
    public void RunDetailAndCompareAnswerWithoutContentReads()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        List<CollectedRecord> runTurns =
        [
            Turn(
                "span-run-a1",
                "sess-run-a",
                0,
                inputTokens: 400,
                cacheRead: 100,
                outputTokens: 60,
                parts: [TextPart(0, "user_prompt", new string('a', 4_096))],
                declaredContextWindow: 2_000),
        ];
        runTurns[0] = WithToolCall(runTurns[0]);
        WriteSession(store, "sess-run-a", runTurns, repo: "kyber-weave", branch: "kw-task/T6");
        WriteSession(
            store,
            "sess-run-b",
            [
                Turn(
                    "span-run-b1",
                    "sess-run-b",
                    1,
                    inputTokens: 1_800,
                    outputTokens: 120,
                    parts: [TextPart(0, "tool_result_content", new string('b', 4_096))],
                    declaredContextWindow: 2_000),
            ]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        const string RunA = "derived:claude-code:sess-run-a";
        const string RunB = "derived:claude-code:sess-run-b";

        store.ClearReadLog();
        JsonElement detail = bridge.GetRunById(RunA)!.Value;
        Assert.True(
            store.MaterializedContentBytes == 0,
            $"the run detail path materialized {store.MaterializedContentBytes} content bytes");
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        JsonElement run = detail.GetProperty("run");
        Assert.Equal(500, run.GetProperty("totalInput").GetInt64());
        Assert.Equal(60, run.GetProperty("totalOutput").GetInt64());
        Assert.Equal(1, run.GetProperty("turnCount").GetInt32());
        JsonElement scorecard = detail.GetProperty("scorecard");
        Assert.Equal("1.0x", scorecard.GetProperty("toolYield").GetProperty("display").GetString());
        Assert.Equal("20%", scorecard.GetProperty("cacheEfficiency").GetProperty("display").GetString());

        store.ClearReadLog();
        JsonElement comparison = bridge.GetCompareRuns(RunA, RunB)!.Value;
        Assert.True(
            store.MaterializedContentBytes == 0,
            $"the compare path materialized {store.MaterializedContentBytes} content bytes");
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        // The comparison's run sides are the documented contract (issue #190): measured sides
        // carry the canonical token total, never a per-class split the page would have to sum.
        Assert.Equal("measured", comparison.GetProperty("runA").GetProperty("availability").GetString());
        Assert.Equal(560, comparison.GetProperty("runA").GetProperty("totalTokens").GetInt64());
        Assert.Equal(1, comparison.GetProperty("runA").GetProperty("turnCount").GetInt32());
        Assert.Equal("measured", comparison.GetProperty("runB").GetProperty("availability").GetString());
        Assert.Equal(1_920, comparison.GetProperty("runB").GetProperty("totalTokens").GetInt64());
        Assert.Equal(1_360, comparison.GetProperty("totals").GetProperty("tokenDelta").GetInt64());
        JsonElement pair = Assert.Single(comparison.GetProperty("pairs").EnumerateArray());
        Assert.Equal(560, pair.GetProperty("runATurn").GetProperty("tokens").GetProperty("all").GetInt64());
        Assert.Equal(1_920, pair.GetProperty("runBTurn").GetProperty("tokens").GetProperty("all").GetInt64());
    }

    /// <summary>
    /// The span inspector answers one span's metadata from the single content-free record read and
    /// never issues a session-wide read, so opening the panel does not pay for the conversation.
    /// </summary>
    [Fact]
    public void SpanAttributesAnswerOneSpansMetadataWithoutSessionReads()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-span-a",
            [
                Turn(
                    "span-a1",
                    "sess-span-a",
                    0,
                    inputTokens: 400,
                    outputTokens: 60,
                    parts: [TextPart(0, "user_prompt", new string('p', 4_096))],
                    declaredContextWindow: 2_000),
            ]);
        WriteSession(
            store,
            "sess-span-b",
            [Turn("span-b1", "sess-span-b", 0, inputTokens: 100, parts: [TextPart(0, "user_prompt", "other session text")])]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        JsonElement attributes = bridge.GetSpanAttributes("span-a1")!.Value;

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal([nameof(InMemoryRelationalStore.RecordContentFree)], store.ReadLog);
        Assert.Equal("span-a1", attributes.GetProperty("spanId").GetString());
        Assert.Equal("sess-span-a", attributes.GetProperty("sessionId").GetString());
        Assert.Equal("claude-code", attributes.GetProperty("harness").GetString());
        Assert.Equal(400, attributes.GetProperty("tokens").GetProperty("freshInput").GetInt64());
        Assert.Equal("claude-sonnet-4-6", attributes.GetProperty("attributes").GetProperty("gen_ai.response.model").GetString());
        Assert.DoesNotContain("other session text", attributes.GetRawText(), StringComparison.Ordinal);
        Assert.Null(bridge.GetSpanAttributes("no-such-span"));
    }

    /// <summary>
    /// The span inspector carries the consent-allowed Droid prompt: a Droid span with a
    /// captured prompt part answers it under the captured-prompt attribute key, skipping a
    /// blank prompt and a non-prompt part for the first prompt text in position order. A
    /// Droid span whose prompt was withheld for consent carries none, and a span from
    /// another harness issues no prompt read at all. The read log shows the content-free
    /// record read and the span-scoped prompt read for that span only, never a
    /// session-wide read.
    /// </summary>
    [Fact]
    public void SpanAttributesCarryTheConsentAllowedDroidPrompt()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-droid",
            [
                Turn(
                    "span-droid-1",
                    "sess-droid",
                    0,
                    harnessId: "droid",
                    parts:
                    [
                        TextPart(0, "tool_result_content", "tool noise"),
                        TextPart(1, "user_prompt", "   "),
                        TextPart(2, "user_message", "hello droid"),
                    ]),
                Turn(
                    "span-droid-off",
                    "sess-droid",
                    1,
                    harnessId: "droid",
                    parts: [WithheldPart(0, "user_prompt")]),
            ],
            harnessId: "droid");
        WriteSession(
            store,
            "sess-other",
            [Turn("span-other-1", "sess-other", 0, parts: [TextPart(0, "user_prompt", "other harness prompt")])]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        JsonElement attributes = bridge.GetSpanAttributes("span-droid-1")!.Value;

        Assert.Equal("hello droid", attributes.GetProperty("attributes").GetProperty(FactoryKeys.PromptText).GetString());
        Assert.Equal(11, store.MaterializedContentBytes);
        Assert.Equal(
            [nameof(InMemoryRelationalStore.RecordContentFree), nameof(InMemoryRelationalStore.SpanPromptText)],
            store.ReadLog);

        store.ClearReadLog();
        JsonElement withheld = bridge.GetSpanAttributes("span-droid-off")!.Value;

        Assert.False(withheld.GetProperty("attributes").TryGetProperty(FactoryKeys.PromptText, out _));
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal(
            [nameof(InMemoryRelationalStore.RecordContentFree), nameof(InMemoryRelationalStore.SpanPromptText)],
            store.ReadLog);

        store.ClearReadLog();
        JsonElement other = bridge.GetSpanAttributes("span-other-1")!.Value;

        Assert.False(other.GetProperty("attributes").TryGetProperty(FactoryKeys.PromptText, out _));
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal([nameof(InMemoryRelationalStore.RecordContentFree)], store.ReadLog);

        Assert.Null(bridge.GetSpanAttributes("no-such-span"));
    }

    /// <summary>
    /// The timeline answers from the content-free record read for the named session: it never
    /// materializes a conversation and reads no other session's records.
    /// </summary>
    [Fact]
    public void TimelineAnswersFromContentFreeRecords()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-timeline";
        WriteSession(
            store,
            sessionKey,
            [
                Turn(
                    "span-tl-1",
                    sessionKey,
                    0,
                    inputTokens: 400,
                    parts: [TextPart(0, "user_prompt", new string('p', 4_096))],
                    declaredContextWindow: 2_000),
                Turn(
                    "span-tl-2",
                    sessionKey,
                    1,
                    inputTokens: 1_800,
                    outputTokens: 120,
                    parts: [TextPart(0, "tool_result_content", new string('r', 4_096))],
                    declaredContextWindow: 2_000),
            ]);
        WriteSession(store, "sess-timeline-other", [Turn("span-tl-3", "sess-timeline-other", 0, inputTokens: 50)]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        JsonElement timeline = bridge.GetTimeline(sessionKey);

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal(
            [
                nameof(InMemoryRelationalStore.CountRecords),
                nameof(InMemoryRelationalStore.GetSession),
                nameof(InMemoryRelationalStore.SessionRecordsContentFree),
            ],
            store.ReadLog);
        JsonElement session = Assert.Single(timeline.GetProperty("sessions").EnumerateArray());
        Assert.Equal(sessionKey, session.GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.Object, session.GetProperty("timeline").ValueKind);
        Assert.DoesNotContain("span-tl-3", timeline.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The stored-fields inventory is the section's mount read and it is content-free: it answers
    /// every catalog table's name, columns, scoped row count and missing parent keys for the named
    /// session, and never a row value — the rows load per table only when the viewer opens one
    /// (decision D15).
    /// </summary>
    [Fact]
    public void StoredFieldsInventoryStaysScopedToTheNamedSessionAndCarriesNoRowValues()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-fields-a",
            [Turn("span-fa", "sess-fields-a", 0, parts: [TextPart(0, "user_prompt", "alpha conversation text")])],
            title: "alpha session");
        WriteSession(
            store,
            "sess-fields-b",
            [Turn("span-fb", "sess-fields-b", 0, parts: [TextPart(0, "user_prompt", "beta conversation text")])],
            title: "beta session");
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        JsonElement view = bridge.GetStoredFieldsInventory("sess-fields-a")!.Value;

        Assert.Equal(
            [
                nameof(InMemoryRelationalStore.CountRecords),
                nameof(InMemoryRelationalStore.GetSession),
                nameof(InMemoryRelationalStore.StoredFieldsInventory),
            ],
            store.ReadLog);
        Assert.Equal("sess-fields-a", view.GetProperty("sessionKey").GetString());

        // No row values: neither the part text nor the session title row value appears. The
        // missing parent keys are parent key identifiers, not content.
        string raw = view.GetRawText();
        Assert.DoesNotContain("alpha conversation text", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha session", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("beta conversation text", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("sess-fields-b", raw, StringComparison.Ordinal);

        JsonElement sessionEntry = Assert.Single(
            view.GetProperty("tables").EnumerateArray(),
            static entry => string.Equals(entry.GetProperty("tableName").GetString(), "session", StringComparison.Ordinal));
        Assert.Equal(1, sessionEntry.GetProperty("rowCount").GetInt64());

        // An optional child whose row is absent reports it through the missing parent key.
        JsonElement withheldEntry = Assert.Single(
            view.GetProperty("tables").EnumerateArray(),
            static entry => string.Equals(entry.GetProperty("tableName").GetString(), "content_part_withheld", StringComparison.Ordinal));
        Assert.Equal(0, withheldEntry.GetProperty("rowCount").GetInt64());
        // The part tables' foreign key is (span_id, position), so a missing parent key is the
        // composite the parent content_part row carries.
        Assert.Equal("span-fa:0", withheldEntry.GetProperty("missingParentKeys")[0].GetString());

        Assert.Null(bridge.GetStoredFieldsInventory("no-such-session"));
    }

    /// <summary>
    /// A table's rows load on demand over the rows route, scoped to the named session: the page
    /// carries that table's row values and no other session's, and an unknown or non-catalog
    /// table reads as absent (decision D15).
    /// </summary>
    [Fact]
    public void StoredFieldsRowsReturnOnePageOfTheNamedTableScopedToTheSession()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-fields-a",
            [Turn("span-fa", "sess-fields-a", 0, parts: [TextPart(0, "user_prompt", "alpha conversation text")])],
            title: "alpha session");
        WriteSession(
            store,
            "sess-fields-b",
            [Turn("span-fb", "sess-fields-b", 0, parts: [TextPart(0, "user_prompt", "beta conversation text")])],
            title: "beta session");
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        JsonElement page = bridge.GetStoredFieldRows("sess-fields-a", "content_part_text", 1, 25)!.Value;

        Assert.Equal(
            [
                nameof(InMemoryRelationalStore.CountRecords),
                nameof(InMemoryRelationalStore.GetSession),
                nameof(InMemoryRelationalStore.StoredFieldsRows),
            ],
            store.ReadLog);
        Assert.Equal("content_part_text", page.GetProperty("tableName").GetString());
        Assert.Equal(1, page.GetProperty("totalRows").GetInt64());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(25, page.GetProperty("limit").GetInt32());
        Assert.Equal(0, page.GetProperty("offset").GetInt32());

        string raw = page.GetRawText();
        Assert.Contains("alpha conversation text", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("beta conversation text", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("sess-fields-b", raw, StringComparison.Ordinal);

        Assert.Null(bridge.GetStoredFieldRows("sess-fields-a", "not_a_catalog_table", 1, 25));
        Assert.Null(bridge.GetStoredFieldRows("no-such-session", "content_part_text", 1, 25));
    }

    /// <summary>A table's rows answer one page of 25 at a time, whatever the table holds.</summary>
    [Fact]
    public void StoredFieldsRowsPageTwentyFiveRowsAtATime()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-fields-pages";
        WriteSession(
            store,
            sessionKey,
            [.. Enumerable.Range(0, 30).Select(static i => Turn(
                $"span-page-{i:D2}",
                "sess-fields-pages",
                i,
                parts: [TextPart(0, "user_prompt", $"page text {i}")]))]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        JsonElement firstPage = bridge.GetStoredFieldRows(sessionKey, "content_part", 1, 25)!.Value;

        Assert.Equal(30, firstPage.GetProperty("totalRows").GetInt64());
        Assert.Equal(25, firstPage.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, firstPage.GetProperty("offset").GetInt32());

        JsonElement lastPage = bridge.GetStoredFieldRows(sessionKey, "content_part", 2, 25)!.Value;

        Assert.Equal(30, lastPage.GetProperty("totalRows").GetInt64());
        Assert.Equal(5, lastPage.GetProperty("rows").GetArrayLength());
        Assert.Equal(25, lastPage.GetProperty("offset").GetInt32());
    }

    /// <summary>Runs, harnesses, findings, report, compare, and timeline answer from the projection store.</summary>
    [Fact]
    public void RunsHarnessesFindingsReportAndTimelineAnswerFromTheProjectionStore()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(
            store,
            "sess-cx",
            [
                Turn("turn-c1", "sess-cx", 0, inputTokens: 400, declaredContextWindow: 2_000),
                Turn("turn-c2", "sess-cx", 1, inputTokens: 1_800, declaredContextWindow: 2_000),
            ]);
        (ProjectionPassReport pass, IReadOnlyList<HarnessRollupRow> rollups) = CreateProjection(store, pricing).ProjectAll();
        Assert.Equal(1, pass.Built);
        Assert.Equal(1, pass.Findings);
        Assert.Single(rollups);
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        JsonElement runsPayload = bridge.GetRuns();
        JsonElement run = Assert.Single(runsPayload.GetProperty("runs").EnumerateArray());
        Assert.Equal("derived:claude-code:sess-cx", run.GetProperty("runId").GetString());
        Assert.Equal("derived", run.GetProperty("groupingBasis").GetString());
        Assert.Equal(1, run.GetProperty("executionCount").GetInt32());
        Assert.Equal(2, run.GetProperty("turnCount").GetInt32());
        Assert.Equal(2_200, run.GetProperty("totalInput").GetInt64());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("costUsd").ValueKind);

        JsonElement detail = bridge.GetRunById("derived:claude-code:sess-cx")!.Value;
        Assert.Equal(1, detail.GetProperty("run").GetProperty("findingCount").GetInt32());
        Assert.Equal(1, detail.GetProperty("findings").GetArrayLength());
        Assert.Equal(2, detail.GetProperty("turns").GetArrayLength());
        Assert.Single(detail.GetProperty("executionTree").EnumerateArray());
        Assert.True(detail.GetProperty("scorecard").ValueKind is JsonValueKind.Object or JsonValueKind.Null);

        JsonElement findingsPayload = bridge.GetFindings();
        Assert.Equal(1, findingsPayload.GetProperty("total").GetInt32());
        Assert.Equal(1, findingsPayload.GetProperty("detectorCounts").GetProperty("compaction-hazard").GetInt32());
        JsonElement finding = Assert.Single(findingsPayload.GetProperty("findings").EnumerateArray());
        Assert.Equal("compaction-hazard", finding.GetProperty("detectorId").GetString());
        string findingId = finding.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(findingId));
        Finding? stored = bridge.GetFindingById(findingId);
        Assert.NotNull(stored);
        Assert.Equal(2, stored!.EvidenceLinks.Count);
        Assert.Null(bridge.GetFindingById("no-such-finding"));

        JsonElement harnessesPayload = bridge.GetHarnesses();
        JsonElement harness = Assert.Single(harnessesPayload.GetProperty("harnesses").EnumerateArray());
        Assert.Equal("claude-code", harness.GetProperty("harness").GetString());
        Assert.Equal(1, harness.GetProperty("sampleCount").GetInt32());
        Assert.Equal(1, harness.GetProperty("sessionCount").GetInt32());
        Assert.Equal(1, harness.GetProperty("findingCount").GetInt32());
        Assert.True(harness.GetProperty("scorecard").TryGetProperty("continuity", out _));
        Assert.NotNull(bridge.GetHarnessById("claude-code"));
        Assert.Null(bridge.GetHarnessById("no-such-harness"));

        JsonElement report = bridge.GetReport();
        Assert.Equal(1, report.GetProperty("sessionCount").GetInt32());
        Assert.Equal(1, report.GetProperty("findingCount").GetInt32());
        Assert.Equal(1, report.GetProperty("harnessCount").GetInt32());

        Assert.True(bridge.GetCompare().TryGetProperty("harnesses", out _));

        JsonElement meta = bridge.GetMeta();
        Assert.Equal(SqliteRelationalStore.SchemaVersion, meta.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(SqliteRelationalStore.DetectorVersion, meta.GetProperty("detectorVersion").GetInt32());
        Assert.Equal(2, meta.GetProperty("recordCount").GetInt32());
        Assert.Equal(1, meta.GetProperty("sessionCount").GetInt32());
        Assert.Equal(1, meta.GetProperty("apiVersion").GetInt32());

        JsonElement timeline = bridge.GetTimeline("sess-cx");
        JsonElement timelineSession = Assert.Single(timeline.GetProperty("sessions").EnumerateArray());
        Assert.Equal("sess-cx", timelineSession.GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.Object, timelineSession.GetProperty("timeline").ValueKind);
    }

    /// <summary>Quarantine and problems pages answer from the operational store.</summary>
    [Fact]
    public void QuarantineAndProblemsPagesAnswerFromTheOperationalStore()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, "sess-q", [Turn("turn-q1", "sess-q", 0, inputTokens: 400, outputTokens: 60)]);
        _ = CreateProjection(store, pricing).ProjectAll();
        store.PutQuarantinedSpan(new StoredQuarantinedSpan(
            "span-q",
            "unsupported_namespace",
            ["gen_ai"],
            Source: "otlp",
            Name: "chat",
            TimestampUtc: "2026-11-19T10:00:00.000Z",
            HarnessId: "codex"));
        ((IRelationalStoreWriter)store).WriteProblem(new CollectedProblem(
            "prob-1",
            "warning",
            "SOME_CODE",
            "message text",
            SpanId: "turn-q1",
            Location: null,
            SessionKey: "sess-q",
            HarnessId: "claude-code",
            TimestampUtc: "2026-11-19T10:00:30.000Z",
            Stack: "at KyberDash.Tests()"));
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        JsonElement quarantinePage = bridge.GetQuarantinePage(null, 10, 0, 1);
        Assert.Equal(1, quarantinePage.GetProperty("total").GetInt32());
        JsonElement quarantined = Assert.Single(quarantinePage.GetProperty("entries").EnumerateArray());
        Assert.Equal("span-q", quarantined.GetProperty("spanId").GetString());
        Assert.Equal("unsupported_namespace", quarantined.GetProperty("reason").GetString());
        Assert.False(quarantined.GetProperty("evidenceAvailable").GetBoolean());
        Assert.Equal(1, bridge.GetQuarantinePage("codex", 10, 0, 1).GetProperty("total").GetInt32());
        Assert.Equal(0, bridge.GetQuarantinePage("claude-code", 10, 0, 1).GetProperty("total").GetInt32());

        JsonElement quarantineDetail = bridge.GetQuarantineDetail("span-q")!.Value;
        Assert.Equal("chat", quarantineDetail.GetProperty("name").GetString());
        Assert.Null(bridge.GetQuarantineDetail("no-such-span"));

        JsonElement problemsPage = bridge.GetProblemsPage(null, 10, 0, 1);
        Assert.Equal(1, problemsPage.GetProperty("total").GetInt32());
        JsonElement problem = Assert.Single(problemsPage.GetProperty("problems").EnumerateArray());
        Assert.Equal("SOME_CODE", problem.GetProperty("code").GetString());
        Assert.Equal("sess-q", problem.GetProperty("sessionId").GetString());
        Assert.Equal("claude-code", problem.GetProperty("harness").GetString());

        JsonElement problemDetail = bridge.GetProblemDetail("1")!.Value;
        Assert.Equal(1, problemDetail.GetProperty("id").GetInt64());
        Assert.Equal("message text", problemDetail.GetProperty("message").GetString());
        Assert.False(string.IsNullOrEmpty(problemDetail.GetProperty("stack").GetString()));
        Assert.Null(bridge.GetProblemDetail("bogus"));
        Assert.Null(bridge.GetProblemDetail("99"));
    }

    /// <summary>Coverage reports refresh, ingest, quarantine, checkpoints, harness dimensions, and projection health.</summary>
    [Fact]
    public void CoverageReportsRefreshIngestCheckpointsAndProjectionHealth()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, "sess-cov", [Turn("turn-v1", "sess-cov", 0, inputTokens: 400, outputTokens: 60)]);
        _ = CreateProjection(store, pricing).ProjectAll();
        ((IOperationalStore)store).UpsertRefreshRun(new RefreshRunRow(
            "rr-1",
            "2026-11-19T11:00:00.000Z",
            "2026-11-19T11:05:00.000Z",
            "success",
            42,
            "test",
            "ok",
            2));
        ((IOperationalStore)store).RecordIngest("otlp", 1, "2026-11-19T11:06:00.000Z");
        ((IOperationalStore)store).UpsertSourceCheckpoint(new SourceCheckpoint(
            "claude-code",
            "/tmp/a.jsonl",
            "claude",
            "reader",
            "1",
            "claude-jsonl",
            "claude-projects",
            "rev-1",
            "2026-11-05T00:00:00.000Z",
            "2026-11-19T11:00:00.000Z",
            "2026-11-19T11:00:00.000Z",
            "2026-11-19T11:00:00.000Z",
            "ok",
            null,
            1,
            1));
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        JsonElement coverage = bridge.GetCoverage();
        Assert.Equal("2026-11-19T11:05:00.000Z", coverage.GetProperty("refresh").GetProperty("lastSuccessAt").GetString());
        Assert.Equal(2, coverage.GetProperty("refresh").GetProperty("historyWeeks").GetInt32());
        Assert.Equal("known", coverage.GetProperty("ingest").GetProperty("status").GetString());
        Assert.Equal("2026-11-19T11:06:00.000Z", coverage.GetProperty("ingest").GetProperty("lastReceivedAt").GetString());
        Assert.True(coverage.GetProperty("ingest").GetProperty("sources").GetArrayLength() > 0);
        Assert.True(coverage.GetProperty("quarantineByReason").ValueKind is JsonValueKind.Array);
        Assert.Equal(1, coverage.GetProperty("checkpoints").GetArrayLength());
        Assert.False(coverage.GetProperty("receiver").GetProperty("enabled").GetBoolean());
        Assert.True(coverage.GetProperty("dimensions").GetArrayLength() > 0);
        JsonElement harness = Assert.Single(coverage.GetProperty("harnesses").EnumerateArray());
        Assert.Equal("claude-code", harness.GetProperty("harness").GetString());
        Assert.Equal(1, harness.GetProperty("sampleCount").GetInt32());

        JsonElement projectionHealth = coverage.GetProperty("projection");
        Assert.Equal("ok", projectionHealth.GetProperty("status").GetString());
        Assert.Equal(0, projectionHealth.GetProperty("failedSessions").GetArrayLength());
    }

    /// <summary>Predictions record, list, and calibrate through the projection store.</summary>
    [Fact]
    public void PredictionsRecordListAndCalibrateThroughTheProjectionStore()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        Assert.False(bridge.TryRecordPrediction(Json("not-an-object"), out PredictionRow? rejected, out string? error));
        Assert.Null(rejected);
        Assert.False(string.IsNullOrEmpty(error));

        Assert.True(bridge.TryRecordPrediction(
            Json(new { findingId = "finding-1", runId = "run-1", predictedWasteTokens = 500, confidence = 0.6 }),
            out PredictionRow? created,
            out string? createdError));
        Assert.Null(createdError);
        Assert.NotNull(created);
        Assert.StartsWith("pred-", created!.Id, StringComparison.Ordinal);
        Assert.Equal("finding-1", created.FindingId);
        Assert.Equal(500, created.PredictedWasteTokens);
        Assert.Equal("medium", created.ConfidenceTier);

        Assert.True(bridge.TryRecordPrediction(
            Json(new { findingId = "finding-2", runId = "run-1", confidence = 0.9, calibrationScore = 0.9 }),
            out _,
            out _));

        JsonElement predictions = bridge.GetPredictions();
        Assert.Equal(2, predictions.GetArrayLength());
        Assert.Equal(500, predictions[0].GetProperty("predictedWasteTokens").GetInt64());
        Assert.Equal("run-1", predictions[0].GetProperty("runId").GetString());
        Assert.Empty(bridge.GetPredictions("run-2").EnumerateArray());
        StoredPrediction? stored = ((IProjectionStore)store).ListPredictions("run-1")
            .SingleOrDefault(static p => p.FindingId == "finding-2");
        Assert.NotNull(stored);
        Assert.NotNull(stored.CalibrationScore);
        Assert.Equal(0.9, stored.CalibrationScore!.Value, precision: 6);

        JsonElement calibration = bridge.GetCalibration();
        Assert.Equal(2, calibration.GetProperty("totalPredictions").GetInt32());
        Assert.Equal(1, calibration.GetProperty("evaluatedCount").GetInt32());
        Assert.Equal(0.9, calibration.GetProperty("meanCalibrationScore").GetDouble(), precision: 6);
    }

    /// <summary>A failing session answers PROJECTION_FAILED while the remaining sessions answer normally.</summary>
    [Fact]
    public void FailingSessionIsIsolatedAsProjectionFailedWhileRemainingSessionsAnswer()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, "sess-healthy", [Turn("turn-h1", "sess-healthy", 0, inputTokens: 400, outputTokens: 60)]);
        WriteSession(store, "sess-broken", [Turn("turn-x1", "sess-broken", 0, inputTokens: 100, outputTokens: 10, harnessId: "codex")], harnessId: "codex");
        _ = CreateProjection(store, pricing).ProjectAll();
        FaultingSessionReader faultingReader = new(store, "sess-broken");
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing, readerOverride: faultingReader);

        List<JsonElement> nodes = [.. bridge.GetSessionPage().GetProperty("sessions").EnumerateArray()];
        Assert.Equal(2, nodes.Count);
        JsonElement healthy = Assert.Single(nodes, static n => n.GetProperty("session_id").GetString() == "sess-healthy");
        Assert.Equal(1, healthy.GetProperty("turn_count").GetInt32());
        Assert.Equal(400, healthy.GetProperty("total_input").GetInt64());
        Assert.False(healthy.TryGetProperty("projection_status", out _));
        JsonElement broken = Assert.Single(nodes, static n => n.GetProperty("session_id").GetString() == "sess-broken");
        Assert.Equal("PROJECTION_FAILED", broken.GetProperty("projection_status").GetString());

        JsonElement brokenSummary = bridge.GetSessionSummary("sess-broken")!.Value;
        Assert.Equal("PROJECTION_FAILED", brokenSummary.GetProperty("projection_status").GetString());
        Assert.Equal("sess-broken", brokenSummary.GetProperty("session_id").GetString());
        Assert.Equal(JsonValueKind.Null, brokenSummary.GetProperty("summary").ValueKind);

        JsonElement healthySummary = bridge.GetSessionSummary("sess-healthy")!.Value;
        Assert.False(healthySummary.TryGetProperty("projection_status", out _));
        Assert.Equal(400, healthySummary.GetProperty("summary").GetProperty("total_input").GetInt64());

        JsonElement brokenCharts = bridge.GetSessionCharts("sess-broken")!.Value;
        Assert.Equal("PROJECTION_FAILED", brokenCharts.GetProperty("projection_status").GetString());
        Assert.Equal(0, brokenCharts.GetProperty("turns").GetArrayLength());
        Assert.Equal(1, bridge.GetSessionCharts("sess-healthy")!.Value.GetProperty("turns").GetArrayLength());

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics("query", null);
        Assert.Equal(200, statusCode);
        Assert.True(envelope.GetProperty("data").GetProperty("groups").GetArrayLength() > 0);

        JsonElement projectionHealth = bridge.GetCoverage().GetProperty("projection");
        Assert.Equal("PROJECTION_FAILED", projectionHealth.GetProperty("status").GetString());
        Assert.Contains("sess-broken", [.. projectionHealth.GetProperty("failedSessions").EnumerateArray().Select(static s => s.GetString())], StringComparer.Ordinal);
    }

    /// <summary>Summary, charts, tools, servers, skills, and reconciliation sections project 1-based context, schema, skills, servers, reconciliation, notes, costBasisMismatch, and dual turn keys.</summary>
    [Fact]
    public void SectionRoutesProjectMultiTurnOtlpContextSchemaSkillsServersReconciliationAndDualTurnKeys()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new(snapshot: new Dictionary<string, ModelCosts>(StringComparer.Ordinal)
        {
            ["claude-sonnet-4-6"] = new ModelCosts(
                InputCostPerToken: 0.000003,
                OutputCostPerToken: 0.000015,
                CacheWriteCostPerToken: 0.00000375,
                CacheReadCostPerToken: 0.0000003,
                WebSearchCostPerRequest: 0,
                FastMultiplier: 1,
                CacheWriteCostIsExplicit: true),
        });

        string toolDefsJson = JsonSerializer.Serialize(new object[]
        {
            new { name = "search_docs", server = "docs-mcp", description = "Search documentation repository for relevant sections." },
            new { name = "unused_mcp_tool", server = "docs-mcp", description = "An unused MCP tool schema carried across turns." },
            new { name = "Bash", server = (string?)null, description = "Run a shell command." },
        });

        OtlpSpan turn1 = MakeOtlpSpan(
            spanId: "otlp-turn-1",
            traceId: "11111111111111111111111111111111",
            serviceName: "claude-host",
            name: "chat",
            kind: "client",
            timestamp: "2026-11-19T10:00:00.000Z",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude.deployment_mode"] = Json("1p"),
                ["session.id"] = Json("sess-otlp-rich"),
                ["gen_ai.response.model"] = Json("claude-sonnet-4-6"),
                ["gen_ai.usage.input_tokens"] = Json(100),
                ["gen_ai.usage.cache_read.input_tokens"] = Json(200),
                ["gen_ai.usage.cache_creation.input_tokens"] = Json(50),
                ["gen_ai.usage.output_tokens"] = Json(40),
                ["gen_ai.usage.reasoning_tokens"] = Json(10),
                ["cost_usd"] = Json(0.12),
                ["gen_ai.system_instructions"] = Json("You are a helpful coding assistant."),
                ["gen_ai.tool.definitions"] = Json(toolDefsJson),
                ["gen_ai.input.messages"] = Json("[{\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"text\":\"Check docs\"}]}]"),
                ["gen_ai.request.max_context_tokens"] = Json(2000),
            });

        OtlpSpan turn2 = MakeOtlpSpan(
            spanId: "otlp-turn-2",
            traceId: "11111111111111111111111111111111",
            serviceName: "claude-host",
            name: "chat",
            kind: "client",
            timestamp: "2026-11-19T10:01:00.000Z",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude.deployment_mode"] = Json("1p"),
                ["session.id"] = Json("sess-otlp-rich"),
                ["gen_ai.response.model"] = Json("claude-sonnet-4-6"),
                ["gen_ai.usage.input_tokens"] = Json(1500),
                ["gen_ai.usage.cache_read.input_tokens"] = Json(100),
                ["gen_ai.usage.cache_creation.input_tokens"] = Json(0),
                ["gen_ai.usage.output_tokens"] = Json(30),
                ["gen_ai.tool.definitions"] = Json(toolDefsJson),
                ["gen_ai.input.messages"] = Json("[{\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"text\":\"Run search\"}]}]"),
                ["gen_ai.request.max_context_tokens"] = Json(2000),
            });

        OtlpSpan toolCall = MakeOtlpSpan(
            spanId: "otlp-tool-1",
            traceId: "11111111111111111111111111111111",
            serviceName: "claude-host",
            name: "execute_tool",
            kind: "internal",
            timestamp: "2026-11-19T10:00:30.000Z",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude.deployment_mode"] = Json("1p"),
                ["session.id"] = Json("sess-otlp-rich"),
                ["gen_ai.operation.name"] = Json("execute_tool"),
                ["gen_ai.tool.name"] = Json("search_docs"),
                ["gen_ai.tool.call.id"] = Json("call-1"),
            });

        OtlpSpan skillCall = MakeOtlpSpan(
            spanId: "otlp-skill-1",
            traceId: "11111111111111111111111111111111",
            serviceName: "claude-host",
            name: "execute_tool",
            kind: "internal",
            timestamp: "2026-11-19T10:00:45.000Z",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude.deployment_mode"] = Json("1p"),
                ["session.id"] = Json("sess-otlp-rich"),
                ["gen_ai.operation.name"] = Json("execute_tool"),
                ["gen_ai.tool.name"] = Json("Skill"),
                ["gen_ai.tool.call.id"] = Json("call-skill-1"),
                ["gen_ai.tool.call.arguments"] = Json("{\"skill\":\"docs\"}"),
            });

        IngestOutcome outcome = CanonIngestPipeline.IngestBatch([turn1, turn2, toolCall, skillCall], store, store);
        Assert.Equal(new IngestOutcome(4, 0, 0), outcome);

        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        JsonElement summary = bridge.GetSessionSummary("sess-otlp-rich")!.Value;
        JsonElement charts = bridge.GetSessionCharts("sess-otlp-rich")!.Value;
        JsonElement toolsPage = bridge.GetSessionTools("sess-otlp-rich")!.Value;
        JsonElement serversPage = bridge.GetSessionServers("sess-otlp-rich")!.Value;
        JsonElement skillsPage = bridge.GetSessionSkills("sess-otlp-rich")!.Value;
        JsonElement reconciliationPage = bridge.GetSessionReconciliation("sess-otlp-rich")!.Value;

        Assert.Equal(4, summary.GetProperty("span_count").GetInt32());

        JsonElement mismatch = summary.GetProperty("costBasisMismatch");
        Assert.Equal("COST_BASIS_MISMATCH", mismatch.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Object, mismatch.GetProperty("totalsByBasis").ValueKind);
        Assert.True(mismatch.GetProperty("totalsByBasis").TryGetProperty("harness", out _));
        Assert.True(mismatch.GetProperty("totalsByBasis").TryGetProperty("published", out _));

        List<JsonElement> turnDtos = [.. charts.GetProperty("turns").EnumerateArray()];
        Assert.Equal(2, turnDtos.Count);
        JsonElement firstTurn = turnDtos[0];
        Assert.Equal(0, firstTurn.GetProperty("turnIndex").GetInt32());
        Assert.Equal(0, firstTurn.GetProperty("index").GetInt32());
        Assert.Equal(350, firstTurn.GetProperty("inputTokens").GetInt64());
        Assert.Equal(350, firstTurn.GetProperty("input").GetInt64());
        Assert.Equal(40, firstTurn.GetProperty("outputTokens").GetInt64());
        Assert.Equal(40, firstTurn.GetProperty("output").GetInt64());
        Assert.Equal(100, firstTurn.GetProperty("fresh").GetInt64());
        Assert.Equal(100, firstTurn.GetProperty("fresh_input").GetInt64());
        Assert.Equal(200, firstTurn.GetProperty("cache_read").GetInt64());
        Assert.Equal(50, firstTurn.GetProperty("cache_creation").GetInt64());
        Assert.Equal(10, firstTurn.GetProperty("reasoning").GetInt64());

        JsonElement context = charts.GetProperty("context");
        Assert.True(context.GetProperty("measurable").GetBoolean());
        List<JsonElement> contextTurns = [.. context.GetProperty("turns").EnumerateArray()];
        Assert.Equal(2, contextTurns.Count);
        Assert.Equal(1, contextTurns[0].GetProperty("index").GetInt32());
        Assert.Equal(2, contextTurns[1].GetProperty("index").GetInt32());
        Assert.Equal(JsonValueKind.Object, contextTurns[0].GetProperty("toolDefinitionsByServer").ValueKind);
        Assert.True(contextTurns[0].GetProperty("toolDefinitionsByServer").GetProperty("docs-mcp").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Object, contextTurns[0].GetProperty("residual").ValueKind);
        Assert.True(contextTurns[0].GetProperty("residual").TryGetProperty("tokens", out _));
        Assert.True(contextTurns[0].GetProperty("residual").TryGetProperty("attribution", out _));
        Assert.True(context.TryGetProperty("first", out JsonElement firstEdge));
        Assert.True(firstEdge.TryGetProperty("buckets", out _));
        Assert.True(firstEdge.TryGetProperty("reported_input", out _));
        Assert.True(context.TryGetProperty("last", out JsonElement lastEdge));
        Assert.True(lastEdge.TryGetProperty("buckets", out _));
        Assert.True(lastEdge.TryGetProperty("reported_input", out _));
        foreach (JsonElement flagged in context.GetProperty("flaggedTurns").EnumerateArray())
        {
            Assert.True(flagged.GetInt32() >= 1);
        }

        JsonElement schema = toolsPage.GetProperty("aggregate");
        Assert.True(summary.GetProperty("summary").GetProperty("coverage").GetProperty("schema_measurable").GetBoolean());
        // The stored schema ranked three tools; the section's rows carry those three marked
        // in-definitions beside the fourth row the Skill invocation adds without a definition.
        Assert.Equal(4, toolsPage.GetProperty("total").GetInt32());
        Assert.Equal(3, toolsPage.GetProperty("tools").EnumerateArray().Count(static t => t.GetProperty("in_definitions").GetBoolean()));
        Assert.Contains(toolsPage.GetProperty("tools").EnumerateArray(), static t => t.GetProperty("name").GetString() == "unused_mcp_tool" && !t.GetProperty("invoked").GetBoolean());
        Assert.True(schema.GetProperty("byServer").GetProperty("docs-mcp").GetInt64() > 0);
        Assert.True(schema.GetProperty("unusedRange").GetProperty("tokenResidencies").GetInt64() > 0);

        List<JsonElement> tools = [.. toolsPage.GetProperty("tools").EnumerateArray()];
        JsonElement mcpTool = Assert.Single(tools, static t => t.GetProperty("name").GetString() == "search_docs");
        Assert.True(mcpTool.GetProperty("in_definitions").GetBoolean());
        Assert.True(mcpTool.GetProperty("is_mcp").GetBoolean());
        JsonElement builtinTool = Assert.Single(tools, static t => t.GetProperty("name").GetString() == "Bash");
        Assert.True(builtinTool.GetProperty("in_definitions").GetBoolean());
        Assert.False(builtinTool.GetProperty("is_mcp").GetBoolean());

        JsonElement server = Assert.Single(serversPage.GetProperty("servers").EnumerateArray());
        Assert.Equal("docs-mcp", server.GetProperty("server").GetString());
        Assert.True(server.GetProperty("is_mcp").GetBoolean());
        Assert.Equal(2, server.GetProperty("tools").GetInt32());

        JsonElement skills = skillsPage;
        Assert.True(skills.GetProperty("measurable").GetBoolean());
        JsonElement docsSkill = Assert.Single(skills.GetProperty("rows").EnumerateArray());
        Assert.Equal("docs", docsSkill.GetProperty("name").GetString());
        Assert.Equal("not_measurable", docsSkill.GetProperty("listed").GetProperty("availability").GetString());
        Assert.Equal(1, docsSkill.GetProperty("invocations").GetInt32());
        Assert.Equal(["docs"], [.. summary.GetProperty("summary").GetProperty("skills_invoked").EnumerateArray().Select(static s => s.GetString())]);

        List<JsonElement> reconciliation = [.. reconciliationPage.GetProperty("reconciliation").EnumerateArray()];
        Assert.Equal(2, reconciliation.Count);
        Assert.All(reconciliation, static row =>
        {
            Assert.Equal(JsonValueKind.True, row.GetProperty("input_match").ValueKind);
            Assert.Equal(JsonValueKind.True, row.GetProperty("output_match").ValueKind);
            Assert.True(row.TryGetProperty("request", out _));
            Assert.True(row.TryGetProperty("root_input", out _));
            Assert.True(row.TryGetProperty("sum_chat_input", out _));
            Assert.True(row.TryGetProperty("root_output", out _));
            Assert.True(row.TryGetProperty("sum_chat_output", out _));
        });

        Assert.True(summary.GetProperty("notes").GetArrayLength() > 0);
    }

    /// <summary>Pi OTLP session without schemas, message parts, or skill listings projects honest not_measurable blocks.</summary>
    [Fact]
    public void SectionRoutesReportHonestNotMeasurableGapsForPiOtlpSession()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();

        OtlpSpan piTurn = MakeOtlpSpan(
            spanId: "pi-turn-1",
            traceId: "22222222222222222222222222222222",
            serviceName: "pi-host",
            name: "pi.agent.chat",
            kind: "client",
            timestamp: "2026-11-19T11:00:00.000Z",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.session.id"] = Json("  sess-pi-gap  "),
                ["gen_ai.usage.input_tokens"] = Json(100),
                ["gen_ai.usage.output_tokens"] = Json(25),
            });

        OtlpSpan piTool = MakeOtlpSpan(
            spanId: "pi-tool-1",
            traceId: "22222222222222222222222222222222",
            serviceName: "pi-host",
            name: "pi.tool",
            kind: "internal",
            timestamp: "2026-11-19T11:00:10.000Z",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.session.id"] = Json("  sess-pi-gap  "),
                ["gen_ai.operation.name"] = Json("execute_tool"),
                ["gen_ai.tool.name"] = Json("Bash"),
                ["gen_ai.tool.call.id"] = Json("pi-call-1"),
            });

        IngestOutcome outcome = CanonIngestPipeline.IngestBatch([piTurn, piTool], store, store);
        Assert.Equal(new IngestOutcome(2, 0, 0), outcome);

        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        JsonElement summary = bridge.GetSessionSummary("sess-pi-gap")!.Value;
        JsonElement charts = bridge.GetSessionCharts("sess-pi-gap")!.Value;
        JsonElement toolsPage = bridge.GetSessionTools("sess-pi-gap")!.Value;

        Assert.Equal("pi", summary.GetProperty("harness").GetString());
        Assert.Equal(2, summary.GetProperty("span_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("costBasisMismatch").ValueKind);

        JsonElement context = charts.GetProperty("context");
        Assert.False(context.GetProperty("measurable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(context.GetProperty("reason").GetString()));

        // The schema's honest gap: the summary's coverage flags it unmeasurable, the notes carry
        // the projection's own reason, and the tools section still counts the invocation.
        Assert.False(summary.GetProperty("summary").GetProperty("coverage").GetProperty("schema_measurable").GetBoolean());
        Assert.Equal(1, toolsPage.GetProperty("aggregate").GetProperty("invocationCount").GetInt32());
        StoredSessionProjection? stored = store.GetSessionProjection("sess-pi-gap");
        Assert.NotNull(stored);
        Assert.False(string.IsNullOrWhiteSpace(stored.SchemaUnmeasurableReason));
        Assert.Contains(
            summary.GetProperty("notes").EnumerateArray(),
            note => note.GetString() == stored.SchemaUnmeasurableReason);
        Assert.Contains(
            summary.GetProperty("notes").EnumerateArray(),
            static note => note.GetString() == "Harness-reported cost was not exported; cost calculated from published rates is shown separately.");

        JsonElement skills = bridge.GetSessionSkills("sess-pi-gap")!.Value;
        Assert.Equal("not_measurable", skills.GetProperty("availability").GetString());
        Assert.False(string.IsNullOrWhiteSpace(skills.GetProperty("reason").GetString()));

        JsonElement bashTool = Assert.Single(toolsPage.GetProperty("tools").EnumerateArray());
        Assert.Equal("Bash", bashTool.GetProperty("name").GetString());
        Assert.False(bashTool.GetProperty("in_definitions").GetBoolean());
        Assert.False(bashTool.GetProperty("is_mcp").GetBoolean());
        Assert.Equal(JsonValueKind.Null, bashTool.GetProperty("total_schema_cost").ValueKind);

        Assert.Equal("not_measurable", summary.GetProperty("summary").GetProperty("tools_offered").GetProperty("availability").GetString());
        Assert.True(summary.GetProperty("notes").GetArrayLength() > 0);
        Assert.Null(bridge.GetSessionSummary("nonexistent-pi-session"));
    }

    private static CanonProjectionService CreateProjection(InMemoryRelationalStore store, FakePricingRepository pricing) =>
        new(
            store,
            store,
            store,
            new CanonSessionProjectorEngine(store, store, store, pricing),
            new CanonRollupsEngine(store, store, pricing));

    private static KyberBridgeEngine CreateBridge(
        InMemoryRelationalStore store,
        IRelationalStoreReader? readerOverride = null,
        FakePricingRepository? pricing = null)
    {
        FakePricingRepository resolvedPricing = pricing ?? new FakePricingRepository();
        return new KyberBridgeEngine(
            readerOverride ?? store,
            store,
            store,
            resolvedPricing,
            new CanonSessionProjectorEngine(readerOverride ?? store, store, store, resolvedPricing),
            new CanonRollupsEngine(readerOverride ?? store, store, resolvedPricing),
            clock: () => FixedTimestamp,
            instanceId: "test-instance");
    }

    private static void WriteSession(
        InMemoryRelationalStore store,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records,
        string harnessId = "claude-code",
        string? repo = null,
        string? branch = null,
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
                    Workspace: repo is null && branch is null
                        ? null
                        : new CollectedWorkspace("/tmp/working-directory", repo, branch),
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
        string? model = "claude-sonnet-4-6",
        double? reportedCostUsd = null,
        IReadOnlyList<CollectedContentPart>? parts = null,
        long? declaredContextWindow = null,
        string harnessId = "claude-code",
        IReadOnlyList<CollectedTokenCount>? tokenCounts = null,
        long? reportedInputTokens = null,
        long? reportedOutputTokens = null)
    {
        // An explicit tokenCounts list overrides the default per-class rows, so a test can seed
        // a token-less turn (no rows at all) or an exact class breakdown; the reported totals
        // then default to absent instead of the derived checksum.
        List<CollectedTokenCount> tokens = tokenCounts is null
            ?
            [
                new CollectedTokenCount(TokenClass.Input, inputTokens),
                new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
                new CollectedTokenCount(TokenClass.CacheWrite, 0),
                new CollectedTokenCount(TokenClass.Output, outputTokens),
            ]
            : [.. tokenCounts];

        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens: tokens,
            ReportedCost: reportedCostUsd.HasValue
                ? new CollectedCost(reportedCostUsd.Value, "USD")
                : null,
            Parts: parts ?? [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: declaredContextWindow),
            ReportedInputTokens: reportedInputTokens ?? (tokenCounts is null ? inputTokens + cacheRead : null),
            ReportedOutputTokens: reportedOutputTokens ?? (tokenCounts is null ? outputTokens : null));
    }

    private static CollectedContentPart TextPart(int position, string bucket, string text, long? reportedTokens = null) =>
        new(
            Position: position,
            Bucket: PromptBucket(bucket),
            PlainText: text,
            Withheld: null,
            ReportedTokens: reportedTokens,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    /// <summary>A consent-withheld prompt part: metrics only, no text, so the inspector must carry no prompt.</summary>
    /// <param name="position">Zero-based part position.</param>
    /// <param name="bucket">Canonical part bucket name.</param>
    private static CollectedContentPart WithheldPart(int position, string bucket) =>
        new(
            Position: position,
            Bucket: PromptBucket(bucket),
            PlainText: null,
            Withheld: new CollectedWithheldText(Utf8Bytes: 12, Characters: 12, MeasuredTokens: 3),
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static ContentBucket PromptBucket(string bucket) =>
        bucket switch
        {
            "user_prompt" => ContentBucket.UserPrompt,
            "user_message" => ContentBucket.UserMessage,
            "conversation_history" => ContentBucket.ConversationHistory,
            "tool_result_content" => ContentBucket.ToolResultContent,
            _ => throw new ArgumentException($"Unexpected bucket '{bucket}'.", nameof(bucket)),
        };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static OtlpSpan MakeOtlpSpan(
        string spanId,
        string traceId,
        string serviceName,
        string name,
        string kind,
        string timestamp,
        IReadOnlyDictionary<string, JsonElement> attributes) =>
        new(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: null,
            Name: name,
            Kind: kind,
            StartTimeUnixNano: "1760000000000000000",
            EndTimeUnixNano: "1760000000100000000",
            Timestamp: timestamp,
            DurationMs: 100.0,
            Status: new OtlpStatus("ok"),
            Attributes: attributes,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Json(serviceName),
            },
            Scope: new OtlpScope("test-scope", "1.0.0"));

    /// <summary>
    /// Reader decorator that fails every aggregate read for one session key, standing in for a
    /// store whose projection of that session is broken beyond recovery. The batched
    /// sessions-list read faults when it would cover that session, so the bridge's per-session
    /// degradation is what isolates it.
    /// </summary>
    private sealed class FaultingSessionReader(InMemoryRelationalStore inner, string failingSessionKey) : IRelationalStoreReader
    {
        public IReadOnlyList<string> Harnesses() => inner.Harnesses();

        public IReadOnlyList<StoredSource> Sources(string? harnessId = null) => inner.Sources(harnessId);

        public int CountRecords(string? harnessId = null) => inner.CountRecords(harnessId);

        /// <inheritdoc />
        public IReadOnlyList<StoredSourceCount> SourceRecordCounts() => inner.SourceRecordCounts();

        public CollectedRecord? RecordContentFree(string spanId) => inner.RecordContentFree(spanId);

        public string? SpanPromptText(string spanId) => inner.SpanPromptText(spanId);

        public IReadOnlyList<StoredSessionAttribution> SessionAttributions() => inner.SessionAttributions();

        public IReadOnlyList<CollectedRecord> SessionRecordsForRebuild(string sessionKey) => inner.SessionRecordsForRebuild(sessionKey);

        public IReadOnlyList<CollectedRecord> AllSessionRecordsForMaintenance() => inner.AllSessionRecordsForMaintenance();

        public IReadOnlyList<StoredTokenCount> TokenCounts(string? spanId = null, string? sessionKey = null) => inner.TokenCounts(spanId, sessionKey);

        public IReadOnlyList<StoredReportedTotal> ReportedTotals(string? spanId = null, string? sessionKey = null) => inner.ReportedTotals(spanId, sessionKey);

        public IReadOnlyList<StoredReportedCost> ReportedCosts(string? spanId = null, string? sessionKey = null) => inner.ReportedCosts(spanId, sessionKey);

        public IReadOnlyList<StoredSkillInvocation> SkillsInvoked(string? spanId = null, string? sessionKey = null) => inner.SkillsInvoked(spanId, sessionKey);

        public IReadOnlyList<StoredToolCall> ToolCalls(string? spanId = null, string? sessionKey = null) => inner.ToolCalls(spanId, sessionKey);

        public IReadOnlyList<StoredContentPart> TurnContentParts(
            string sessionKey,
            int? turnIndex = null,
            string? spanId = null,
            string? part = null,
            int budget = 0,
            bool full = false) =>
            inner.TurnContentParts(sessionKey, turnIndex, spanId, part, budget, full);

        public int TurnContentPartCount(
            string sessionKey,
            int? turnIndex = null,
            string? spanId = null,
            string? part = null) =>
            inner.TurnContentPartCount(sessionKey, turnIndex, spanId, part);

        public IReadOnlyList<StoredToolDefinition> ToolDefinitions(string? spanId = null, string? sessionKey = null) => inner.ToolDefinitions(spanId, sessionKey);

        public IReadOnlyList<StoredAgentInvocation> AgentInvocations(string? spanId = null, string? sessionKey = null) => inner.AgentInvocations(spanId, sessionKey);

        public IReadOnlyList<StoredRecordSignals> RecordSignals(string? spanId = null, string? sessionKey = null) => inner.RecordSignals(spanId, sessionKey);

        public CollectedSession? GetSession(string sessionKey) => inner.GetSession(sessionKey);

        public IReadOnlyList<CollectedSession> Sessions(string? harnessId = null) => inner.Sessions(harnessId);

        public IReadOnlyList<StoredSessionModelUsage> SessionModelUsages(string? sessionKey = null) => inner.SessionModelUsages(sessionKey);

        public StoredFieldsInventoryView StoredFieldsInventory(string sessionKey) => inner.StoredFieldsInventory(sessionKey);

        public StoredTableRows? StoredFieldsRows(string sessionKey, string table, int page, int limit) => inner.StoredFieldsRows(sessionKey, table, page, limit);

        public IReadOnlyList<SessionListFigure> SessionListFigures(string? harnessId = null, IModelPricingLookup? pricing = null)
        {
            IReadOnlyList<SessionListFigure> figures = inner.SessionListFigures(harnessId, pricing);
            if (figures.Any(figure => string.Equals(figure.SessionKey, failingSessionKey, StringComparison.Ordinal)))
            {
                throw new StoredDataException("projection exploded");
            }

            return figures;
        }

        public IReadOnlyList<SessionListFigure> SessionListFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null)
        {
            IReadOnlyList<SessionListFigure> figures = inner.SessionListFigures(sessionKeys, pricing);
            if (figures.Any(figure => string.Equals(figure.SessionKey, failingSessionKey, StringComparison.Ordinal)))
            {
                throw new StoredDataException("projection exploded");
            }

            return figures;
        }

        public SessionSummaryFigure? SessionSummaryFigures(string sessionKey, IModelPricingLookup? pricing = null)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new StoredDataException("projection exploded");
            }

            return inner.SessionSummaryFigures(sessionKey, pricing);
        }

        public IReadOnlyList<SessionReportFigure> SessionReportFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null)
        {
            IReadOnlyList<SessionReportFigure> figures = inner.SessionReportFigures(sessionKeys, pricing);
            if (figures.Any(figure => string.Equals(figure.SessionKey, failingSessionKey, StringComparison.Ordinal)))
            {
                throw new StoredDataException("projection exploded");
            }

            return figures;
        }

        public ReadTimeTurnSummary? TurnFigures(string sessionKey, int turnIndex, IModelPricingLookup? pricing = null)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new StoredDataException("projection exploded");
            }

            return inner.TurnFigures(sessionKey, turnIndex, pricing);
        }

        public SessionTurnFigureSet? SessionTurnFigures(string sessionKey, IModelPricingLookup? pricing = null)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new StoredDataException("projection exploded");
            }

            return inner.SessionTurnFigures(sessionKey, pricing);
        }

        public SessionRequestFigurePageResult SessionRequestFigures(string sessionKey, int page, int limit)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new StoredDataException("projection exploded");
            }

            return inner.SessionRequestFigures(sessionKey, page, limit);
        }

        public SessionReconciliationFigurePageResult SessionReconciliationFigures(string sessionKey, int page, int limit)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new StoredDataException("projection exploded");
            }

            return inner.SessionReconciliationFigures(sessionKey, page, limit);
        }

        public SessionProblemPageResult SessionProblems(string sessionKey, int page, int limit)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new StoredDataException("projection exploded");
            }

            return inner.SessionProblems(sessionKey, page, limit);
        }

        public IReadOnlyList<StoredRequestPromptPreview> RequestPromptPreviews(string sessionKey, IReadOnlyCollection<string> requestSpanIds) =>
            inner.RequestPromptPreviews(sessionKey, requestSpanIds);

        public IReadOnlyList<CollectedRecord> SessionRecordsContentFree(string sessionKey)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new StoredDataException("projection exploded");
            }

            return inner.SessionRecordsContentFree(sessionKey);
        }

        public SessionToolFigureSet SessionToolFigures(string sessionKey, IModelPricingLookup? pricing = null)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new StoredDataException("projection exploded");
            }

            return inner.SessionToolFigures(sessionKey, pricing);
        }

        public IReadOnlyList<StoredSkillInvocation> SessionSkillFigures(string sessionKey)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new StoredDataException("projection exploded");
            }

            return inner.SessionSkillFigures(sessionKey);
        }

        public SessionKwSkillLoadPageResult SessionKwSkillLoads(string sessionKey, int page, int limit) => inner.SessionKwSkillLoads(sessionKey, page, limit);

        public IReadOnlyList<CollectedRecord> SessionTimelineRecords(string sessionKey)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new StoredDataException("projection exploded");
            }

            return inner.SessionTimelineRecords(sessionKey);
        }
    }
}
