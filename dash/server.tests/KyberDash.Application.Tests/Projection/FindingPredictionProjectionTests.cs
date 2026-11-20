using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Verifies findings and predictions persist as typed child rows: detector findings emit typed
/// <c>finding_evidence_link</c> rows and <c>finding_compaction_hazard</c> rows, and predictions
/// store <c>prediction_comparison</c>, <c>prediction_observed_delta</c>, and
/// <c>prediction_calibration</c> rows only when the corresponding values are present.
/// </summary>
public class FindingPredictionProjectionTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A measured dormant tool schema and a compaction hazard project typed evidence links, a
    /// waste/error-bar pair for the measured finding, and a compaction hazard row carrying the
    /// declared window, while the unmeasured dormant finding omits waste and error-bar rows.
    /// </summary>
    [Fact]
    public void ProjectIncremental_WritesTypedFindingEvidenceLinksAndCompactionHazard()
    {
        InMemoryRelationalStore store = new();
        WriteSession(
            store,
            "sess-dormant",
            [
                DormantTurn("turn-d1", "sess-dormant", 0),
                DormantTurn("turn-d2", "sess-dormant", 1),
                DormantTurn("turn-d3", "sess-dormant", 2),
            ]);
        WriteSession(
            store,
            "sess-compact",
            [
                CompactTurn("turn-c1", "sess-compact", 0, inputTokens: 400),
                CompactTurn("turn-c2", "sess-compact", 1, inputTokens: 1800),
            ]);

        ProjectionPassReport report = NewProjector(store).ProjectIncremental();

        Assert.Equal(2, report.Built);
        Assert.Equal(3, report.Findings);

        StoredFinding? measured = Assert.Single(
            ((IProjectionStore)store).ListFindings(sessionId: "sess-dormant"),
            static finding => finding.DetectorId == "dormant-tool-schema"
                && finding.MeasurementClass == "deterministic");
        Assert.Equal("finding-dormant-tool-measured_tool-sess-dormant", measured.FindingId);
        Assert.Equal(240, measured.EstimatedWasteTokens);
        Assert.Equal(new FindingErrorBar(192, 288), measured.ErrorBar);
        Assert.Equal(
            ["turn-d1", "turn-d3"],
            measured.EvidenceLinks.Select(static link => link.SpanId));
        Assert.All(measured.EvidenceLinks, static link => Assert.False(string.IsNullOrWhiteSpace(link.Description)));
        Assert.Null(measured.CompactionHazard);

        StoredFinding? unmeasured = Assert.Single(
            ((IProjectionStore)store).ListFindings(sessionId: "sess-dormant"),
            static finding => finding.DetectorId == "dormant-tool-schema"
                && finding.MeasurementClass == "coverage-gap");
        Assert.Equal(2, unmeasured.EvidenceLinks.Count);
        Assert.Null(unmeasured.EstimatedWasteTokens);
        Assert.Null(unmeasured.ErrorBar);
        Assert.Null(unmeasured.CompactionHazard);

        StoredFinding? hazard = Assert.Single(((IProjectionStore)store).ListFindings(sessionId: "sess-compact"));
        Assert.Equal("compaction-hazard", hazard.DetectorId);
        Assert.Equal(["turn-c1", "turn-c2"], hazard.EvidenceLinks.Select(static link => link.SpanId));
        Assert.Equal(100, hazard.EstimatedWasteTokens);
        Assert.NotNull(hazard.ErrorBar);
        Assert.NotNull(hazard.CompactionHazard);
        Assert.Equal(2000, hazard.CompactionHazard!.ContextLimit);
        Assert.Equal("declared", hazard.CompactionHazard.ContextLimitSource);

        Assert.Equal(3, store.CountRows("finding"));
        Assert.Equal(6, store.CountRows("finding_evidence_link"));
        Assert.Equal(2, store.CountRows("finding_estimated_waste"));
        Assert.Equal(2, store.CountRows("finding_error_bar"));
        Assert.Equal(1, store.CountRows("finding_compaction_hazard"));
        Assert.Equal(3, store.CountRows("finding_run"));
        Assert.Equal(3, store.CountRows("finding_session"));
        Assert.Equal(3, store.CountRows("finding_measurement_class"));
        Assert.Equal(3, store.CountRows("finding_confidence_basis"));
        Assert.Equal(3, store.CountRows("finding_what_would_raise_it"));
    }

    /// <summary>
    /// A session producing only an unmeasured dormant finding writes no compaction hazard, waste,
    /// or error-bar rows: absent optional children stay absent instead of becoming zeroes.
    /// </summary>
    [Fact]
    public void ProjectIncremental_OmitsAbsentOptionalFindingChildren()
    {
        InMemoryRelationalStore store = new();
        WriteSession(
            store,
            "sess-quiet",
            [
                QuietTurn("turn-q1", "sess-quiet", 0),
                QuietTurn("turn-q2", "sess-quiet", 1),
                QuietTurn("turn-q3", "sess-quiet", 2),
            ]);

        ProjectionPassReport report = NewProjector(store).ProjectIncremental();

        Assert.Equal(1, report.Built);
        StoredFinding? finding = Assert.Single(((IProjectionStore)store).ListFindings(sessionId: "sess-quiet"));
        Assert.Equal("dormant-tool-schema", finding.DetectorId);
        Assert.Equal(2, finding.EvidenceLinks.Count);
        Assert.Null(finding.EstimatedWasteTokens);
        Assert.Null(finding.ErrorBar);
        Assert.Null(finding.CompactionHazard);

        Assert.Equal(1, store.CountRows("finding"));
        Assert.Equal(2, store.CountRows("finding_evidence_link"));
        Assert.Equal(0, store.CountRows("finding_compaction_hazard"));
        Assert.Equal(0, store.CountRows("finding_estimated_waste"));
        Assert.Equal(0, store.CountRows("finding_error_bar"));
    }

    /// <summary>
    /// A prediction recorded with comparison, observed-delta, and calibration values persists
    /// each optional child row and reads back through the typed prediction store.
    /// </summary>
    [Fact]
    public void TryRecordPrediction_WritesTypedOptionalPredictionChildren()
    {
        InMemoryRelationalStore projections = new();
        KyberBridgeEngine bridge = NewBridge(new FakeCanonStoreRepository(), projections);
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            findingId = "finding-1",
            runId = "run-1",
            predictedWasteTokens = 500L,
            confidence = 0.9,
            comparisonRunId = "run-2",
            observedDeltaTokens = -120L,
            calibrationScore = 0.75,
        });

        Assert.True(bridge.TryRecordPrediction(body, out PredictionRow? created, out string? errorMessage));
        Assert.Null(errorMessage);
        Assert.NotNull(created);
        Assert.Equal("run-2", created!.ComparisonRunId);
        Assert.Equal(-120, created.ObservedDeltaTokens);
        Assert.Equal(0.75, created.CalibrationScore);
        Assert.Equal("high", created.ConfidenceTier);

        Assert.Equal(1, projections.CountRows("prediction"));
        Assert.Equal(1, projections.CountRows("prediction_comparison"));
        Assert.Equal(1, projections.CountRows("prediction_observed_delta"));
        Assert.Equal(1, projections.CountRows("prediction_calibration"));

        StoredPrediction? stored = ((IProjectionStore)projections).GetPrediction(created.Id);
        Assert.NotNull(stored);
        Assert.Equal("run-2", stored!.ComparisonRunId);
        Assert.Equal(-120, stored.ObservedDeltaTokens);
        Assert.Equal(0.75, stored.CalibrationScore);

        JsonElement listed = bridge.GetPredictions("run-1");
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal("run-2", listed[0].GetProperty("comparisonRunId").GetString());

        JsonElement calibration = bridge.GetCalibration();
        Assert.Equal(1, calibration.GetProperty("totalPredictions").GetInt32());
        Assert.Equal(1, calibration.GetProperty("evaluatedCount").GetInt32());
        Assert.Equal(0.75, calibration.GetProperty("meanCalibrationScore").GetDouble());
    }

    /// <summary>
    /// A prediction recorded without optional values writes the parent row only: no comparison,
    /// observed-delta, or calibration child rows are created.
    /// </summary>
    [Fact]
    public void TryRecordPrediction_OmitsAbsentOptionalPredictionChildren()
    {
        InMemoryRelationalStore projections = new();
        KyberBridgeEngine bridge = NewBridge(new FakeCanonStoreRepository(), projections);
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            findingId = "finding-9",
            runId = "run-9",
        });

        Assert.True(bridge.TryRecordPrediction(body, out PredictionRow? created, out string? errorMessage));
        Assert.Null(errorMessage);
        Assert.NotNull(created);
        Assert.Null(created!.ComparisonRunId);
        Assert.Null(created.ObservedDeltaTokens);
        Assert.Null(created.CalibrationScore);

        Assert.Equal(1, projections.CountRows("prediction"));
        Assert.Equal(0, projections.CountRows("prediction_comparison"));
        Assert.Equal(0, projections.CountRows("prediction_observed_delta"));
        Assert.Equal(0, projections.CountRows("prediction_calibration"));

        StoredPrediction? stored = ((IProjectionStore)projections).GetPrediction(created.Id);
        Assert.NotNull(stored);
        Assert.Null(stored!.ComparisonRunId);
        Assert.Null(stored.ObservedDeltaTokens);
        Assert.Null(stored.CalibrationScore);
    }

    private static CanonSessionProjectorEngine NewProjector(InMemoryRelationalStore store) =>
        new(store, store, store, new FakePricingRepository());

    private static KyberBridgeEngine NewBridge(ICanonStoreRepository store, InMemoryRelationalStore projections) =>
        new(
            store,
            new FakeSettingsRepository(),
            new FakePricingRepository(),
            new CanonSessionProjectorEngine(store, new FakePricingRepository()),
            new CanonRollupsEngine(store, new FakePricingRepository()),
            clock: () => FixedTimestamp,
            instanceId: "test-instance",
            predictionStore: projections);

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

    private static CollectedRecord DormantTurn(string spanId, string sessionKey, int minuteOffset) =>
        Turn(
            spanId,
            sessionKey,
            minuteOffset,
            inputTokens: 500,
            outputTokens: 60,
            parts: [TextPart(0, "conversation_history", "Refactor the login flow")],
            definitions:
            [
                new CollectedToolDefinition(0, "measured_tool", "builtin", 80),
                new CollectedToolDefinition(1, "quiet_tool", "builtin", 0),
            ],
            declaredContextWindow: 200_000);

    private static CollectedRecord QuietTurn(string spanId, string sessionKey, int minuteOffset) =>
        Turn(
            spanId,
            sessionKey,
            minuteOffset,
            inputTokens: 500,
            outputTokens: 60,
            parts: [TextPart(0, "conversation_history", "Refactor the login flow")],
            definitions: [new CollectedToolDefinition(0, "quiet_tool", "builtin", 0)],
            declaredContextWindow: 200_000);

    private static CollectedRecord CompactTurn(string spanId, string sessionKey, int minuteOffset, long inputTokens) =>
        Turn(
            spanId,
            sessionKey,
            minuteOffset,
            inputTokens: inputTokens,
            outputTokens: 60,
            parts: [TextPart(0, "conversation_history", "Summarize the migration plan")],
            declaredContextWindow: 2000);

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
            Tool: null,
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
        "conversation_history" => ContentBucket.ConversationHistory,
        _ => throw new ArgumentException($"Unexpected bucket '{bucket}'.", nameof(bucket)),
    };
}
