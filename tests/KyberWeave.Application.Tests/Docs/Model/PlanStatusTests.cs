using KyberWeave.Core.Docs.Model;
using Xunit;

namespace KyberWeave.Tests.Docs.Model;

/// <summary>
/// Every spelling of a plan's status and every body form it is written in resolves to the same word, so the
/// lifecycle rules cannot read the same line differently.
/// </summary>
public sealed class PlanStatusTests
{
    [Theory]
    [InlineData("**Status:** Ready", PlanStatus.Ready)]
    [InlineData("**Status: Ready**", PlanStatus.Ready)]
    [InlineData("**Status**: Ready", PlanStatus.Ready)]
    [InlineData("**Status:** Ready, 2026-11-26", PlanStatus.Ready)]
    [InlineData("**Status: Ready, 2026-11-26.**", PlanStatus.Ready)]
    [InlineData("**Status: In progress.**", PlanStatus.InProgress)]
    [InlineData("**Status: In-progress.**", PlanStatus.InProgress)]
    [InlineData("**Status:** Review-required", PlanStatus.ReviewRequired)]
    [InlineData("**Status:** Archived, 2026-11-26", PlanStatus.Archived)]
    public void EveryBodyFormParsesToItsCanonicalStatus(string line, string expected)
    {
        Assert.Equal(expected, PlanStatus.ParseBodyStatus($"# Plan\n\n{line}\n\nBody text.\n"));
    }

    [Fact]
    public void AStatusWordOutsideTheStatusLineIsNotThePlansStatus()
    {
        const string body = """
            # Plan

            ## Notes

            The finalize step writes `**Status: Ready, 2026-11-26.**` into the plan.
            """;

        Assert.Null(PlanStatus.ParseBodyStatus(body));
    }

    [Theory]
    [InlineData("Ready", PlanStatus.Ready)]
    [InlineData("In-progress", PlanStatus.InProgress)]
    [InlineData("Review required", PlanStatus.ReviewRequired)]
    [InlineData("Ready, 2026-11-26", PlanStatus.Ready)]
    [InlineData("Complete", PlanStatus.Completed)]
    [InlineData("Archived — complete 2026-11-26", PlanStatus.Archived)]
    public void AStatusWordParsesFromAnIndexCell(string value, string expected)
    {
        Assert.Equal(expected, PlanStatus.Parse(value));
    }

    [Theory]
    [InlineData(PlanStatus.Ready, true)]
    [InlineData(PlanStatus.InProgress, true)]
    [InlineData(PlanStatus.Blocked, false)]
    [InlineData(PlanStatus.ReviewRequired, false)]
    [InlineData(PlanStatus.Draft, false)]
    public void ActiveIsReadyOrInProgress(string status, bool expected)
    {
        Assert.Equal(expected, PlanStatus.IsActive(status));
    }

    [Theory]
    [InlineData(PlanStatus.ReviewRequired, true)]
    [InlineData(PlanStatus.Blocked, true)]
    [InlineData(PlanStatus.Ready, false)]
    [InlineData(PlanStatus.InProgress, false)]
    public void InReviewIsReviewRequiredOrBlocked(string status, bool expected)
    {
        Assert.Equal(expected, PlanStatus.IsInReview(status));
    }

    [Theory]
    [InlineData(PlanStatus.Draft, true)]
    [InlineData(PlanStatus.Ready, true)]
    [InlineData(PlanStatus.InProgress, true)]
    [InlineData(PlanStatus.Blocked, true)]
    [InlineData(PlanStatus.ReviewRequired, true)]
    [InlineData(PlanStatus.Completed, false)]
    [InlineData(PlanStatus.Archived, false)]
    [InlineData(PlanStatus.Superseded, false)]
    public void OpenIsEveryStatusBeforeCloseout(string status, bool expected)
    {
        Assert.Equal(expected, PlanStatus.IsOpen(status));
    }

    [Theory]
    [InlineData(PlanStatus.Completed, true)]
    [InlineData(PlanStatus.Archived, true)]
    [InlineData(PlanStatus.Superseded, true)]
    [InlineData(PlanStatus.ReviewRequired, false)]
    [InlineData("COMPLETE", true)]
    public void ClosedIsCompletedArchivedOrSuperseded(string status, bool expected)
    {
        Assert.Equal(expected, PlanStatus.IsClosed(status));
    }
}
