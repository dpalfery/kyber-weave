using Xunit;
using KyberWeave.Core.Review;

namespace KyberWeave.Tests;

/// <summary>When the review loop goes on, ends clean, or is stopped by a cap.</summary>
public sealed class ReviewLoopTests
{
    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 3, 2, 1, 0 })]
    public void NothingLeftToFixEndsTheLoopClean(int[] counts) =>
        Assert.Equal(ReviewLoopState.Clean, ReviewLoop.Decide(counts));

    [Theory]
    [InlineData(new[] { 2 })]
    [InlineData(new[] { 3, 2 })]
    [InlineData(new[] { 3, 3 })]
    [InlineData(new[] { 3, 3, 3 })]
    [InlineData(new[] { 3, 2, 1 })]
    [InlineData(new[] { 3, 2, 1, 1 })]
    [InlineData(new[] { 4, 3, 3, 2 })]
    public void ARunningLoopContinuesAndThreeFixRoundsAreReachable(int[] counts) =>
        Assert.Equal(ReviewLoopState.Continue, ReviewLoop.Decide(counts));

    [Theory]
    [InlineData(new[] { 1, 1, 1, 1 })]
    [InlineData(new[] { 3, 3, 4, 5 })]
    [InlineData(new[] { 2, 5, 5, 6 })]
    public void ThreeCyclesInARowWithoutADropStopTheLoop(int[] counts) =>
        Assert.Equal(ReviewLoopState.NoProgress, ReviewLoop.Decide(counts));

    [Fact]
    public void TheFifthCycleWithFindingsLeftStopsTheLoop() =>
        Assert.Equal(ReviewLoopState.CycleCap, ReviewLoop.Decide([5, 4, 3, 2, 1]));

    [Fact]
    public void ACleanFifthCycleIsStillClean() =>
        Assert.Equal(ReviewLoopState.Clean, ReviewLoop.Decide([5, 4, 3, 2, 0]));
}
