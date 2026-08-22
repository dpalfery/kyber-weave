using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Review;

/// <summary>Everything the verdict engine decided, and why.</summary>
/// <param name="Verdict">The computed verdict.</param>
/// <param name="Risk">The risk grade, derived from findings rather than diff size.</param>
/// <param name="Accepted">Findings that survived adjudication, most severe first.</param>
/// <param name="Dropped">Findings the engine removed, each with the rule that removed it.</param>
/// <param name="Diagnostics">Every rule that fired, in evaluation order.</param>
public sealed record ReviewOutcome(
    ReviewVerdict Verdict,
    RiskGrade Risk,
    IReadOnlyList<ReviewFinding> Accepted,
    IReadOnlyList<DroppedFinding> Dropped,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The exit code a command should return for this verdict.</summary>
    /// <remarks>
    /// <see cref="ReviewVerdict.NeedsHuman"/> is not a failure and does not share an exit
    /// code with <see cref="ReviewVerdict.RequestChanges"/>. Collapsing them would make a
    /// change that merely touches a protected path indistinguishable, to any caller reading
    /// the exit code, from one the review actually rejected.
    /// </remarks>
    public int ExitCode => Verdict switch
    {
        ReviewVerdict.Approve => 0,
        ReviewVerdict.RequestChanges => 1,
        ReviewVerdict.NeedsHuman => 2,
        _ => 1
    };
}
