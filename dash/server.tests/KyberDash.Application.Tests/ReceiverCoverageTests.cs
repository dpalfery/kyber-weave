using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Services;
using KyberDash.Domain;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// Coverage <c>receiver</c> reports measured collector state. Settings flags alone do not
/// invent a listener, a fresh probe, or a clean error window.
/// </summary>
public class ReceiverCoverageTests
{
    [Fact]
    public void EnabledReceiverAdvertisesTheConfiguredPortWithoutClaimingItIsListening()
    {
        InMemoryRelationalStore store = new();
        store.SaveSettings(new SettingsView(false, false, 5, true, 4747));
        KyberBridgeEngine bridge = CreateBridge(store, new DashOptions { OtlpPort = 4319 });

        JsonElement receiver = bridge.GetCoverage().GetProperty("receiver");

        Assert.Equal("http://127.0.0.1:4319", receiver.GetProperty("endpoint").GetString());
        Assert.Equal(JsonValueKind.Null, receiver.GetProperty("listening").ValueKind);
        Assert.Equal(JsonValueKind.Null, receiver.GetProperty("hostedByUs").ValueKind);
        Assert.False(receiver.GetProperty("healthy").GetBoolean());
        Assert.Equal(JsonValueKind.Null, receiver.GetProperty("failures5m").ValueKind);
        Assert.Equal(JsonValueKind.Null, receiver.GetProperty("checkedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, receiver.GetProperty("lastAcceptedAt").ValueKind);
    }

    [Fact]
    public void BoundListenerIsHealthyOnlyWhenTheMeasuredFailureWindowIsEmpty()
    {
        InMemoryRelationalStore store = new();
        store.SaveSettings(new SettingsView(false, false, 5, true, 4747));
        OtlpListenerState listener = new();
        listener.NoteListening(4319);
        Mock<IOtlpCollectorService> collector = new();
        collector.SetupGet(c => c.IsPaused).Returns(false);
        collector.SetupGet(c => c.LastAcceptedAt).Returns("2026-11-22T00:00:00.000Z");
        collector.Setup(c => c.CountFailuresSince(It.IsAny<DateTimeOffset>())).Returns(0);
        KyberBridgeEngine bridge = CreateBridge(store, new DashOptions { OtlpPort = 4318 }, collector.Object, listener);

        JsonElement receiver = bridge.GetCoverage().GetProperty("receiver");

        Assert.True(receiver.GetProperty("listening").GetBoolean());
        Assert.True(receiver.GetProperty("hostedByUs").GetBoolean());
        Assert.True(receiver.GetProperty("healthy").GetBoolean());
        Assert.False(receiver.GetProperty("paused").GetBoolean());
        Assert.Equal(0, receiver.GetProperty("failures5m").GetInt32());
        Assert.False(string.IsNullOrEmpty(receiver.GetProperty("checkedAt").GetString()));
        Assert.Equal("http://127.0.0.1:4319", receiver.GetProperty("endpoint").GetString());
        Assert.Equal("2026-11-22T00:00:00.000Z", receiver.GetProperty("lastAcceptedAt").GetString());
    }

    [Fact]
    public void AdminPauseAndRecordedFailuresKeepTheReceiverUnhealthy()
    {
        InMemoryRelationalStore store = new();
        store.SaveSettings(new SettingsView(false, false, 5, true, 4747));
        OtlpListenerState listener = new();
        listener.NoteListening(4318);
        Mock<IOtlpCollectorService> collector = new();
        collector.SetupGet(c => c.IsPaused).Returns(true);
        collector.SetupGet(c => c.LastErrorCode).Returns("OTLP_MALFORMED_PAYLOAD");
        collector.SetupGet(c => c.LastError).Returns("malformed");
        collector.SetupGet(c => c.LastErrorAt).Returns("2026-11-22T00:01:00.000Z");
        collector.Setup(c => c.CountFailuresSince(It.IsAny<DateTimeOffset>())).Returns(2);
        KyberBridgeEngine bridge = CreateBridge(store, listener: listener, collector: collector.Object);

        JsonElement receiver = bridge.GetCoverage().GetProperty("receiver");

        Assert.True(receiver.GetProperty("paused").GetBoolean());
        Assert.True(receiver.GetProperty("listening").GetBoolean());
        Assert.False(receiver.GetProperty("healthy").GetBoolean());
        Assert.Equal(2, receiver.GetProperty("failures5m").GetInt32());
        Assert.Equal("OTLP_MALFORMED_PAYLOAD", receiver.GetProperty("lastCode").GetString());
        Assert.False(string.IsNullOrEmpty(receiver.GetProperty("checkedAt").GetString()));
    }

    [Fact]
    public void JobsPausedFromTheSameSettingsReadIsReportedWithTheReceiverFlag()
    {
        InMemoryRelationalStore store = new();
        store.SaveSettings(new SettingsView(false, true, 5, true, 4747));
        OtlpListenerState listener = new();
        listener.NoteListening(4318);
        KyberBridgeEngine bridge = CreateBridge(store, listener: listener);

        JsonElement receiver = bridge.GetCoverage().GetProperty("receiver");

        Assert.True(receiver.GetProperty("enabled").GetBoolean());
        Assert.True(receiver.GetProperty("paused").GetBoolean());
        Assert.False(receiver.GetProperty("healthy").GetBoolean());
    }

    private static KyberBridgeEngine CreateBridge(
        InMemoryRelationalStore store,
        DashOptions? options = null,
        IOtlpCollectorService? collector = null,
        IOtlpListenerState? listener = null)
    {
        FakePricingRepository pricing = new();
        return new KyberBridgeEngine(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing),
            options: options,
            collector: collector,
            listener: listener);
    }
}
