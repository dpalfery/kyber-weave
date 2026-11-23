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
    public void GetSessionsAnswersTypedFiguresAndNeverBlendsReportedAndPublishedCosts()
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

        JsonElement node = Assert.Single(bridge.GetSessions().EnumerateArray());

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
    /// The sessions-list read model answers exactly the figures the broad aggregate reads
    /// answered: per session, <see cref="InMemoryRelationalStore.SessionListFigures"/> equals the
    /// <see cref="InMemoryRelationalStore.GetSessionAggregate"/> row and the unfiltered
    /// <see cref="InMemoryRelationalStore.ListSessionAggregates"/> row, on a token-less session
    /// (token totals stay absent, never zero), a zero-token session (zero stays zero), a
    /// multi-harness session (every attributed harness's turns count), an empty session, and a
    /// content-bearing session.
    /// </summary>
    [Fact]
    public void SessionListFiguresAnswerTheSameFiguresAsTheBroadAggregateRead()
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
            SessionAggregateView? broad = store.GetSessionAggregate(figure.SessionKey);
            Assert.NotNull(broad);
            AssertFigureMatchesAggregate(figure, broad, store.GetSession(figure.SessionKey));
        }

        foreach (SessionAggregateView listed in store.ListSessionAggregates())
        {
            SessionListFigure figure = Assert.Single(figures, f => f.SessionKey == listed.SessionKey);
            AssertFigureMatchesAggregate(figure, listed, store.GetSession(listed.SessionKey));
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
    /// <see cref="KyberBridgeEngine.GetSessions"/> the instrumented fake records zero
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
            nameof(InMemoryRelationalStore.Records),
            nameof(InMemoryRelationalStore.SessionRecordsForRebuild),
            nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance),
            nameof(InMemoryRelationalStore.GetRecord),
            nameof(InMemoryRelationalStore.GetSessionAggregate),
            nameof(InMemoryRelationalStore.ListSessionAggregates),
            nameof(InMemoryRelationalStore.SessionTurnSummaries),
            nameof(InMemoryRelationalStore.ContentParts),
            nameof(InMemoryRelationalStore.ToolCalls),
            nameof(InMemoryRelationalStore.ToolDefinitions),
            nameof(InMemoryRelationalStore.AgentInvocations),
            nameof(InMemoryRelationalStore.SkillsInvoked),
        ];

        store.ClearReadLog();
        JsonElement unfiltered = bridge.GetSessions();
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
        JsonElement filtered = bridge.GetSessions(harness: "codex");
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => contentBearingReads.Contains(read));
        Assert.Equal(
            ["sess-list-cx"],
            filtered.EnumerateArray().Select(static node => node.GetProperty("session_id").GetString()).ToList());

        store.ClearReadLog();
        JsonElement limited = bridge.GetSessions(limit: 1);
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => contentBearingReads.Contains(read));
        Assert.Equal(
            ["sess-list-cx"],
            limited.EnumerateArray().Select(static node => node.GetProperty("session_id").GetString()).ToList());

        InMemoryRelationalStore emptyStore = new();
        KyberBridgeEngine emptyBridge = CreateBridge(emptyStore, pricing: new FakePricingRepository());
        emptyStore.ClearReadLog();
        JsonElement none = emptyBridge.GetSessions();
        Assert.Equal(0, emptyStore.MaterializedContentBytes);
        Assert.DoesNotContain(emptyStore.ReadLog, read => contentBearingReads.Contains(read));
        Assert.Equal(0, none.GetArrayLength());
    }

    /// <summary>
    /// Selecting a large session answers its detail figures and every per-turn figure from the
    /// content-free read model: the figures equal the broad aggregate and turn summaries the
    /// detail payload used to pay for, and reading them materializes no content, so a session
    /// with many large turns never pulls its text out of the store.
    /// </summary>
    [Fact]
    public void SessionDetailAnswersTurnFiguresWithoutContentReads()
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
        SessionAggregateView aggregate = store.GetSessionAggregate(sessionKey, lookup)!;
        IReadOnlyList<ReadTimeTurnSummary> turns = store.SessionTurnSummaries(sessionKey, lookup);
        Assert.Equal(128, turns.Count);

        store.ClearReadLog();
        SessionDetailFigure detail = store.SessionDetailFigures(sessionKey, lookup)!;

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        Assert.Equal([nameof(InMemoryRelationalStore.SessionDetailFigures)], store.ReadLog);
        AssertSummaryMatchesAggregate(detail.Summary, aggregate);
        Assert.Equivalent(turns, detail.Turns, strict: true);
        Assert.Null(store.SessionDetailFigures("missing", lookup));

        // The pages answer those same figures: the detail header, the bounded summary, and one
        // turn come off the read models rather than a second aggregate or turn-summary pass.
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        JsonElement payload = bridge.GetSessionDetail(sessionKey)!.Value;
        Assert.Equal(detail.Summary.TurnCount, payload.GetProperty("summary").GetProperty("turn_count").GetInt32());
        Assert.Equal(detail.Summary.TotalInput, payload.GetProperty("summary").GetProperty("total_input").GetInt64());
        Assert.Equal(detail.Summary.TotalOutput, payload.GetProperty("summary").GetProperty("total_output").GetInt64());
        Assert.Equal(detail.Summary.DurationMs, payload.GetProperty("summary").GetProperty("duration_ms").GetInt64());
        Assert.Equal(detail.Summary.Problems.Count, payload.GetProperty("problems_count").GetInt32());
        Assert.Equal(detail.Turns.Count, payload.GetProperty("turns").GetArrayLength());
        JsonElement summaryPayload = bridge.GetSessionSummary(sessionKey)!.Value;
        Assert.Equal(detail.Summary.TurnCount, summaryPayload.GetProperty("summary").GetProperty("turn_count").GetInt32());
        Assert.Equal(detail.Summary.TotalInput, summaryPayload.GetProperty("summary").GetProperty("total_input").GetInt64());
        Assert.Equal(
            detail.Turns[1].SpanId,
            bridge.GetTurnFigures(sessionKey, 1)!.Value.GetProperty("turn").GetProperty("spanId").GetString());
        Assert.Null(bridge.GetTurnFigures(sessionKey, detail.Turns.Count));
    }

    /// <summary>
    /// The bounded summary page answers from the content-free summary read model alone: no turn
    /// summary is read, no content is materialized, and every figure equals the broad aggregate.
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
        SessionAggregateView aggregate = store.GetSessionAggregate(sessionKey, lookup)!;

        store.ClearReadLog();
        SessionSummaryFigure summary = store.SessionSummaryFigures(sessionKey, lookup)!;

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        Assert.Equal([nameof(InMemoryRelationalStore.SessionSummaryFigures)], store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.SessionTurnSummaries), store.ReadLog);
        AssertSummaryMatchesAggregate(summary, aggregate);
        Assert.Null(store.SessionSummaryFigures("missing", lookup));
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
        IReadOnlyList<ReadTimeTurnSummary> turns = store.SessionTurnSummaries(sessionKey, lookup);
        Assert.Equal(3, turns.Count);

        store.ClearReadLog();
        ReadTimeTurnSummary second = store.TurnFigures(sessionKey, 1, lookup)!;

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        Assert.Equivalent(turns[1], second, strict: true);
        Assert.Equal("turn-fig-1", second.SpanId);
        Assert.Null(store.TurnFigures(sessionKey, turns.Count, lookup));
        Assert.Null(store.TurnFigures(sessionKey, -1, lookup));
        Assert.Null(store.TurnFigures("missing", 0, lookup));
    }

    /// <summary>
    /// The session detail page answers without materializing content: the bridge's public
    /// <see cref="KyberBridgeEngine.GetSessionDetail"/> reads no content-bearing column and no
    /// content bytes for a session with many large turns, while the payload still carries its
    /// summary, turn, problem, tool, and request figures.
    /// </summary>
    [Fact]
    public void SessionDetailPageAnswersWithoutContentReads()
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
        JsonElement payload = bridge.GetSessionDetail(sessionKey)!.Value;

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        Assert.Equal(128, payload.GetProperty("turns").GetArrayLength());
        Assert.Equal(128, payload.GetProperty("summary").GetProperty("turn_count").GetInt32());
        Assert.True(payload.GetProperty("summary").GetProperty("total_input").GetInt64() > 0);
        Assert.Equal(0, payload.GetProperty("problems_count").GetInt32());
        JsonElement tool = Assert.Single(payload.GetProperty("tools").EnumerateArray());
        Assert.Equal("read", tool.GetProperty("name").GetString());
        Assert.Equal(1, tool.GetProperty("invocations").GetInt32());
        Assert.NotEmpty(payload.GetProperty("requests").EnumerateArray());
        Assert.False(payload.TryGetProperty("projection_status", out _));

        // The bounded summary page answers through the same content-free path.
        store.ClearReadLog();
        JsonElement summary = bridge.GetSessionSummary(sessionKey)!.Value;
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, read => ContentBearingReads.Contains(read));
        Assert.Equal(128, summary.GetProperty("summary").GetProperty("turn_count").GetInt32());
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
        nameof(InMemoryRelationalStore.Records),
        nameof(InMemoryRelationalStore.SessionRecordsForRebuild),
        nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance),
        nameof(InMemoryRelationalStore.GetRecord),
        nameof(InMemoryRelationalStore.GetSessionAggregate),
        nameof(InMemoryRelationalStore.ListSessionAggregates),
        nameof(InMemoryRelationalStore.SessionTurnSummaries),
        nameof(InMemoryRelationalStore.ContentParts),
        nameof(InMemoryRelationalStore.ToolCalls),
        nameof(InMemoryRelationalStore.ToolDefinitions),
        nameof(InMemoryRelationalStore.AgentInvocations),
        nameof(InMemoryRelationalStore.SkillsInvoked),
    ];

    private static void AssertSummaryMatchesAggregate(SessionSummaryFigure summary, SessionAggregateView aggregate)
    {
        Assert.Equal(aggregate.SessionKey, summary.SessionKey);
        Assert.Equal(aggregate.HarnessId, summary.HarnessId);
        Assert.Equal(aggregate.TurnCount, summary.TurnCount);
        Assert.Equal(aggregate.RequestCount, summary.RequestCount);
        Assert.Equal(aggregate.TotalInput, summary.TotalInput);
        Assert.Equal(aggregate.TotalOutput, summary.TotalOutput);
        Assert.Equal(aggregate.TotalCacheRead, summary.TotalCacheRead);
        Assert.Equal(aggregate.TotalCacheCreation, summary.TotalCacheCreation);
        Assert.Equal(aggregate.DurationMs, summary.DurationMs);
        Assert.Equal(aggregate.Models, summary.Models);
        Assert.Equal(aggregate.CacheHitRatio, summary.CacheHitRatio);
        Assert.Equal(aggregate.CacheCreationCoverage, summary.CacheCreationCoverage);
        Assert.Equal(aggregate.ToolCalls, summary.ToolCalls);
        Assert.Equal(aggregate.ToolsInvoked, summary.ToolsInvoked);
        Assert.Equal(aggregate.HasToolDefinitions, summary.HasToolDefinitions);
        Assert.Equal(aggregate.ToolsOffered, summary.ToolsOffered);
        Assert.Equivalent(aggregate.ReportedCost, summary.ReportedCost, strict: true);
        Assert.Equivalent(aggregate.PublishedCost, summary.PublishedCost, strict: true);
        Assert.Equivalent(aggregate.CostBasisMismatch, summary.CostBasisMismatch, strict: true);
        Assert.Equivalent(aggregate.Coverage, summary.Coverage, strict: true);
        Assert.Equivalent(aggregate.Problems, summary.Problems, strict: true);
    }

    private static void AssertFigureMatchesAggregate(
        SessionListFigure figure,
        SessionAggregateView aggregate,
        CollectedSession? session)
    {
        Assert.Equal(aggregate.SessionKey, figure.SessionKey);
        Assert.Equal(aggregate.HarnessId, figure.HarnessId);
        Assert.Equal(aggregate.TurnCount, figure.TurnCount);
        Assert.Equal(aggregate.RequestCount, figure.RequestCount);
        Assert.Equal(aggregate.TotalInput, figure.TotalInput);
        Assert.Equal(aggregate.TotalOutput, figure.TotalOutput);
        Assert.Equal(aggregate.Models, figure.Models);
        Assert.Equivalent(aggregate.ReportedCost, figure.ReportedCost, strict: true);
        Assert.Equivalent(aggregate.PublishedCost, figure.PublishedCost, strict: true);
        Assert.Equivalent(aggregate.CostBasisMismatch, figure.CostBasisMismatch, strict: true);
        Assert.Equal(aggregate.Problems.Count, figure.ProblemsCount);
        Assert.Equal(session?.Workspace?.Repository, figure.Repo);
        Assert.Equal(session?.Workspace?.Branch, figure.Branch);
    }

    /// <summary>Session detail, summary, and span attributes answer from typed rows with no store JSON.</summary>
    [Fact]
    public void GetSessionDetailAnswersTypedSummaryTurnsAndSpanAttributes()
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

        JsonElement detail = bridge.GetSessionDetail("sess-detail")!.Value;
        Assert.Equal("sess-detail", detail.GetProperty("session_id").GetString());
        Assert.Equal("sess-detail", detail.GetProperty("sessionId").GetString());
        Assert.Equal(0, detail.GetProperty("problems_count").GetInt32());
        Assert.Equal(400, detail.GetProperty("summary").GetProperty("total_input").GetInt64());
        Assert.True(detail.GetProperty("summary").TryGetProperty("coverage", out _));

        JsonElement turn = Assert.Single(detail.GetProperty("turns").EnumerateArray());
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

    /// <summary>Turn figures and bounded turn content answer from typed turn summaries.</summary>
    [Fact]
    public void GetTurnFiguresAndTurnContentAnswerBoundedTurnPayloads()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, "sess-turns", [Turn("turn-t1", "sess-turns", 0, inputTokens: 400, outputTokens: 60)]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

        JsonElement figures = bridge.GetTurnFigures("sess-turns", 0)!.Value;
        Assert.Equal("sess-turns", figures.GetProperty("sessionId").GetString());
        Assert.Equal(0, figures.GetProperty("turnIndex").GetInt32());
        Assert.Equal("turn-t1", figures.GetProperty("turn").GetProperty("spanId").GetString());
        Assert.Null(bridge.GetTurnFigures("sess-turns", 5));
        Assert.Null(bridge.GetTurnFigures("missing", 0));

        JsonElement bounded = bridge.GetTurnContent("sess-turns", 0, budget: 64, full: false)!.Value;
        Assert.True(bounded.GetProperty("truncated").GetBoolean());
        Assert.Equal(64, bounded.GetProperty("budget").GetInt32());
        Assert.True(bounded.GetProperty("totalBytes").GetInt64() > 64);
        Assert.False(string.IsNullOrEmpty(bounded.GetProperty("content").GetString()));
        Assert.Equal(JsonValueKind.Null, bounded.GetProperty("turn").ValueKind);

        JsonElement full = bridge.GetTurnContent("sess-turns", 0, full: true)!.Value;
        Assert.False(full.GetProperty("truncated").GetBoolean());
        Assert.Equal("turn-t1", full.GetProperty("turn").GetProperty("spanId").GetString());
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
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.ContentParts), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.Records), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.GetRecord), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.SessionRecordsForRebuild), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance), store.ReadLog);

        store.ClearReadLog();
        JsonElement turnContent = bridge.GetTurnContent(sessionKey, 1, full: true)!.Value;
        Assert.Equal(
            ["user_prompt", "tool_result_content"],
            turnContent.GetProperty("parts").EnumerateArray().Select(static part => part.GetProperty("part").GetString()).ToList());
        Assert.Equal(41, store.MaterializedContentBytes);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.Records), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.ContentParts), store.ReadLog);
    }

    /// <summary>
    /// Every request path except the content view leaves content unread. The sweep calls the
    /// list, detail, summary, turn-figure, span-attribute, timeline, coverage, findings,
    /// problems, quarantine, harness, run, report, and analytics paths over the instrumented
    /// fake against a session whose turns carry four kilobytes of text each, and each path
    /// records zero materialized content bytes and no content-bearing read.
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
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
        bridge.EnsureProjected();

        string[] contentBearingReads =
        [
            nameof(InMemoryRelationalStore.Records),
            nameof(InMemoryRelationalStore.SessionRecordsForRebuild),
            nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance),
            nameof(InMemoryRelationalStore.GetRecord),
            nameof(InMemoryRelationalStore.GetSessionAggregate),
            nameof(InMemoryRelationalStore.ListSessionAggregates),
            nameof(InMemoryRelationalStore.SessionTurnSummaries),
            nameof(InMemoryRelationalStore.ContentParts),
            nameof(InMemoryRelationalStore.ToolCalls),
            nameof(InMemoryRelationalStore.ToolDefinitions),
            nameof(InMemoryRelationalStore.AgentInvocations),
            nameof(InMemoryRelationalStore.SkillsInvoked),
        ];

        (string Path, Action Call)[] paths =
        [
            ("sessions list", () => _ = bridge.GetSessions()),
            ("sessions list filtered", () => _ = bridge.GetSessions(harness: "claude-code")),
            ("session detail", () => _ = bridge.GetSessionDetail(sessionKey)),
            ("session summary", () => _ = bridge.GetSessionSummary(sessionKey)),
            ("turn figures", () => _ = bridge.GetTurnFigures(sessionKey, 0)),
            ("span attributes", () => _ = bridge.GetSpanAttributes("span-sweep-1")),
            ("timeline", () => _ = bridge.GetTimeline(sessionKey)),
            ("coverage", () => _ = bridge.GetCoverage()),
            ("findings", () => _ = bridge.GetFindings(sessionId: sessionKey)),
            ("problems", () => _ = bridge.GetProblemsPage(null, 10, 0, 1)),
            ("quarantine", () => _ = bridge.GetQuarantinePage(null, 10, 0, 1)),
            ("harnesses", () => _ = bridge.GetHarnesses()),
            ("runs", () => _ = bridge.GetRuns()),
            ("report", () => _ = bridge.GetReport()),
            ("analytics status", () => _ = bridge.ExecuteAnalytics("status", null)),
            ("analytics query", () => _ = bridge.ExecuteAnalytics("query", null)),
            ("analytics findings", () => _ = bridge.ExecuteAnalytics("findings", null)),
        ];

        foreach ((string path, Action call) in paths)
        {
            store.ClearReadLog();
            call();
            Assert.True(
                store.MaterializedContentBytes == 0,
                $"the {path} path materialized {store.MaterializedContentBytes} content bytes");
            Assert.DoesNotContain(store.ReadLog, read => contentBearingReads.Contains(read));
        }
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

        List<JsonElement> nodes = [.. bridge.GetSessions().EnumerateArray()];
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

        JsonElement brokenDetail = bridge.GetSessionDetail("sess-broken")!.Value;
        Assert.Equal("PROJECTION_FAILED", brokenDetail.GetProperty("projection_status").GetString());
        Assert.Equal(0, brokenDetail.GetProperty("turns").GetArrayLength());
        Assert.Equal(1, bridge.GetSessionDetail("sess-healthy")!.Value.GetProperty("turns").GetArrayLength());

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics("query", null);
        Assert.Equal(200, statusCode);
        Assert.True(envelope.GetProperty("data").GetProperty("groups").GetArrayLength() > 0);

        JsonElement projectionHealth = bridge.GetCoverage().GetProperty("projection");
        Assert.Equal("PROJECTION_FAILED", projectionHealth.GetProperty("status").GetString());
        Assert.Contains("sess-broken", [.. projectionHealth.GetProperty("failedSessions").EnumerateArray().Select(static s => s.GetString())], StringComparer.Ordinal);
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
            Bucket: bucket switch
            {
                "user_prompt" => ContentBucket.UserPrompt,
                "tool_result_content" => ContentBucket.ToolResultContent,
                _ => throw new ArgumentException($"Unexpected bucket '{bucket}'.", nameof(bucket)),
            },
            PlainText: text,
            Withheld: null,
            ReportedTokens: reportedTokens,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

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

        public CollectedRecord? GetRecord(string spanId) => inner.GetRecord(spanId);

        public CollectedRecord? RecordContentFree(string spanId) => inner.RecordContentFree(spanId);

        public IReadOnlyList<CollectedRecord> Records(string? sessionKey = null, string? harnessId = null, string? sourceId = null) =>
            inner.Records(sessionKey, harnessId, sourceId);

        public IReadOnlyList<StoredSessionAttribution> SessionAttributions() => inner.SessionAttributions();

        public IReadOnlyList<CollectedRecord> SessionRecordsForRebuild(string sessionKey) => inner.SessionRecordsForRebuild(sessionKey);

        public IReadOnlyList<CollectedRecord> AllSessionRecordsForMaintenance() => inner.AllSessionRecordsForMaintenance();

        public IReadOnlyList<StoredTokenCount> TokenCounts(string? spanId = null, string? sessionKey = null) => inner.TokenCounts(spanId, sessionKey);

        public IReadOnlyList<StoredReportedTotal> ReportedTotals(string? spanId = null, string? sessionKey = null) => inner.ReportedTotals(spanId, sessionKey);

        public IReadOnlyList<StoredReportedCost> ReportedCosts(string? spanId = null, string? sessionKey = null) => inner.ReportedCosts(spanId, sessionKey);

        public IReadOnlyList<StoredSkillInvocation> SkillsInvoked(string? spanId = null, string? sessionKey = null) => inner.SkillsInvoked(spanId, sessionKey);

        public IReadOnlyList<StoredToolCall> ToolCalls(string? spanId = null, string? sessionKey = null) => inner.ToolCalls(spanId, sessionKey);

        public IReadOnlyList<StoredContentPart> ContentParts(string? spanId = null, string? sessionKey = null) => inner.ContentParts(spanId, sessionKey);

        public IReadOnlyList<StoredContentPart> TurnContentParts(
            string sessionKey,
            int? turnIndex = null,
            string? spanId = null,
            string? part = null,
            int budget = 0,
            bool full = false) =>
            inner.TurnContentParts(sessionKey, turnIndex, spanId, part, budget, full);

        public IReadOnlyList<StoredToolDefinition> ToolDefinitions(string? spanId = null, string? sessionKey = null) => inner.ToolDefinitions(spanId, sessionKey);

        public IReadOnlyList<StoredAgentInvocation> AgentInvocations(string? spanId = null, string? sessionKey = null) => inner.AgentInvocations(spanId, sessionKey);

        public IReadOnlyList<StoredRecordSignals> RecordSignals(string? spanId = null, string? sessionKey = null) => inner.RecordSignals(spanId, sessionKey);

        public CollectedSession? GetSession(string sessionKey) => inner.GetSession(sessionKey);

        public IReadOnlyList<CollectedSession> Sessions(string? harnessId = null) => inner.Sessions(harnessId);

        public IReadOnlyList<StoredSessionModelUsage> SessionModelUsages(string? sessionKey = null) => inner.SessionModelUsages(sessionKey);

        public StoredFieldsView StoredFields(string sessionKey) => inner.StoredFields(sessionKey);

        public SessionAggregateView? GetSessionAggregate(string sessionKey, IModelPricingLookup? pricing = null)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("projection exploded");
            }

            return inner.GetSessionAggregate(sessionKey, pricing);
        }

        public IReadOnlyList<SessionAggregateView> ListSessionAggregates(string? harnessId = null, IModelPricingLookup? pricing = null) =>
            inner.ListSessionAggregates(harnessId, pricing);

        public IReadOnlyList<SessionListFigure> SessionListFigures(string? harnessId = null, IModelPricingLookup? pricing = null)
        {
            IReadOnlyList<SessionListFigure> figures = inner.SessionListFigures(harnessId, pricing);
            if (figures.Any(figure => string.Equals(figure.SessionKey, failingSessionKey, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("projection exploded");
            }

            return figures;
        }

        public SessionSummaryFigure? SessionSummaryFigures(string sessionKey, IModelPricingLookup? pricing = null)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("projection exploded");
            }

            return inner.SessionSummaryFigures(sessionKey, pricing);
        }

        public SessionDetailFigure? SessionDetailFigures(string sessionKey, IModelPricingLookup? pricing = null)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("projection exploded");
            }

            return inner.SessionDetailFigures(sessionKey, pricing);
        }

        public ReadTimeTurnSummary? TurnFigures(string sessionKey, int turnIndex, IModelPricingLookup? pricing = null)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("projection exploded");
            }

            return inner.TurnFigures(sessionKey, turnIndex, pricing);
        }

        public IReadOnlyList<CollectedRecord> SessionRecordsContentFree(string sessionKey)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("projection exploded");
            }

            return inner.SessionRecordsContentFree(sessionKey);
        }

        public IReadOnlyList<ReadTimeTurnSummary> SessionTurnSummaries(string sessionKey, IModelPricingLookup? pricing = null)
        {
            if (string.Equals(sessionKey, failingSessionKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("projection exploded");
            }

            return inner.SessionTurnSummaries(sessionKey, pricing);
        }
    }
}
