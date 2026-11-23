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
        string? basis = null,
        string? claim = "The loop bound excludes the final element.") =>
        new(
            "correctness/off-by-one",
            lens,
            severity,
            9,
            "src/Foo.cs",
            12,
            "if (i < count)",
            claim,
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

    /// <summary>
    /// A claim that opens with the marker is dropped as pre-existing debt whatever its kind and cycle, with the rest of
    /// the claim's first line as the reason a manual <c>--drop</c> would have recorded.
    /// </summary>
    [Fact]
    public void APreExistingDebtClaimIsDroppedWhateverItsKind() =>
        Assert.Equal(
            "pre-existing debt: the file is not in this change.",
            FindingKindRouting.PreExistingDebtDropReason(Finding(
                ReviewSeverity.Major,
                FindingKind.Hardening,
                claim: "Pre-existing debt: the file is not in this change.")));

    /// <summary>The marker is matched whatever its case and leading whitespace, and only the first line is recorded.</summary>
    [Fact]
    public void APreExistingDebtClaimIsMatchedByPrefixAndFirstLine() =>
        Assert.Equal(
            "pre-existing debt: not touched here",
            FindingKindRouting.PreExistingDebtDropReason(Finding(
                ReviewSeverity.Minor,
                FindingKind.Defect,
                claim: "  \n pre-existing DEBT:   not touched here\nIt predates the branch.")));

    /// <summary>A claim that only mentions the marker later is an ordinary finding.</summary>
    [Fact]
    public void APreExistingDebtMarkerThatDoesNotOpenTheClaimIsIgnored()
    {
        Assert.Null(FindingKindRouting.PreExistingDebtDropReason(Finding(
            ReviewSeverity.Major,
            FindingKind.Hardening,
            claim: "This is not a Pre-existing debt: it is in this change.")));
        Assert.Null(FindingKindRouting.PreExistingDebtDropReason(Finding(
            ReviewSeverity.Major, FindingKind.Hardening, claim: null)));
    }
}
