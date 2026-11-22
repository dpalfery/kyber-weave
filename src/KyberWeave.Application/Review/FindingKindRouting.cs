using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

/// <summary>
/// Routes a finding by its kind and by the number of the review cycle being decided.
/// </summary>
/// <remarks>
/// Severity and the fix level still decide a defect, and a seat that wrote no kind is read as a defect. A plan
/// objection never reaches a fix round, and a hardening survives only inside a shrinking window: cycle 1 keeps a
/// major or critical one the fix level would take, cycle 2 keeps only a critical one, and later cycles keep none.
/// Anything this drops becomes pull request follow-up work, never a <c>fix:</c> line.
/// </remarks>
public static class FindingKindRouting
{
    /// <summary>The diagnostic code the kind routing reports its drops and its missing kinds under.</summary>
    public const string Code = "KW-REVIEW-063";

    /// <summary>The lens whose findings are always defects, whatever kind the seat wrote.</summary>
    public const string SecurityLens = "security";

    /// <summary>What a plan objection with no quoted basis is recorded as breaking.</summary>
    public const string NoBasisQuoted = "no basis quoted";

    /// <summary>The kind the verdict routes <paramref name="finding"/> as: a security finding is always a defect.</summary>
    /// <param name="finding">The finding to read.</param>
    public static FindingKind RoutedKind(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return string.Equals(finding.Lens, SecurityLens, StringComparison.Ordinal)
            ? FindingKind.Defect
            : finding.Kind ?? FindingKind.Defect;
    }

    /// <summary>
    /// The reason the cycle drops <paramref name="finding"/>, or null when it stays in the verdict.
    /// </summary>
    /// <param name="finding">The finding to route.</param>
    /// <param name="policy">The policy whose fix level decides whether cycle 1 takes a hardening.</param>
    /// <param name="cycle">The one-based number of the cycle being decided.</param>
    public static string? DropReason(ReviewFinding finding, ReviewPolicy policy, int cycle)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycle, 1);

        return RoutedKind(finding) switch
        {
            FindingKind.Defect => null,
            FindingKind.PlanObjection =>
                $"pr note: plan objection: {(string.IsNullOrWhiteSpace(finding.Basis) ? NoBasisQuoted : finding.Basis)}",
            FindingKind.Hardening when HardeningSurvives(finding, policy, cycle) => null,
            FindingKind.Hardening => $"pr note: hardening outside the cycle {cycle} schedule",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "The finding's kind is not a defined value.")
        };
    }

    /// <summary>Whether the cycle's schedule keeps a hardening finding.</summary>
    private static bool HardeningSurvives(ReviewFinding finding, ReviewPolicy policy, int cycle) => cycle switch
    {
        // The first cycle is the one pass at everything worth doing now: a hardening has to be major or worse and
        // at or above the host's fix level to earn a place in it.
        1 => finding.Severity is ReviewSeverity.Major or ReviewSeverity.Critical && policy.FixRoundTakes(finding),

        // After the first fix round only a critical hardening is still worth another cycle; later cycles take none.
        2 => finding.Severity == ReviewSeverity.Critical,

        _ => false
    };
}
