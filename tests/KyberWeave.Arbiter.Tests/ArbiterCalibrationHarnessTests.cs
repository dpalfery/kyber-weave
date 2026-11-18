using System.Net;
using KyberWeave.Core.Arbiter;
using KyberWeave.Tests;
using Xunit;
using static KyberWeave.Tests.ArbiterCalibrationHarness;

namespace KyberWeave.Arbiter.Tests;

/// <summary>
/// The Arbiter calibration run's offline helpers: which models it measures, how many cases it takes, and how one
/// request reports a network failure, a timeout or a cancelled run. None of it calls a provider. The labelled cases
/// are checked against the shipped rules, and the paid measurement runs, in <c>ArbiterCalibrationTests</c> in the
/// integration project.
/// </summary>
public sealed class ArbiterCalibrationHarnessTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ")]
    public void ParseModels_RefusesAValueThatNamesNoModel(string value)
    {
        Assert.Throws<ArgumentException>(() => ParseModels(value));
    }

    [Fact]
    public void ParseModels_UsesTheDefaultWhenUnset()
    {
        Assert.Equal(["jev-1.13.0", "jev-1.14.0"], ParseModels(null));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("many")]
    public void ApplyLimit_RefusesAValueThatIsNotAPositiveCount(string value)
    {
        Assert.Throws<ArgumentException>(() => ApplyLimit(Cases(3), value));
    }

    [Fact]
    public void ApplyLimit_TakesThatManyCases()
    {
        Assert.Equal(2, ApplyLimit(Cases(3), "2").Count);
    }

    [Fact]
    public async Task SendAsync_ReportsANetworkErrorAsAFailedCall()
    {
        using ThrowingHandler handler = new(new HttpRequestException("unreachable"));
        using HttpClient client = new(handler);

        (HttpStatusCode status, _) = await SendAsync(client, ProbeUri, "key", "{}", TestContext.Current.CancellationToken);

        Assert.Equal(TransportFailure, status);
    }

    [Fact]
    public async Task SendAsync_ReportsATimeoutAsAFailedCall()
    {
        using ThrowingHandler handler = new(new TaskCanceledException("timed out"));
        using HttpClient client = new(handler);

        (HttpStatusCode status, _) = await SendAsync(client, ProbeUri, "key", "{}", TestContext.Current.CancellationToken);

        Assert.Equal(TransportFailure, status);
    }

    [Fact]
    public async Task SendAsync_StopsWhenTheRunIsCancelled()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        using ThrowingHandler handler = new(new OperationCanceledException(cancellation.Token));
        using HttpClient client = new(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendAsync(client, ProbeUri, "key", "{}", cancellation.Token));
    }

    private static List<CalibrationCase> Cases(int count) =>
        [.. Enumerable.Range(1, count).Select(index => new CalibrationCase(
            "case-" + index,
            "rule",
            "label",
            "easy",
            new ArbiterFactSet(new Dictionary<string, ArbiterFact>(StringComparer.Ordinal))))];
}
