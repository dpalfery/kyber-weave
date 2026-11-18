using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Review;

/// <summary>
/// The shared fake for <see cref="IReviewWorkTreeService"/> answers the way the service does: with no named base it
/// prefers the base the cycle being run recorded for this branch, and a tree it is not told about answers the path's
/// full spelling rather than the caller's.
/// </summary>
public sealed class FakeReviewWorkTreeServiceTests
{
    [Fact]
    public void APriorSplitUsesTheBaseTheCurrentCycleFolderRecorded()
    {
        ReviewCycleRecord finished = new(1, "/repo/.agent_scratchpad/task/review/cycle-1", "head-1", null, "branch", "recorded-base");
        FakeReviewWorkTreeService trees = new()
        {
            Identities = { ["/repo"] = new ReviewCycleIdentity("branch", "fresh-base", "head-2") },
            RecordedIdentities = { ["/repo/.agent_scratchpad/task/review/cycle-2"] = new ReviewCycleIdentity("branch", "recorded-base", "head-2") },
            Ancestors = ["head-1"],
        };

        PriorCycles prior = trees.Prior("/repo", null, [finished], "/repo/.agent_scratchpad/task/review/cycle-2");

        Assert.Equal([finished], prior.Valid);
    }

    [Fact]
    public void WithoutTheRecordedOverrideTheFreshlyComputedBaseDecidesTheSplit()
    {
        ReviewCycleRecord finished = new(1, "/repo/cycle-1", "head-1", null, "branch", "recorded-base");
        FakeReviewWorkTreeService trees = new()
        {
            Identities = { ["/repo"] = new ReviewCycleIdentity("branch", "fresh-base", "head-2") },
            Ancestors = ["head-1"],
        };

        PriorCycles prior = trees.Prior("/repo", null, [finished]);

        Assert.Empty(prior.Valid);
        Assert.Single(prior.Ignored);
    }

    [Fact]
    public void ATreeItIsNotToldAboutAnswersTheFullPath()
    {
        FakeReviewWorkTreeService trees = new() { WorkingDirectory = "/work" };

        Assert.Equal("/work/a/b", trees.Containing("a/b"));
        Assert.Throws<ArgumentException>(() => trees.Containing(" "));
    }
}
