using System.Reflection;
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
        new() { Policy = policy ?? new ReviewPolicy() };

    private static ReviewPolicy Preset(ReviewStrictness strictness)
    {
        ReviewPolicy preset = ReviewPolicy.ForStrictness(strictness);
        return new ReviewPolicy
        {
            Strictness = strictness,
            MinConfidence = preset.MinConfidence,
            MajorCountBlocks = preset.MajorCountBlocks
        };
    }

    private static ReviewOutcome Evaluate(
        IReadOnlyList<ReviewFinding>? findings = null,
        IReadOnlyList<GateResult>? gates = null,
        ReviewConfig? config = null,
        CoverageResult? coverage = null) =>
        VerdictEngine.Evaluate(
            findings ?? [],
            gates ?? [],
            config ?? Config(),
            Today,
            coverage);

    [Fact]
    public void StrictModeBlocksOnASingleLowerConfidenceMajorThatBalancedModeLetsThrough()
    {
        ReviewFinding major = Finding(ReviewSeverity.Major, confidence: 5);

        ReviewOutcome balanced = Evaluate([major], config: Config(Preset(ReviewStrictness.Balanced)));
        ReviewOutcome strict = Evaluate([major], config: Config(Preset(ReviewStrictness.Strict)));

        Assert.Equal(ReviewVerdict.Approve, balanced.Verdict);
        Assert.Equal(ReviewVerdict.RequestChanges, strict.Verdict);
        Assert.Contains(strict.Diagnostics, d => d.Code == VerdictEngine.MajorFindingThreshold);
    }

    [Fact]
    public void StrictModeStillNeverBlocksOnMinorFindings()
    {
        ReviewOutcome strict = Evaluate(
            [Finding(ReviewSeverity.Minor), Finding(ReviewSeverity.Minor, id: "correctness/other")],
            config: Config(Preset(ReviewStrictness.Strict)));

        Assert.Equal(ReviewVerdict.Approve, strict.Verdict);
    }

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

    /// <summary>
    /// A blocking gate the time limit or the council deadline stopped did not finish, so it says nothing about
    /// whether the change is broken. It is reported as a warning (KW-REVIEW-039) and neither requests changes
    /// nor raises the risk grade. Stopping for lack of time is a budget the council ran out of, not a defect.
    /// </summary>
    [Theory]
    [InlineData(GateOutcome.TimedOut)]
    [InlineData(GateOutcome.DeadlinePassed)]
    public void ABlockingGateStoppedForTimeDoesNotRequestChangesOrRaiseRisk(GateOutcome outcome)
    {
        ReviewOutcome result = Evaluate(gates: [new GateResult("test", true, -1, "stopped", Outcome: outcome)]);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.NotEqual(RiskGrade.High, result.Risk);
        Assert.Contains(result.Diagnostics, d => d is { Code: "KW-REVIEW-039", Severity: Severity.Warning });
    }

    /// <summary>
    /// A blocking gate whose program never started is still a reason to request changes: the missing tool has to
    /// be installed before the gate can say anything, so it is not treated like a stopped-for-time gate.
    /// </summary>
    [Fact]
    public void ABlockingGateThatNeverStartedStillRequestsChanges()
    {
        ReviewOutcome result = Evaluate(gates: [new GateResult("tests", true, -1, "could not start", Outcome: GateOutcome.NotStarted)]);

        Assert.Equal(ReviewVerdict.RequestChanges, result.Verdict);
    }

    [Fact]
    public void AFailingNonBlockingGateDoesNotBlock()
    {
        Assert.Equal(
            ReviewVerdict.Approve,
            Evaluate(gates: [new GateResult("lint", false, 1, "2 warnings")]).Verdict);
    }

    /// <summary>
    /// Q3 (issue #386, T2): Minor is the lowest finding severity — there is no Info
    /// finding severity, Info is a diagnostic severity — and Minor findings never block.
    /// Minor-only findings with green blocking gates approve; only the fixed
    /// Critical and Major-threshold rules decide.
    /// </summary>
    [Fact]
    public void MinorOnlyFindingsWithGreenBlockingGatesAreApproved()
    {
        ReviewOutcome outcome = Evaluate(
            [Finding(ReviewSeverity.Minor, id: "a"), Finding(ReviewSeverity.Minor, id: "b")],
            gates: [new GateResult("test", true, 0, "passed")]);

        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(RiskGrade.Medium, outcome.Risk);
        Assert.Equal(2, outcome.Accepted.Count);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Severity == Severity.Error);
    }

    /// <summary>
    /// Unlike Major findings, Minor findings have no blocking threshold: any number of
    /// surviving minors still approves when the blocking gates are green.
    /// </summary>
    [Fact]
    public void AnyNumberOfMinorFindingsStillApprovesWhenBlockingGatesAreGreen()
    {
        ReviewOutcome outcome = Evaluate(
            [
                Finding(ReviewSeverity.Minor, id: "a"),
                Finding(ReviewSeverity.Minor, id: "b"),
                Finding(ReviewSeverity.Minor, id: "c"),
                Finding(ReviewSeverity.Minor, id: "d")
            ],
            gates: [new GateResult("test", true, 0, "passed")]);

        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(4, outcome.Accepted.Count);
    }

    [Fact]
    public void ReviewVerdictDefinesOnlyApproveAndRequestChanges()
    {
        Assert.Equal(["Approve", "RequestChanges"], Enum.GetNames<ReviewVerdict>());

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        Assert.Null(typeof(VerdictEngine).GetField("ReservedPath", flags));
        Assert.Null(typeof(VerdictEngine).GetField("ChangeTooLarge", flags));
        Assert.Null(typeof(VerdictEngine).GetField("NoReservedPathsDeclared", flags));
    }

    [Fact]
    public void RetiredReservedPathRuleEmitsNoDiagnostics()
    {
        ReviewOutcome outcome = Evaluate(
            gates: [new GateResult("test", true, 0, "passed")]);

        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.Equal(RiskGrade.Low, outcome.Risk);
        Assert.Equal(0, outcome.ExitCode);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code is "KW-REVIEW-008" or "KW-REVIEW-011");
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
    public void RetiredSizeCeilingRuleEmitsNoDiagnostics()
    {
        ReviewOutcome outcome = Evaluate(
            gates: [new GateResult("test", true, 0, "passed")]);

        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.Equal(RiskGrade.Low, outcome.Risk);
        Assert.Equal(0, outcome.ExitCode);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code == "KW-REVIEW-009");
    }

    /// <summary>
    /// The risk grade comes from findings and gates, not from scope metadata.
    /// </summary>
    [Fact]
    public void RiskGradeComesFromFindings()
    {
        Assert.Equal(RiskGrade.Low, Evaluate().Risk);
        Assert.Equal(RiskGrade.High, Evaluate([Finding(ReviewSeverity.Critical)]).Risk);
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
            Policy = new ReviewPolicy()
        };

        ReviewOutcome outcome = Evaluate(config: config, coverage: new CoverageResult(81.2, 90));

        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.Contains(
            outcome.Diagnostics,
            d => d is { Code: VerdictEngine.CoverageBelowFloor, Severity: Severity.Warning });
    }

    [Fact]
    public void DefaultConfigEmitsNoReservedPathsDiagnostic()
    {
        ReviewOutcome outcome = Evaluate(config: new ReviewConfig());

        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code == "KW-REVIEW-011");
    }

    /// <summary>
    /// An undefined verdict must surface with its own exit code rather than silently masquerade as
    /// <see cref="ReviewVerdict.RequestChanges" />.
    /// </summary>
    [Fact]
    public void ExitCode_WhenVerdictIsUndefined_ReturnsTheDefensiveCode()
    {
        ReviewOutcome outcome = new((ReviewVerdict)99, RiskGrade.Low, [], [], []);

        Assert.Equal(2, outcome.ExitCode);
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
