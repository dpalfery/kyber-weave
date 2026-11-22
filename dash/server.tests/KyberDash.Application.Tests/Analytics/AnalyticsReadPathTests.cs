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

    /// <summary>Session detail projects 1-based context, schema, skills, servers, reconciliation, notes, costBasisMismatch, and dual turn keys.</summary>
    [Fact]
    public void GetSessionDetailProjectsMultiTurnOtlpContextSchemaSkillsServersReconciliationAndDualTurnKeys()
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

        JsonElement detail = bridge.GetSessionDetail("sess-otlp-rich")!.Value;

        Assert.Equal(4, detail.GetProperty("span_count").GetInt32());

        JsonElement mismatch = detail.GetProperty("costBasisMismatch");
        Assert.Equal("COST_BASIS_MISMATCH", mismatch.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Object, mismatch.GetProperty("totalsByBasis").ValueKind);
        Assert.True(mismatch.GetProperty("totalsByBasis").TryGetProperty("harness", out _));
        Assert.True(mismatch.GetProperty("totalsByBasis").TryGetProperty("published", out _));

        List<JsonElement> turnDtos = [.. detail.GetProperty("turns").EnumerateArray()];
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

        JsonElement context = detail.GetProperty("context");
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

        JsonElement schema = detail.GetProperty("schema");
        Assert.True(schema.GetProperty("measurable").GetBoolean());
        Assert.Equal(3, schema.GetProperty("ranked").GetArrayLength());
        Assert.Contains(schema.GetProperty("neverInvoked").EnumerateArray(), static t => t.GetProperty("name").GetString() == "unused_mcp_tool");
        Assert.True(schema.GetProperty("byServer").GetProperty("docs-mcp").GetInt64() > 0);
        Assert.True(schema.GetProperty("unusedRange").GetProperty("tokenResidencies").GetInt64() > 0);

        List<JsonElement> tools = [.. detail.GetProperty("tools").EnumerateArray()];
        JsonElement mcpTool = Assert.Single(tools, static t => t.GetProperty("name").GetString() == "search_docs");
        Assert.True(mcpTool.GetProperty("in_definitions").GetBoolean());
        Assert.True(mcpTool.GetProperty("is_mcp").GetBoolean());
        JsonElement builtinTool = Assert.Single(tools, static t => t.GetProperty("name").GetString() == "Bash");
        Assert.True(builtinTool.GetProperty("in_definitions").GetBoolean());
        Assert.False(builtinTool.GetProperty("is_mcp").GetBoolean());

        JsonElement server = Assert.Single(detail.GetProperty("servers").EnumerateArray());
        Assert.Equal("docs-mcp", server.GetProperty("server").GetString());
        Assert.True(server.GetProperty("is_mcp").GetBoolean());
        Assert.Equal(2, server.GetProperty("tools").GetInt32());

        JsonElement skills = detail.GetProperty("skills");
        Assert.True(skills.GetProperty("measurable").GetBoolean());
        JsonElement docsSkill = Assert.Single(skills.GetProperty("rows").EnumerateArray());
        Assert.Equal("docs", docsSkill.GetProperty("name").GetString());
        Assert.Equal("not_measurable", docsSkill.GetProperty("listed").GetProperty("availability").GetString());
        Assert.Equal(1, docsSkill.GetProperty("invocations").GetInt32());
        Assert.Equal(["docs"], [.. detail.GetProperty("summary").GetProperty("skills_invoked").EnumerateArray().Select(static s => s.GetString())]);

        List<JsonElement> reconciliation = [.. detail.GetProperty("reconciliation").EnumerateArray()];
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

        Assert.True(detail.GetProperty("notes").GetArrayLength() > 0);
    }

    /// <summary>Pi OTLP session without schemas, message parts, or skill listings projects honest not_measurable blocks.</summary>
    [Fact]
    public void GetSessionDetailReportsHonestNotMeasurableGapsForPiOtlpSession()
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

        JsonElement detail = bridge.GetSessionDetail("sess-pi-gap")!.Value;

        Assert.Equal("pi", detail.GetProperty("harness").GetString());
        Assert.Equal(2, detail.GetProperty("span_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("costBasisMismatch").ValueKind);

        JsonElement context = detail.GetProperty("context");
        Assert.False(context.GetProperty("measurable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(context.GetProperty("reason").GetString()));

        JsonElement schema = detail.GetProperty("schema");
        Assert.False(schema.GetProperty("measurable").GetBoolean());
        Assert.Equal(1, schema.GetProperty("invocationCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(schema.GetProperty("reason").GetString()));

        JsonElement skills = detail.GetProperty("skills");
        Assert.Equal("not_measurable", skills.GetProperty("availability").GetString());
        Assert.False(string.IsNullOrWhiteSpace(skills.GetProperty("reason").GetString()));

        JsonElement bashTool = Assert.Single(detail.GetProperty("tools").EnumerateArray());
        Assert.Equal("Bash", bashTool.GetProperty("name").GetString());
        Assert.False(bashTool.GetProperty("in_definitions").GetBoolean());
        Assert.False(bashTool.GetProperty("is_mcp").GetBoolean());
        Assert.Equal(JsonValueKind.Null, bashTool.GetProperty("total_schema_cost").ValueKind);

        Assert.Equal("not_measurable", detail.GetProperty("summary").GetProperty("tools_offered").GetProperty("availability").GetString());
        Assert.True(detail.GetProperty("notes").GetArrayLength() > 0);
        Assert.Null(bridge.GetSessionDetail("nonexistent-pi-session"));
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
            ReportedInputTokens: inputTokens + cacheRead,
            ReportedOutputTokens: outputTokens);
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
    /// store whose projection of that session is broken beyond recovery.
    /// </summary>
    private sealed class FaultingSessionReader(InMemoryRelationalStore inner, string failingSessionKey) : IRelationalStoreReader
    {
        public IReadOnlyList<string> Harnesses() => inner.Harnesses();

        public IReadOnlyList<StoredSource> Sources(string? harnessId = null) => inner.Sources(harnessId);

        public int CountRecords(string? harnessId = null) => inner.CountRecords(harnessId);

        public CollectedRecord? GetRecord(string spanId) => inner.GetRecord(spanId);

        public IReadOnlyList<CollectedRecord> Records(string? sessionKey = null, string? harnessId = null, string? sourceId = null) =>
            inner.Records(sessionKey, harnessId, sourceId);

        public IReadOnlyList<StoredTokenCount> TokenCounts(string? spanId = null, string? sessionKey = null) => inner.TokenCounts(spanId, sessionKey);

        public IReadOnlyList<StoredReportedTotal> ReportedTotals(string? spanId = null, string? sessionKey = null) => inner.ReportedTotals(spanId, sessionKey);

        public IReadOnlyList<StoredReportedCost> ReportedCosts(string? spanId = null, string? sessionKey = null) => inner.ReportedCosts(spanId, sessionKey);

        public IReadOnlyList<StoredSkillInvocation> SkillsInvoked(string? spanId = null, string? sessionKey = null) => inner.SkillsInvoked(spanId, sessionKey);

        public IReadOnlyList<StoredToolCall> ToolCalls(string? spanId = null, string? sessionKey = null) => inner.ToolCalls(spanId, sessionKey);

        public IReadOnlyList<StoredContentPart> ContentParts(string? spanId = null, string? sessionKey = null) => inner.ContentParts(spanId, sessionKey);

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
