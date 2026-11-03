using System.Globalization;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Review;

/// <summary>
/// Turns findings and gate results into a binary Approve or RequestChanges verdict by fixed rule,
/// with no model in the loop.
/// </summary>
/// <remarks>
/// <para>
/// The judgement in a review is the lenses'; the gate is this. A verdict produced by the
/// same model that produced the findings cannot be audited, cannot be regression-tested, and
/// can legitimately differ between two runs over the same diff. This type exists so that the
/// last step is none of those things: same inputs, same verdict, every time, and every
/// decision attributable to a numbered rule.
/// </para>
/// <para>
/// It is deliberately pure — no clock, no filesystem, no process. The evaluation date is a
/// parameter rather than a read of the system clock precisely so suppression expiry is
/// testable rather than dependent on the day the suite runs.
/// </para>
/// </remarks>
public static class VerdictEngine
{
    /// <summary>A lens finding missing a field that makes it reviewable.</summary>
    public const string IncompleteFinding = "KW-REVIEW-001";

    /// <summary>A finding below the configured confidence floor.</summary>
    public const string LowConfidenceFinding = "KW-REVIEW-002";

    /// <summary>A finding removed by an active suppression.</summary>
    public const string SuppressedFinding = "KW-REVIEW-003";

    /// <summary>A suppression that has passed its expiry and no longer applies.</summary>
    public const string ExpiredSuppression = "KW-REVIEW-004";

    /// <summary>A blocking gate that did not pass.</summary>
    public const string BlockingGateFailed = "KW-REVIEW-005";

    /// <summary>A surviving critical finding.</summary>
    public const string CriticalFinding = "KW-REVIEW-006";

    /// <summary>Surviving major findings at or above the configured threshold.</summary>
    public const string MajorFindingThreshold = "KW-REVIEW-007";

    /// <summary>Measured coverage below the declared floor.</summary>
    public const string CoverageBelowFloor = "KW-REVIEW-010";

    /// <summary>A coverage floor is declared but no coverage report was produced.</summary>
    public const string CoverageMissing = "KW-REVIEW-012";

    private const string Subject = "review";

    /// <summary>Computes the verdict for one change.</summary>
    /// <param name="scope">What the change touches.</param>
    /// <param name="findings">Findings reported by the council, after its own confirmation pass.</param>
    /// <param name="gates">Results from the deterministic gate suite.</param>
    /// <param name="config">The host's review configuration.</param>
    /// <param name="today">The date suppression expiry is evaluated against.</param>
    /// <param name="coverage">Measured coverage, when the coverage gate produced any.</param>
    public static ReviewOutcome Evaluate(
        ReviewScope scope,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<GateResult> gates,
        ReviewConfig config,
        DateOnly today,
        CoverageResult? coverage = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(config);

        List<Diagnostic> diagnostics = [];
        ReviewPolicy policy = config.Policy;

        IReadOnlyList<ReviewSuppression> active = PartitionSuppressions(policy, today, diagnostics);
        (List<ReviewFinding> accepted, List<DroppedFinding> dropped) =
            Adjudicate(findings, policy, active, diagnostics);

        ReportCoverage(coverage, config.Coverage, diagnostics);

        // Evaluation order is the whole contract. Blocking gates run before findings
        // are weighed; if all gates pass and findings are below blocking thresholds,
        // the change is approved.
        ReviewVerdict verdict =
            EvaluateGates(gates, diagnostics)
            ?? EvaluateFindings(accepted, policy, diagnostics)
            ?? ReviewVerdict.Approve;

        return new ReviewOutcome(
            verdict,
            GradeRisk(accepted, gates),
            [.. accepted.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Confidence)],
            dropped,
            diagnostics);
    }

    private static IReadOnlyList<ReviewSuppression> PartitionSuppressions(
        ReviewPolicy policy,
        DateOnly today,
        List<Diagnostic> diagnostics)
    {
        List<ReviewSuppression> active = [];

        foreach (ReviewSuppression suppression in policy.Suppressions)
        {
            if (suppression.Expires < today)
            {
                diagnostics.Add(new Diagnostic(
                    ExpiredSuppression,
                    Severity.Warning,
                    $"Suppression '{suppression.Id}' expired on " +
                    $"{suppression.Expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} " +
                    "and no longer applies.",
                    Subject,
                    Hint: "Re-justify it with a new expiry, or delete it and fix the finding."));
                continue;
            }

            active.Add(suppression);
        }

        return active;
    }

    private static (List<ReviewFinding> Accepted, List<DroppedFinding> Dropped) Adjudicate(
        IReadOnlyList<ReviewFinding> findings,
        ReviewPolicy policy,
        IReadOnlyList<ReviewSuppression> active,
        List<Diagnostic> diagnostics)
    {
        List<ReviewFinding> accepted = [];
        List<DroppedFinding> dropped = [];

        foreach (ReviewFinding finding in findings)
        {
            string? missing = FirstMissingField(finding);
            if (missing is not null)
            {
                // The skeptic, as a schema constraint. A finding the engine would have to
                // complete on the reporter's behalf is one the engine would be inventing,
                // so it is removed rather than repaired — and the lens is named, because a
                // lens that does this repeatedly is the actual defect.
                dropped.Add(new DroppedFinding(
                    finding,
                    IncompleteFinding,
                    $"No {missing}. A finding without one cannot be verified."));
                diagnostics.Add(new Diagnostic(
                    IncompleteFinding,
                    Severity.Warning,
                    $"Lens '{finding.Lens}' reported '{finding.Id}' with no {missing}; it was dropped.",
                    finding.Lens,
                    finding.File,
                    "Every finding needs an excerpt, evidence, and a concrete failure scenario.",
                    finding.Line));
                continue;
            }

            if (finding.Confidence < policy.MinConfidence)
            {
                dropped.Add(new DroppedFinding(
                    finding,
                    LowConfidenceFinding,
                    $"Confidence {finding.Confidence} is below the floor of {policy.MinConfidence}."));
                diagnostics.Add(new Diagnostic(
                    LowConfidenceFinding,
                    Severity.Info,
                    $"'{finding.Id}' was dropped at confidence {finding.Confidence}, " +
                    $"below the floor of {policy.MinConfidence}.",
                    finding.Lens,
                    finding.File,
                    StartLine: finding.Line));
                continue;
            }

            ReviewSuppression? suppression = active
                .FirstOrDefault(s => string.Equals(s.Id, finding.Id, StringComparison.Ordinal));
            if (suppression is not null)
            {
                dropped.Add(new DroppedFinding(
                    finding,
                    SuppressedFinding,
                    $"Suppressed until " +
                    $"{suppression.Expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: " +
                    suppression.Reason));
                diagnostics.Add(new Diagnostic(
                    SuppressedFinding,
                    Severity.Info,
                    $"'{finding.Id}' is suppressed until " +
                    $"{suppression.Expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.",
                    finding.Lens,
                    finding.File,
                    suppression.Reason,
                    finding.Line));
                continue;
            }

            accepted.Add(finding);
        }

        return (accepted, dropped);
    }

    private static string? FirstMissingField(ReviewFinding finding)
    {
        if (string.IsNullOrWhiteSpace(finding.Excerpt)) return "excerpt";
        if (string.IsNullOrWhiteSpace(finding.Evidence)) return "evidence";
        if (string.IsNullOrWhiteSpace(finding.FailureScenario)) return "failure scenario";
        return null;
    }

    private static ReviewVerdict? EvaluateGates(
        IReadOnlyList<GateResult> gates,
        List<Diagnostic> diagnostics)
    {
        // A gate that did not apply is neither passed nor failed: it was never executed,
        // so its result is no evidence either way and the verdict with it equals the
        // verdict without it.
        bool failed = false;
        foreach (GateResult gate in gates.Where(g => g is { Blocking: true, Passed: false, NotApplicableReason: null }))
        {
            failed = true;
            diagnostics.Add(new Diagnostic(
                BlockingGateFailed,
                Severity.Error,
                $"Blocking gate '{gate.Id}' failed with exit code {gate.ExitCode}: {gate.Summary}",
                gate.Id,
                Hint: "Fix the cause and re-run the gate; a failing blocking gate cannot be approved past."));
        }

        return failed ? ReviewVerdict.RequestChanges : null;
    }

    private static ReviewVerdict? EvaluateFindings(
        List<ReviewFinding> accepted,
        ReviewPolicy policy,
        List<Diagnostic> diagnostics)
    {
        bool blocked = false;

        foreach (ReviewFinding finding in accepted.Where(f => f.Severity == ReviewSeverity.Critical))
        {
            blocked = true;
            diagnostics.Add(new Diagnostic(
                CriticalFinding,
                Severity.Error,
                $"{finding.Id}: {finding.Claim}",
                finding.Lens,
                finding.File,
                finding.Suggestion,
                finding.Line));
        }

        int majors = accepted.Count(f => f.Severity == ReviewSeverity.Major);
        if (majors >= policy.MajorCountBlocks)
        {
            blocked = true;
            diagnostics.Add(new Diagnostic(
                MajorFindingThreshold,
                Severity.Error,
                $"{majors} major findings reached the blocking threshold of {policy.MajorCountBlocks}.",
                Subject));
        }

        return blocked ? ReviewVerdict.RequestChanges : null;
    }

    private static void ReportCoverage(
        CoverageResult? coverage,
        ReviewCoverage floor,
        List<Diagnostic> diagnostics)
    {
        if (!floor.IsDeclared)
            return;

        if (coverage is null)
        {
            diagnostics.Add(new Diagnostic(
                CoverageMissing,
                Severity.Warning,
                "A coverage floor is declared but no coverage report was produced.",
                Subject,
                Hint: "Add --collect 'XPlat Code Coverage' to the test gate, or drop review.coverage."));
            return;
        }

        // Reported, never decisive. Coverage is a proxy for protection, and a verdict driven
        // by a proxy rewards padding the proxy — which the test-adequacy lens is there to
        // catch. The shortfall belongs in the report; the blocking decision belongs to the
        // findings.
        if (coverage.FileLinePercent < floor.FileLinePercent)
        {
            diagnostics.Add(new Diagnostic(
                CoverageBelowFloor,
                Severity.Warning,
                $"File line coverage {coverage.FileLinePercent:0.0}% is below the floor of " +
                $"{floor.FileLinePercent:0.0}%.",
                Subject));
        }

        if (coverage.ClassLinePercent < floor.ClassLinePercent)
        {
            diagnostics.Add(new Diagnostic(
                CoverageBelowFloor,
                Severity.Warning,
                $"Class line coverage {coverage.ClassLinePercent:0.0}% is below the floor of " +
                $"{floor.ClassLinePercent:0.0}%.",
                Subject));
        }
    }

    private static RiskGrade GradeRisk(
        List<ReviewFinding> accepted,
        IReadOnlyList<GateResult> gates)
    {
        if (accepted.Exists(f => f.Severity == ReviewSeverity.Critical) ||
            gates.Any(g => g is { Blocking: true, Passed: false, NotApplicableReason: null }))
        {
            return RiskGrade.High;
        }

        return accepted.Count > 0 ? RiskGrade.Medium : RiskGrade.Low;
    }
}
