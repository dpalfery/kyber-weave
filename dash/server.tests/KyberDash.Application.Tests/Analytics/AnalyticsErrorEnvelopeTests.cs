using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
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
        Mock<ICanonStoreRepository> store = CreateBaseStoreMock();
        if (failDuringGeneration)
        {
            store.Setup(repository => repository.ComputeStoreGeneration()).Throws(storeException);
        }
        else
        {
            store.Setup(repository => repository.ListSessions(It.IsAny<string?>())).Throws(storeException);
        }

        KyberBridgeEngine engine = CreateEngine(store.Object);

        (int statusCode, JsonElement envelope) = engine.ExecuteAnalytics("query", null);

        Assert.Equal(503, statusCode);
        AssertContractV1ErrorEnvelope(envelope, "ANALYTICS_STORE_UNAVAILABLE");
    }

    /// <summary>Argument and validation faults map to HTTP 400 with <c>ANALYTICS_INVALID_ARGUMENT</c>.</summary>
    [Theory]
    [MemberData(nameof(InvalidArgumentCases))]
    public void InvalidArgumentFaultReturnsBadRequestContractV1Envelope(Exception argumentException)
    {
        Mock<ICanonStoreRepository> store = CreateBaseStoreMock();
        store.Setup(repository => repository.ListSessions(It.IsAny<string?>())).Throws(argumentException);
        KyberBridgeEngine engine = CreateEngine(store.Object);

        (int statusCode, JsonElement envelope) = engine.ExecuteAnalytics("query", null);

        Assert.Equal(400, statusCode);
        AssertContractV1ErrorEnvelope(envelope, "ANALYTICS_INVALID_ARGUMENT");
    }

    /// <summary>A blank operation passed directly to the bridge maps to <c>ANALYTICS_INVALID_ARGUMENT</c>.</summary>
    [Fact]
    public void BlankOperationReturnsInvalidArgumentContractV1Envelope()
    {
        FakeCanonStoreRepository store = new();
        KyberBridgeEngine engine = CreateEngine(store);

        (int statusCode, JsonElement envelope) = engine.ExecuteAnalytics("   ", null);

        Assert.Equal(400, statusCode);
        AssertContractV1ErrorEnvelope(envelope, "ANALYTICS_INVALID_ARGUMENT");
    }

    /// <summary>Unexpected errors map to HTTP 500 with a non-empty contract-v1 <c>ANALYTICS_INTERNAL</c> envelope.</summary>
    [Fact]
    public void UnexpectedFaultReturnsNonEmptyInternalErrorContractV1Envelope()
    {
        Mock<ICanonStoreRepository> store = CreateBaseStoreMock();
        store.Setup(repository => repository.ListSessions(It.IsAny<string?>()))
            .Throws(new InvalidOperationException("Corrupted rollup state"));
        KyberBridgeEngine engine = CreateEngine(store.Object);

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

    private static Mock<ICanonStoreRepository> CreateBaseStoreMock()
    {
        Mock<ICanonStoreRepository> store = new();
        store.Setup(repository => repository.ComputeStoreGeneration()).Returns(42u);
        store.Setup(repository => repository.RecordsWriteMarker()).Returns(0L);
        store.Setup(repository => repository.CountRecords()).Returns(0);
        store.Setup(repository => repository.HasCurrentDetectorVersion()).Returns(true);
        store.Setup(repository => repository.GetMetadata("projection_records_marker")).Returns("0");
        store.Setup(repository => repository.GetMetadata("projection_schema_version")).Returns("24");
        store.Setup(repository => repository.GetMetadata("schema_version")).Returns("24");
        store.Setup(repository => repository.ListSessions(It.IsAny<string?>())).Returns([]);
        store.Setup(repository => repository.ListHarnessRollups()).Returns([]);
        store.Setup(repository => repository.ListFindings(It.IsAny<string?>(), It.IsAny<string?>())).Returns([]);
        store.Setup(repository => repository.ListObservedHarnesses()).Returns([]);
        return store;
    }

    private static KyberBridgeEngine CreateEngine(ICanonStoreRepository store)
    {
        FakeSettingsRepository settings = new();
        FakePricingRepository pricing = new();
        return new KyberBridgeEngine(
            store,
            settings,
            pricing,
            new CanonSessionProjectorEngine(store, pricing),
            new CanonRollupsEngine(store, pricing),
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
