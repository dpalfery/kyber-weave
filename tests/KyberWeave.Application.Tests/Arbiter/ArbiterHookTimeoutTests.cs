using KyberWeave.Core.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The hook timeout every renderer writes comes from the provider budget plus two seconds of headroom, and a floor
/// that a burst of concurrent dispatches cannot blow through.
/// </summary>
/// <remarks>
/// A live Copilot CLI run killed 55 hooks at five seconds: the shipped provider budget is three seconds, so
/// <c>ceil(3000 / 1000) + 2</c> rendered five. Nearly every kill landed in a burst, when the conductor dispatched
/// twelve to twenty review lenses at once on a machine already building. A hook therefore waits at least
/// <see cref="ArbiterHookTimeout.MinimumRenderedSeconds"/> seconds, whatever the provider budget.
/// </remarks>
public sealed class ArbiterHookTimeoutTests
{
    [Fact]
    public void TheMinimumIsThirtySeconds() =>
        Assert.Equal(30, ArbiterHookTimeout.MinimumRenderedSeconds);

    [Fact]
    public void TheShippedProviderBudget_RendersTheThirtySecondFloor() =>
        Assert.Equal(
            ArbiterHookTimeout.MinimumRenderedSeconds,
            ArbiterHookTimeout.SecondsFor(ArbiterProviderConfig.DefaultNone.TimeoutMs));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1500)]
    [InlineData(3000)]
    [InlineData(27_999)]
    public void AProviderBudgetUnderTheFloor_RendersTheFloor(int providerTimeoutMs) =>
        Assert.Equal(ArbiterHookTimeout.MinimumRenderedSeconds, ArbiterHookTimeout.SecondsFor(providerTimeoutMs));

    [Fact]
    public void AProviderBudgetOverTheFloor_KeepsTheTwoSecondHeadroom()
    {
        // ceil(30000 / 1000) + 2
        Assert.Equal(32, ArbiterHookTimeout.SecondsFor(30_000));
        Assert.Equal(48, ArbiterHookTimeout.SecondsFor(45_001));
    }

    [Fact]
    public void AFitsCheck_MeasuresTheRenderedFloor()
    {
        // The rendered floor is 30 s, so a 28 s provider budget still leaves two seconds of evaluation room.
        Assert.True(ArbiterHookTimeout.Fits(3000, 28_000));
        Assert.False(ArbiterHookTimeout.Fits(3000, 29_000));
    }
}
