using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What the verdict keeps and drops by a finding's kind, and in which review cycle: the owner's schedule, pinned
/// cell by cell so a later change cannot widen a window quietly.
/// </summary>
public sealed class FindingKindRoutingTests
{
    private static readonly ReviewPolicy MinorLevel = new() { FixLevel = ReviewFixLevel.Minor };

    private static ReviewFinding Finding(
        ReviewSeverity severity,
        FindingKind? kind,
        string lens = "correctness",
        string? basis = null) =>
        new(
            "correctness/off-by-one",
            lens,
            severity,
            9,
            "src/Foo.cs",
            12,
            "if (i < count)",
            "The loop bound excludes the final element.",
            "src/Foo.cs:12",
            "The last element is never processed.",
            "Use <= or count + 1.",
            Kind: kind,
            Basis: basis);

    [Fact]
    public void Cycle1DropsAMinorHardening() =>
        Assert.Equal(
            "pr note: hardening outside the cycle 1 schedule",
            FindingKindRouting.DropReason(Finding(ReviewSeverity.Minor, FindingKind.Hardening), MinorLevel, cycle: 1));

    [Fact]
    public void Cycle1KeepsAMajorHardening() =>
        Assert.Null(FindingKindRouting.DropReason(
            Finding(ReviewSeverity.Major, FindingKind.Hardening), MinorLevel, cycle: 1));

    [Fact]
    public void Cycle1DropsAMajorHardeningTheFixLevelWouldNotTake() =>
        Assert.Equal(
            "pr note: hardening outside the cycle 1 schedule",
            FindingKindRouting.DropReason(
                Finding(ReviewSeverity.Major, FindingKind.Hardening),
                new ReviewPolicy { FixLevel = ReviewFixLevel.Critical },
                cycle: 1));

    [Fact]
    public void Cycle2DropsAMajorHardening() =>
        Assert.Equal(
            "pr note: hardening outside the cycle 2 schedule",
            FindingKindRouting.DropReason(Finding(ReviewSeverity.Major, FindingKind.Hardening), MinorLevel, cycle: 2));

    [Fact]
    public void Cycle2KeepsACriticalHardening() =>
        Assert.Null(FindingKindRouting.DropReason(
            Finding(ReviewSeverity.Critical, FindingKind.Hardening), MinorLevel, cycle: 2));

    [Fact]
    public void Cycle3DropsACriticalHardening() =>
        Assert.Equal(
            "pr note: hardening outside the cycle 3 schedule",
            FindingKindRouting.DropReason(Finding(ReviewSeverity.Critical, FindingKind.Hardening), MinorLevel, cycle: 3));

    [Fact]
    public void APlanObjectionIsDroppedInTheFirstCycleWithItsBasis() =>
        Assert.Equal(
            "pr note: plan objection: plan T2 says the selector stays in Application.",
            FindingKindRouting.DropReason(
                Finding(ReviewSeverity.Major, FindingKind.PlanObjection, basis: "plan T2 says the selector stays in Application."),
                MinorLevel,
                cycle: 1));

    [Fact]
    public void APlanObjectionWithNoBasisSaysSo() =>
        Assert.Equal(
            "pr note: plan objection: no basis quoted",
            FindingKindRouting.DropReason(Finding(ReviewSeverity.Major, FindingKind.PlanObjection), MinorLevel, cycle: 1));

    [Fact]
    public void ASecurityHardeningIsADefectAndIsKeptInCycle3()
    {
        ReviewFinding finding = Finding(ReviewSeverity.Critical, FindingKind.Hardening, lens: FindingKindRouting.SecurityLens);

        Assert.Equal(FindingKind.Defect, FindingKindRouting.RoutedKind(finding));
        Assert.Null(FindingKindRouting.DropReason(finding, MinorLevel, cycle: 3));
    }

    [Fact]
    public void AFindingsWithNoKindIsReadAsADefect()
    {
        ReviewFinding finding = Finding(ReviewSeverity.Minor, kind: null);

        Assert.Equal(FindingKind.Defect, FindingKindRouting.RoutedKind(finding));
        Assert.Null(FindingKindRouting.DropReason(finding, MinorLevel, cycle: 3));
    }

    [Fact]
    public void ADefectIsNeverDroppedByAKindSchedule() =>
        Assert.Null(FindingKindRouting.DropReason(Finding(ReviewSeverity.Critical, FindingKind.Defect), MinorLevel, cycle: 3));
}
