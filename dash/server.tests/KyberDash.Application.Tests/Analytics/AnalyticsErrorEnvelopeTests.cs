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

/// <summary>Verifies <see cref="KyberBridgeEngine.ExecuteAnalytics"/> maps faults to contract-v1 error envelopes.</summary>
public sealed class AnalyticsErrorEnvelopeTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Store exceptions at any stage map to HTTP 503 with <c>ANALYTICS_STORE_UNAVAILABLE</c>.</summary>
    [Theory]
    [MemberData(nameof(StoreFaultCases))]
    public void StoreFaultReturnsUnavailableContractV1Envelope(Exception storeException, bool failDuringGeneration)
    {
        (Mock<IRelationalStoreReader> reader, _, Mock<IOperationalStore> operational) = CreateBaseStoreMocks();
        if (failDuringGeneration)
        {
            operational.Setup(store => store.GetWriteMarker()).Throws(storeException);
        }
        else
        {
            reader.Setup(store => store.SessionAttributions())
                .Throws(storeException);
        }

        KyberBridgeEngine engine = CreateEngine(reader.Object, operational: operational.Object);

        (int statusCode, JsonElement envelope) = engine.ExecuteAnalytics("query", null);

        Assert.Equal(503, statusCode);
        AssertContractV1ErrorEnvelope(envelope, "ANALYTICS_STORE_UNAVAILABLE");
    }

    /// <summary>Argument and validation faults map to HTTP 400 with <c>ANALYTICS_INVALID_ARGUMENT</c>.</summary>
    [Theory]
    [MemberData(nameof(InvalidArgumentCases))]
    public void InvalidArgumentFaultReturnsBadRequestContractV1Envelope(Exception argumentException)
    {
        (Mock<IRelationalStoreReader> reader, Mock<IProjectionStore> projections, Mock<IOperationalStore> operational) = CreateBaseStoreMocks();
        reader.Setup(store => store.SessionAttributions())
            .Throws(argumentException);
        KyberBridgeEngine engine = CreateEngine(reader.Object, projections.Object, operational.Object);

        (int statusCode, JsonElement envelope) = engine.ExecuteAnalytics("query", null);

        Assert.Equal(400, statusCode);
        AssertContractV1ErrorEnvelope(envelope, "ANALYTICS_INVALID_ARGUMENT");
    }

    /// <summary>A blank operation passed directly to the bridge maps to <c>ANALYTICS_INVALID_ARGUMENT</c>.</summary>
    [Fact]
    public void BlankOperationReturnsInvalidArgumentContractV1Envelope()
    {
        InMemoryRelationalStore store = new();
        KyberBridgeEngine engine = CreateEngine(store, store, store);

        (int statusCode, JsonElement envelope) = engine.ExecuteAnalytics("   ", null);

        Assert.Equal(400, statusCode);
        AssertContractV1ErrorEnvelope(envelope, "ANALYTICS_INVALID_ARGUMENT");
    }

    /// <summary>Unexpected errors map to HTTP 500 with a non-empty contract-v1 <c>ANALYTICS_INTERNAL</c> envelope.</summary>
    [Fact]
    public void UnexpectedFaultReturnsNonEmptyInternalErrorContractV1Envelope()
    {
        (Mock<IRelationalStoreReader> reader, Mock<IProjectionStore> projections, Mock<IOperationalStore> operational) = CreateBaseStoreMocks();
        reader.Setup(store => store.SessionAttributions())
            .Throws(new InvalidOperationException("Corrupted rollup state"));
        KyberBridgeEngine engine = CreateEngine(reader.Object, projections.Object, operational.Object);

        (int statusCode, JsonElement envelope) = engine.ExecuteAnalytics("query", null);

        Assert.Equal(500, statusCode);
        AssertContractV1ErrorEnvelope(envelope, "ANALYTICS_INTERNAL");
    }

    /// <summary>Store exception cases mapped to <c>ANALYTICS_STORE_UNAVAILABLE</c>.</summary>
    public static TheoryData<Exception, bool> StoreFaultCases() =>
        new()
        {
            { new SqliteExecutionException("sqlite3 exited with code 1", 1, "database is locked"), true },
            { new CircuitBreakerOpenException("The SQLite circuit breaker is open."), false },
            { new IOException("disk read error"), false },
            { new TimeoutException("sqlite3 timed out"), true },
        };

    /// <summary>Invalid argument and validation exception cases mapped to <c>ANALYTICS_INVALID_ARGUMENT</c>.</summary>
    public static TheoryData<Exception> InvalidArgumentCases() =>
        new()
        {
            new ArgumentException("Invalid harness filter."),
            new ArgumentOutOfRangeException("limit", "Limit must be positive."),
            new DashValidationException("VALIDATION_ERROR", "Malformed filter argument."),
        };

    private static (Mock<IRelationalStoreReader> Reader, Mock<IProjectionStore> Projections, Mock<IOperationalStore> Operational) CreateBaseStoreMocks()
    {
        Mock<IRelationalStoreReader> reader = new();
        reader.Setup(store => store.Sessions(It.IsAny<string?>())).Returns(Array.Empty<CollectedSession>());
        reader.Setup(store => store.CountRecords(It.IsAny<string?>())).Returns(0);
        reader.Setup(store => store.Harnesses()).Returns(Array.Empty<string>());
        reader.Setup(store => store.SessionAttributions()).Returns(Array.Empty<StoredSessionAttribution>());

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
        IProjectionStore? projections = null,
        IOperationalStore? operational = null)
    {
        InMemoryRelationalStore memory = new();
        IProjectionStore resolvedProjections = projections ?? memory;
        IOperationalStore resolvedOperational = operational ?? memory;
        FakePricingRepository pricing = new();
        return new KyberBridgeEngine(
            reader,
            resolvedProjections,
            resolvedOperational,
            pricing,
            new CanonSessionProjectorEngine(reader, resolvedProjections, resolvedOperational, pricing),
            new CanonRollupsEngine(reader, resolvedProjections, pricing),
            clock: () => FixedTimestamp,
            instanceId: "test-instance");
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
}
