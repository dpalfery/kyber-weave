using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The verdict engine is the reason a review verdict is auditable rather than a vibe, and
/// that claim only holds if the rules are pinned. Every test here is the same shape: fixed
/// inputs, one expected verdict, no clock and no filesystem.
/// </summary>
public class ReviewVerdictTests
{
    private static readonly DateOnly Today = new(2026, 8, 20);

    private static ReviewScope Scope(int lines = 100, params string[] paths) =>
        new(paths.Length == 0 ? ["src/Foo.cs"] : paths, lines);

    private static ReviewFinding Finding(
        ReviewSeverity severity = ReviewSeverity.Major,
        int confidence = 9,
        string id = "correctness/off-by-one",
        string? excerpt = "if (i < count)",
        string? evidence = "src/Foo.cs:12",
        string? failureScenario = "The last element is never processed.") =>
        new(
            id,
            "correctness",
            severity,
            confidence,
            "src/Foo.cs",
            12,
            excerpt,
            "The loop bound excludes the final element.",
            evidence,
            failureScenario,
            "Use <= or count + 1.");

    private static ReviewConfig Config(ReviewPolicy? policy = null) =>
        new() { Policy = policy ?? new ReviewPolicy { AlwaysHuman = ["**/auth/**"] } };

    private static ReviewOutcome Evaluate(
        IReadOnlyList<ReviewFinding>? findings = null,
        IReadOnlyList<GateResult>? gates = null,
        ReviewScope? scope = null,
        ReviewConfig? config = null,
        CoverageResult? coverage = null) =>
        VerdictEngine.Evaluate(
            scope ?? Scope(),
            findings ?? [],
            gates ?? [],
            config ?? Config(),
            Today,
            coverage);

    [Fact]
    public void ACleanChangeWithNoFindingsIsApprovedAtLowRisk()
    {
        ReviewOutcome outcome = Evaluate(gates: [new GateResult("test", true, 0, "passed")]);

        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.Equal(RiskGrade.Low, outcome.Risk);
        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public void OneCriticalFindingRequestsChanges()
    {
        ReviewOutcome outcome = Evaluate([Finding(ReviewSeverity.Critical)]);

        Assert.Equal(ReviewVerdict.RequestChanges, outcome.Verdict);
        Assert.Equal(RiskGrade.High, outcome.Risk);
        Assert.Contains(outcome.Diagnostics, d => d.Code == VerdictEngine.CriticalFinding);
    }

    [Fact]
    public void MajorFindingsBlockOnlyOnceTheyReachTheConfiguredThreshold()
    {
        ReviewFinding[] two = [Finding(id: "a"), Finding(id: "b")];
        ReviewFinding[] three = [Finding(id: "a"), Finding(id: "b"), Finding(id: "c")];

        Assert.Equal(ReviewVerdict.Approve, Evaluate(two).Verdict);

        ReviewOutcome blocked = Evaluate(three);
        Assert.Equal(ReviewVerdict.RequestChanges, blocked.Verdict);
        Assert.Contains(blocked.Diagnostics, d => d.Code == VerdictEngine.MajorFindingThreshold);
    }

    [Fact]
    public void AFailingBlockingGateRequestsChangesEvenWithNoFindings()
    {
        ReviewOutcome outcome = Evaluate(gates: [new GateResult("test", true, 1, "3 failed")]);

        Assert.Equal(ReviewVerdict.RequestChanges, outcome.Verdict);
        Assert.Equal(RiskGrade.High, outcome.Risk);
        Assert.Contains(outcome.Diagnostics, d => d.Code == VerdictEngine.BlockingGateFailed);
    }

    [Fact]
    public void AFailingNonBlockingGateDoesNotBlock()
    {
        Assert.Equal(
            ReviewVerdict.Approve,
            Evaluate(gates: [new GateResult("lint", false, 1, "2 warnings")]).Verdict);
    }

    /// <summary>
    /// The policy outranks the findings in both directions: a reserved path escalates a
    /// change with nothing wrong with it, and it escalates rather than merely adding to a
    /// changes-requested verdict, because "a human must look at this" is a different
    /// instruction from "fix this".
    /// </summary>
    [Fact]
    public void AReservedPathEscalatesRegardlessOfHowCleanTheReviewWas()
    {
        ReviewOutcome outcome = Evaluate(scope: Scope(10, "src/auth/TokenIssuer.cs"));

        Assert.Equal(ReviewVerdict.NeedsHuman, outcome.Verdict);
        Assert.Equal(RiskGrade.High, outcome.Risk);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Diagnostics, d => d.Code == VerdictEngine.ReservedPath);
    }

    [Fact]
    public void AReservedPathOutranksACriticalFinding()
    {
        ReviewOutcome outcome = Evaluate(
            [Finding(ReviewSeverity.Critical)],
            scope: Scope(10, "src/auth/TokenIssuer.cs"));

        Assert.Equal(ReviewVerdict.NeedsHuman, outcome.Verdict);
    }

    [Theory]
    [InlineData("**/auth/**", "src/auth/Token.cs", true)]
    [InlineData("**/auth/**", "auth/Token.cs", true)]
    [InlineData("**/auth/**", "src/authorization/Token.cs", false)]
    [InlineData("**/*secret*", "config/app.secrets.json", true)]
    [InlineData("products/kyber-squad/agents/**", "products/kyber-squad/agents/code-reviewer.md", true)]
    [InlineData("products/kyber-squad/agents/**", "products/kyber-squad/skills/x/SKILL.md", false)]
    [InlineData("*.yml", "kyber-weave.yml", true)]
    [InlineData("*.yml", "nested/kyber-weave.yml", false)]
    public void PathGlobMatchesWholeSegmentsAndNeverCrossesASlashWithASingleStar(
        string pattern,
        string path,
        bool expected) =>
        Assert.Equal(expected, PathGlob.IsMatch(pattern, path));

    [Fact]
    public void AChangeTooLargeToReviewEscalatesRatherThanBeingGraded()
    {
        ReviewOutcome outcome = Evaluate(scope: Scope(10_001));

        Assert.Equal(ReviewVerdict.NeedsHuman, outcome.Verdict);
        Assert.Contains(outcome.Diagnostics, d => d.Code == VerdictEngine.ChangeTooLarge);
    }

    /// <summary>
    /// Size is an attention limit, not a risk signal — a large clean diff must not be graded
    /// riskier than a small dirty one just for being large.
    /// </summary>
    [Fact]
    public void SizeAloneDoesNotRaiseTheRiskGrade()
    {
        Assert.Equal(RiskGrade.Low, Evaluate(scope: Scope(9_999)).Risk);
        Assert.Equal(RiskGrade.High, Evaluate([Finding(ReviewSeverity.Critical)], scope: Scope(3)).Risk);
    }

    [Theory]
    [InlineData(null, "src/Foo.cs:12", "scenario", "excerpt")]
    [InlineData("code", null, "scenario", "evidence")]
    [InlineData("code", "src/Foo.cs:12", null, "failure scenario")]
    [InlineData("   ", "src/Foo.cs:12", "scenario", "excerpt")]
    public void AFindingMissingItsProofIsDroppedAndTheLensIsNamed(
        string? excerpt,
        string? evidence,
        string? failureScenario,
        string expectedMissing)
    {
        ReviewOutcome outcome = Evaluate(
            [Finding(ReviewSeverity.Critical, excerpt: excerpt, evidence: evidence, failureScenario: failureScenario)]);

        Assert.Empty(outcome.Accepted);
        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);

        DroppedFinding dropped = Assert.Single(outcome.Dropped);
        Assert.Equal(VerdictEngine.IncompleteFinding, dropped.Code);
        Assert.Contains(expectedMissing, dropped.Reason, StringComparison.Ordinal);
        Assert.Contains(
            outcome.Diagnostics,
            d => d is { Code: VerdictEngine.IncompleteFinding, Subject: "correctness" });
    }

    [Fact]
    public void AFindingBelowTheConfidenceFloorIsDropped()
    {
        ReviewOutcome outcome = Evaluate([Finding(ReviewSeverity.Critical, confidence: 6)]);

        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.Empty(outcome.Accepted);
        Assert.Equal(VerdictEngine.LowConfidenceFinding, Assert.Single(outcome.Dropped).Code);
    }

    [Fact]
    public void AFindingAtTheConfidenceFloorIsKept()
    {
        ReviewOutcome outcome = Evaluate([Finding(ReviewSeverity.Critical, confidence: 7)]);

        Assert.Equal(ReviewVerdict.RequestChanges, outcome.Verdict);
        Assert.Empty(outcome.Dropped);
        Assert.Equal(7, Assert.Single(outcome.Accepted).Confidence);
    }

    [Fact]
    public void AFindingAboveTheConfidenceFloorIsKept()
    {
        ReviewOutcome outcome = Evaluate([Finding(ReviewSeverity.Critical, confidence: 8)]);

        Assert.Equal(ReviewVerdict.RequestChanges, outcome.Verdict);
        Assert.Empty(outcome.Dropped);
        Assert.Equal(8, Assert.Single(outcome.Accepted).Confidence);
    }

    [Fact]
    public void AnActiveSuppressionRemovesItsFinding()
    {
        ReviewConfig config = Config(new ReviewPolicy
        {
            Suppressions = [new ReviewSuppression("correctness/off-by-one", "Generated.", new DateOnly(2026, 11, 18))]
        });

        ReviewOutcome outcome = Evaluate([Finding(ReviewSeverity.Critical)], config: config);

        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.Equal(VerdictEngine.SuppressedFinding, Assert.Single(outcome.Dropped).Code);
    }

    /// <summary>
    /// The point of a dated suppression: on the day after it expires the finding comes back
    /// on its own, without anyone remembering to go looking for it.
    /// </summary>
    [Fact]
    public void AnExpiredSuppressionStopsApplyingAndSaysSo()
    {
        ReviewConfig config = Config(new ReviewPolicy
        {
            Suppressions = [new ReviewSuppression("correctness/off-by-one", "Generated.", Today.AddDays(-1))]
        });

        ReviewOutcome outcome = Evaluate([Finding(ReviewSeverity.Critical)], config: config);

        Assert.Equal(ReviewVerdict.RequestChanges, outcome.Verdict);
        Assert.Contains(outcome.Diagnostics, d => d.Code == VerdictEngine.ExpiredSuppression);
    }

    [Fact]
    public void ASuppressionExpiringTodayStillApplies()
    {
        ReviewConfig config = Config(new ReviewPolicy
        {
            Suppressions = [new ReviewSuppression("correctness/off-by-one", "Generated.", Today)]
        });

        Assert.Equal(ReviewVerdict.Approve, Evaluate([Finding(ReviewSeverity.Critical)], config: config).Verdict);
    }

    /// <summary>
    /// Coverage is reported, never decisive. A verdict driven by a proxy rewards padding the
    /// proxy, which is the defect the test-adequacy lens exists to catch.
    /// </summary>
    [Fact]
    public void CoverageBelowTheFloorIsReportedButDoesNotBlock()
    {
        ReviewConfig config = new()
        {
            Coverage = new ReviewCoverage(85, 85),
            Policy = new ReviewPolicy { AlwaysHuman = ["**/auth/**"] }
        };

        ReviewOutcome outcome = Evaluate(config: config, coverage: new CoverageResult(81.2, 90));

        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.Contains(
            outcome.Diagnostics,
            d => d is { Code: VerdictEngine.CoverageBelowFloor, Severity: Severity.Warning });
    }

    [Fact]
    public void AHostThatDeclaredNoReservedPathsIsToldSoRatherThanSilentlyUnprotected()
    {
        ReviewOutcome outcome = Evaluate(config: new ReviewConfig());

        Assert.Contains(outcome.Diagnostics, d => d.Code == VerdictEngine.NoReservedPathsDeclared);
    }

    [Fact]
    public void AcceptedFindingsAreOrderedMostSevereFirst()
    {
        ReviewOutcome outcome = Evaluate([
            Finding(ReviewSeverity.Minor, id: "a"),
            Finding(ReviewSeverity.Critical, id: "b"),
            Finding(id: "c")
        ]);

        Assert.Equal(["b", "c", "a"], outcome.Accepted.Select(f => f.Id));
    }

    /// <summary>
    /// Determinism is the whole claim. Two evaluations of identical input must agree, or
    /// nothing downstream can treat the verdict as evidence.
    /// </summary>
    [Fact]
    public void TheSameInputsProduceTheSameVerdictAndTheSameDiagnostics()
    {
        ReviewFinding[] findings = [Finding(ReviewSeverity.Critical), Finding(ReviewSeverity.Minor, id: "x")];
        GateResult[] gates = [new GateResult("test", true, 0, "passed")];

        ReviewOutcome first = Evaluate(findings, gates);
        ReviewOutcome second = Evaluate(findings, gates);

        Assert.Equal(first.Verdict, second.Verdict);
        Assert.Equal(first.Risk, second.Risk);
        Assert.Equal(
            first.Diagnostics.Select(d => d.Code),
            second.Diagnostics.Select(d => d.Code));
    }
}
