using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests.Analytics;

/// <summary>
/// Detail analytics operations over <see cref="AllHarnessFixtureSet"/>: <see cref="KyberBridgeEngine"/>
/// and <see cref="AnalyticsService"/> answer HTTP 200 contract-v1 envelopes for <c>inspect</c>,
/// <c>findings</c>, <c>compare</c>, and <c>outcomes</c>; an injected store fault maps to a
/// structured error envelope; and a single failing session isolates to <c>PROJECTION_FAILED</c>
/// while the remaining sessions answer normally.
/// </summary>
public sealed class AnalyticsOperationsDetailTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private const string HealthySessionKey = "sess-all-claude";
    private const string BrokenSessionKey = "sess-all-codex";

    /// <summary><c>inspect</c> answers HTTP 200 with a contract-v1 envelope carrying the session summary.</summary>
    [Fact]
    public void InspectAnswersHttp200ContractV1EnvelopeOverFixtureSet()
    {
        (KyberBridgeEngine bridge, _) = CreateFixtureBridge();

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics(
            "inspect",
            Json(new { sessionId = HealthySessionKey }));

        Assert.Equal(200, statusCode);
        AssertContractV1SuccessEnvelope(envelope);
        JsonElement session = envelope.GetProperty("data").GetProperty("session");
        Assert.Equal(HealthySessionKey, session.GetProperty("session_id").GetString());
        Assert.Equal("claude-code", session.GetProperty("harness").GetString());
        Assert.False(session.TryGetProperty("projection_status", out _));
    }

    /// <summary><c>findings</c> answers HTTP 200 with a contract-v1 envelope carrying the finding items.</summary>
    [Fact]
    public void FindingsAnswersHttp200ContractV1EnvelopeOverFixtureSet()
    {
        (KyberBridgeEngine bridge, InMemoryRelationalStore store) = CreateFixtureBridge();
        int storedFindings = store.ListFindings().Count;

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics("findings", null);

        Assert.Equal(200, statusCode);
        AssertContractV1SuccessEnvelope(envelope);
        JsonElement items = envelope.GetProperty("data").GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(storedFindings, items.GetArrayLength());
        Assert.Equal(storedFindings, envelope.GetProperty("pagination").GetProperty("returnedCount").GetInt32());
    }

    /// <summary><c>compare</c> answers HTTP 200 with a contract-v1 envelope carrying harness deltas.</summary>
    [Fact]
    public void CompareAnswersHttp200ContractV1EnvelopeOverFixtureSet()
    {
        (KyberBridgeEngine bridge, _) = CreateFixtureBridge();

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics("compare", null);

        Assert.Equal(200, statusCode);
        AssertContractV1SuccessEnvelope(envelope);
        JsonElement harnesses = envelope.GetProperty("data").GetProperty("deltas").GetProperty("harnesses");
        Assert.Equal(JsonValueKind.Array, harnesses.ValueKind);
        Assert.True(harnesses.GetArrayLength() > 0, "Compare should cover at least one harness.");
    }

    /// <summary><c>outcomes</c> answers HTTP 200 with a contract-v1 envelope marking every session not-measurable.</summary>
    [Fact]
    public void OutcomesAnswersHttp200ContractV1EnvelopeWithNotMeasurableSessions()
    {
        (KyberBridgeEngine bridge, InMemoryRelationalStore store) = CreateFixtureBridge();
        int sessionCount = store.Sessions().Count;
        Assert.True(sessionCount > 0, "Fixture set should contain sessions.");

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics("outcomes", null);

        Assert.Equal(200, statusCode);
        AssertContractV1SuccessEnvelope(envelope);
        JsonElement items = envelope.GetProperty("data").GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(sessionCount, items.GetArrayLength());
        foreach (JsonElement item in items.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("sessionId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("harness").GetString()));
            Assert.Equal("not_measurable", item.GetProperty("outcome").GetString());
        }

        Assert.Equal(sessionCount, envelope.GetProperty("pagination").GetProperty("returnedCount").GetInt32());
    }

    /// <summary><see cref="AnalyticsService"/> delegates the four detail operations to the bridge unchanged.</summary>
    [Fact]
    public void AnalyticsServiceDelegatesDetailOperationsToTheBridge()
    {
        (KyberBridgeEngine bridge, _) = CreateFixtureBridge();
        AnalyticsService service = new(bridge);

        Assert.True(AnalyticsService.IsSupported("inspect"));
        Assert.True(AnalyticsService.IsSupported("findings"));
        Assert.True(AnalyticsService.IsSupported("compare"));
        Assert.True(AnalyticsService.IsSupported("outcomes"));

        (int inspectStatus, JsonElement inspectEnvelope) = bridge.ExecuteAnalytics(
            "inspect",
            Json(new { sessionId = HealthySessionKey }));
        var serviceInspect = service.Execute("inspect", Json(new { sessionId = HealthySessionKey }));
        Assert.Equal(inspectStatus, serviceInspect.StatusCode);
        Assert.Equal(inspectEnvelope.GetRawText(), serviceInspect.Envelope.GetRawText());

        foreach (string operation in new[] { "findings", "compare", "outcomes" })
        {
            (int bridgeStatus, JsonElement bridgeEnvelope) = bridge.ExecuteAnalytics(operation, null);
            var serviceResult = service.Execute(operation, null);
            Assert.Equal(200, bridgeStatus);
            Assert.Equal(bridgeStatus, serviceResult.StatusCode);
            Assert.Equal(bridgeEnvelope.GetRawText(), serviceResult.Envelope.GetRawText());
        }
    }

    /// <summary><c>inspect</c> without a session id answers HTTP 400 with an <c>invalid_request</c> envelope.</summary>
    [Fact]
    public void InspectWithoutSessionIdAnswersBadRequestEnvelope()
    {
        (KyberBridgeEngine bridge, _) = CreateFixtureBridge();

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics("inspect", Json(new { }));

        Assert.Equal(400, statusCode);
        AssertContractV1ErrorEnvelope(envelope, "invalid_request");
    }

    /// <summary><c>inspect</c> for an unknown session answers HTTP 404 with a <c>not_found</c> envelope.</summary>
    [Fact]
    public void InspectUnknownSessionAnswersNotFoundEnvelope()
    {
        (KyberBridgeEngine bridge, _) = CreateFixtureBridge();

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics(
            "inspect",
            Json(new { sessionId = "sess-does-not-exist" }));

        Assert.Equal(404, statusCode);
        AssertContractV1ErrorEnvelope(envelope, "not_found");
    }

    /// <summary>An injected store fault maps to an HTTP 503 <c>ANALYTICS_STORE_UNAVAILABLE</c> contract-v1 envelope.</summary>
    [Fact]
    public void StoreFaultReturnsUnavailableContractV1Envelope()
    {
        (Mock<IRelationalStoreReader> reader, Mock<IProjectionStore> projections, Mock<IOperationalStore> operational) = CreateBaseStoreMocks();
        reader.Setup(store => store.Sessions(It.IsAny<string?>()))
            .Throws(new SqliteExecutionException("sqlite3 exited with code 1", 1, "database is locked"));
        KyberBridgeEngine engine = CreateEngine(reader.Object, projections.Object, operational.Object);

        (int statusCode, JsonElement envelope) = engine.ExecuteAnalytics("outcomes", null);

        Assert.Equal(503, statusCode);
        AssertContractV1ErrorEnvelope(envelope, "ANALYTICS_STORE_UNAVAILABLE");
    }

    /// <summary>A single failing session isolates to <c>PROJECTION_FAILED</c> while the rest answer normally.</summary>
    [Fact]
    public void SingleFailingSessionIsolatesToProjectionFailedWhileRemainingSessionsAnswer()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();
        FakePricingRepository pricing = new();
        _ = CreateProjection(store, pricing).ProjectAll();
        FaultingSessionReader faultingReader = new(store, BrokenSessionKey);
        KyberBridgeEngine bridge = CreateBridge(store, pricing, faultingReader);

        (int brokenStatus, JsonElement brokenEnvelope) = bridge.ExecuteAnalytics(
            "inspect",
            Json(new { sessionId = BrokenSessionKey }));
        Assert.Equal(200, brokenStatus);
        AssertContractV1SuccessEnvelope(brokenEnvelope);
        JsonElement brokenSession = brokenEnvelope.GetProperty("data").GetProperty("session");
        Assert.Equal(BrokenSessionKey, brokenSession.GetProperty("session_id").GetString());
        Assert.Equal("PROJECTION_FAILED", brokenSession.GetProperty("projection_status").GetString());

        (int healthyStatus, JsonElement healthyEnvelope) = bridge.ExecuteAnalytics(
            "inspect",
            Json(new { sessionId = HealthySessionKey }));
        Assert.Equal(200, healthyStatus);
        JsonElement healthySession = healthyEnvelope.GetProperty("data").GetProperty("session");
        Assert.Equal(HealthySessionKey, healthySession.GetProperty("session_id").GetString());
        Assert.False(healthySession.TryGetProperty("projection_status", out _));

        foreach (string operation in new[] { "findings", "compare", "outcomes" })
        {
            (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics(operation, null);
            Assert.Equal(200, statusCode);
            AssertContractV1SuccessEnvelope(envelope);
        }
    }

    private static (KyberBridgeEngine Bridge, InMemoryRelationalStore Store) CreateFixtureBridge()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();
        FakePricingRepository pricing = new();
        _ = CreateProjection(store, pricing).ProjectAll();
        return (CreateBridge(store, pricing, store), store);
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
        FakePricingRepository pricing,
        IRelationalStoreReader reader)
    {
        return new KyberBridgeEngine(
            reader,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(reader, store, store, pricing),
            new CanonRollupsEngine(reader, store, pricing),
            clock: () => FixedTimestamp,
            instanceId: "test-instance");
    }

    private static (Mock<IRelationalStoreReader> Reader, Mock<IProjectionStore> Projections, Mock<IOperationalStore> Operational) CreateBaseStoreMocks()
    {
        Mock<IRelationalStoreReader> reader = new();
        reader.Setup(store => store.Sessions(It.IsAny<string?>())).Returns(Array.Empty<CollectedSession>());
        reader.Setup(store => store.CountRecords(It.IsAny<string?>())).Returns(0);
        reader.Setup(store => store.Harnesses()).Returns(Array.Empty<string>());

        Mock<IProjectionStore> projections = new();
        projections.Setup(store => store.ListSessionProjections(It.IsAny<string?>()))
            .Returns(Array.Empty<StoredSessionProjection>());
        projections.Setup(store => store.ListRuns(It.IsAny<string?>())).Returns(Array.Empty<StoredRun>());
        projections.Setup(store => store.ListExecutionsForRun(It.IsAny<string>())).Returns(Array.Empty<StoredExecution>());
        projections.Setup(store => store.ListFindings(It.IsAny<string?>(), It.IsAny<string?>())).Returns(Array.Empty<StoredFinding>());
        projections.Setup(store => store.ListHarnessRollups()).Returns(Array.Empty<StoredHarnessRollup>());
        projections.Setup(store => store.ListPredictions(It.IsAny<string?>(), It.IsAny<string?>())).Returns(Array.Empty<StoredPrediction>());

        Mock<IOperationalStore> operational = new();
        operational.Setup(store => store.GetWriteMarker()).Returns(0L);
        operational.Setup(store => store.GetProjectionState()).Returns((ProjectionStateView?)null);
        operational.Setup(store => store.GetProjectionDirtyState()).Returns((DirtyProjectionState?)null);
        operational.Setup(store => store.ReadSettings())
            .Returns(new SettingsView(FolderImportScheduled: false, JobsPaused: false, RefreshCadenceMinutes: 5, ReceiverHosted: false, DashboardPort: 4747));

        return (reader, projections, operational);
    }

    private static KyberBridgeEngine CreateEngine(
        IRelationalStoreReader reader,
        IProjectionStore projections,
        IOperationalStore operational)
    {
        InMemoryRelationalStore memory = new();
        FakePricingRepository pricing = new();
        return new KyberBridgeEngine(
            reader,
            projections,
            operational,
            pricing,
            new CanonSessionProjectorEngine(reader, projections, operational, pricing),
            new CanonRollupsEngine(reader, projections, pricing),
            clock: () => FixedTimestamp,
            instanceId: "test-instance");
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static void AssertContractV1SuccessEnvelope(JsonElement envelope)
    {
        Assert.Equal(1, envelope.GetProperty("contractVersion").GetInt32());
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("scope").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("measurement").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("coverage").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("freshness").ValueKind);
        Assert.Equal("test-instance", envelope.GetProperty("freshness").GetProperty("instanceId").GetString());
        Assert.Equal(JsonValueKind.Array, envelope.GetProperty("evidence").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("pagination").ValueKind);
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("error").ValueKind);
    }

    private static void AssertContractV1ErrorEnvelope(JsonElement envelope, string expectedCode)
    {
        string rawJson = envelope.GetRawText();
        Assert.False(string.IsNullOrWhiteSpace(rawJson));
        Assert.Equal(JsonValueKind.Object, envelope.ValueKind);

        Assert.Equal(1, envelope.GetProperty("contractVersion").GetInt32());
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("scope").ValueKind);
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("measurement").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("coverage").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("freshness").ValueKind);
        Assert.Equal(JsonValueKind.Array, envelope.GetProperty("evidence").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("pagination").ValueKind);

        JsonElement error = envelope.GetProperty("error");
        Assert.Equal(JsonValueKind.Object, error.ValueKind);
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
    }

    /// <summary>
    /// Reader decorator failing aggregate and turn reads for one session key, standing in for a
    /// session whose projection is broken beyond recovery.
    /// </summary>
    private sealed class FaultingSessionReader(InMemoryRelationalStore inner, string failingSessionKey) : IRelationalStoreReader
    {
        public IReadOnlyList<string> Harnesses() => inner.Harnesses();

        public IReadOnlyList<StoredSource> Sources(string? harnessId = null) => inner.Sources(harnessId);

        public int CountRecords(string? harnessId = null) => inner.CountRecords(harnessId);

        /// <inheritdoc />
        public IReadOnlyList<StoredSourceCount> SourceRecordCounts() => inner.SourceRecordCounts();

        public CollectedRecord? RecordContentFree(string spanId) => inner.RecordContentFree(spanId);

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

        public IReadOnlyList<StoredToolDefinition> ToolDefinitions(string? spanId = null, string? sessionKey = null) => inner.ToolDefinitions(spanId, sessionKey);

        public IReadOnlyList<StoredAgentInvocation> AgentInvocations(string? spanId = null, string? sessionKey = null) => inner.AgentInvocations(spanId, sessionKey);

        public IReadOnlyList<StoredRecordSignals> RecordSignals(string? spanId = null, string? sessionKey = null) => inner.RecordSignals(spanId, sessionKey);

        public CollectedSession? GetSession(string sessionKey) => inner.GetSession(sessionKey);

        public IReadOnlyList<CollectedSession> Sessions(string? harnessId = null) => inner.Sessions(harnessId);

        public IReadOnlyList<StoredSessionModelUsage> SessionModelUsages(string? sessionKey = null) => inner.SessionModelUsages(sessionKey);

        public StoredFieldsView StoredFields(string sessionKey) => inner.StoredFields(sessionKey);

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
    }
}
