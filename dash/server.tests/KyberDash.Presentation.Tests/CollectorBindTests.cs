using System.Net;
using System.Net.Sockets;
using KyberDash.Api;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace KyberDash.Presentation.Tests;

/// <summary>
/// Loopback bind decisions: the dashboard URL is not the collector port, a bind failure names the
/// address that failed, and each port serves only its own routes.
/// </summary>
public class CollectorBindTests
{
    [Fact]
    public void WebUrlSkipsAnEarlierCollectorAddress()
    {
        Uri selected = LoopbackBinding.SelectWebUrl(
            ["http://127.0.0.1:4318", "http://127.0.0.1:4747"],
            configuredWebPort: 4747,
            otlpPort: 4318);

        Assert.Equal(4747, selected.Port);
    }

    [Fact]
    public void EphemeralWebUrlIsNotTheCollectorPort()
    {
        Uri selected = LoopbackBinding.SelectWebUrl(
            ["http://127.0.0.1:4318", "http://127.0.0.1:34567"],
            configuredWebPort: 0,
            otlpPort: 4318);

        Assert.Equal(34567, selected.Port);
    }

    [Fact]
    public void ConfiguredCollectorPortIsNotReplacedByTheWebPort()
    {
        int selected = LoopbackBinding.SelectOtlpPort(
            ["http://127.0.0.1:4747"],
            configuredOtlpPort: 4319,
            webPort: 4747);

        Assert.Equal(0, selected);
    }

    [Fact]
    public void BindFailureNamesTheCollectorAddressRatherThanTheWebPort()
    {
        SocketException socket = new((int)SocketError.AddressAlreadyInUse);
        AddressInUseException address = new("Address already in use", socket);
        IOException failure = new(
            "Failed to bind to address http://127.0.0.1:4318: address already in use.",
            address);
        DashRuntimeSettings runtime = new() { WebPort = 4747, OtlpPort = 4318, OtlpEnabled = true };

        Assert.True(LoopbackBinding.IsBindFailure(failure));
        string message = LoopbackBinding.DescribeBindFailure(failure, runtime);
        Assert.Contains("http://127.0.0.1:4318", message, StringComparison.Ordinal);
        Assert.DoesNotContain("4747", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindFailureWithoutAUrlNamesBothPorts()
    {
        IOException failure = new("address already in use.", new SocketException((int)SocketError.AddressAlreadyInUse));
        DashRuntimeSettings runtime = new() { WebPort = 4747, OtlpPort = 4318, OtlpEnabled = true };

        string message = LoopbackBinding.DescribeBindFailure(failure, runtime);

        Assert.Contains("http://127.0.0.1:4747", message, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:4318", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BusyDefaultCollectorPortFallsBackOnlyWhenItWasNotExplicit()
    {
        Assert.True(LoopbackBinding.ShouldUseEphemeralOtlpPort(otlpEnabled: true, otlpPortExplicit: false, preferredPortFree: false));
        Assert.False(LoopbackBinding.ShouldUseEphemeralOtlpPort(otlpEnabled: true, otlpPortExplicit: true, preferredPortFree: false));
        Assert.False(LoopbackBinding.ShouldUseEphemeralOtlpPort(otlpEnabled: true, otlpPortExplicit: false, preferredPortFree: true));
    }

    [Fact]
    public void CollectorBindFailureRetriesOnAnEphemeralPortUnlessThePortWasExplicit()
    {
        SocketException socket = new((int)SocketError.AddressAlreadyInUse);
        IOException collectorFailure = new("Failed to bind to address http://127.0.0.1:4318: address already in use.", socket);
        IOException webFailure = new("Failed to bind to address http://127.0.0.1:4747: address already in use.", socket);
        DashRuntimeSettings runtime = new() { WebPort = 4747, OtlpPort = 4318, OtlpEnabled = true };

        Assert.True(LoopbackBinding.ShouldRetryOtlpBindOnEphemeral(collectorFailure, runtime));
        Assert.False(LoopbackBinding.ShouldRetryOtlpBindOnEphemeral(webFailure, runtime));
        Assert.True(LoopbackBinding.ShouldRetryOtlpBindOnEphemeral(
            new IOException("address already in use.", socket),
            runtime));

        runtime.OtlpPortExplicit = true;
        Assert.False(LoopbackBinding.ShouldRetryOtlpBindOnEphemeral(collectorFailure, runtime));
    }

    [Fact]
    public void OccupiedLoopbackPortIsNotFree()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.ExclusiveAddressUse = true;
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen(1);
        int port = ((IPEndPoint)socket.LocalEndPoint!).Port;

        Assert.False(LoopbackBinding.LoopbackPortIsFree(port));
        socket.Close();
        Assert.True(LoopbackBinding.LoopbackPortIsFree(port));
    }

    [Fact]
    public void CollectorPortDoesNotServeTheDashboardApi()
    {
        DashRuntimeSettings runtime = new()
        {
            WebPort = 4747,
            OtlpPort = 4318,
            OtlpEnabled = true,
        };

        Assert.False(LoopbackBinding.RequestAllowed("/api/kyber/coverage", 4318, runtime));
        Assert.True(LoopbackBinding.RequestAllowed("/v1/traces", 4318, runtime));
        Assert.False(LoopbackBinding.RequestAllowed("/v1/logs", 4747, runtime));
        Assert.True(LoopbackBinding.RequestAllowed("/api/kyber/capture", 4747, runtime));
        Assert.True(LoopbackBinding.RequestAllowed("/", 4747, runtime));
        Assert.False(LoopbackBinding.RequestAllowed("/api/kyber/coverage", 9999, runtime));
        Assert.False(LoopbackBinding.RequestAllowed("/v1/traces", 9999, runtime));
        Assert.True(LoopbackBinding.RequestAllowed("/api/kyber/coverage", 0, runtime));
    }

    [Fact]
    public void OtlpOnlyServesCollectorRoutesAlone()
    {
        DashRuntimeSettings runtime = new() { OtlpOnly = true, OtlpPort = 4318, WebPort = 4747 };

        Assert.True(LoopbackBinding.RequestAllowed("/v1/traces", 4318, runtime));
        Assert.True(LoopbackBinding.RequestAllowed("/healthz", 4318, runtime));
        Assert.True(LoopbackBinding.RequestAllowed("/healthz/", 4318, runtime));
        Assert.False(LoopbackBinding.RequestAllowed("/api/kyber/meta", 4318, runtime));
    }
}
