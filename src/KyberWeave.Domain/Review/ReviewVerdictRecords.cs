namespace KyberWeave.Core.Review;

/// <summary>Findings the decision model merged as one defect: the one kept and the ids folded into it.</summary>
/// <param name="Kept">The finding that stays: the highest severity, then the highest confidence, then the most evidence.</param>
/// <param name="Merged">The findings folded into it, each with the confidence of the answer that joined it.</param>
public sealed record MergedFindings(ReviewFinding Kept, IReadOnlyList<(ReviewFinding Finding, double? Confidence)> Merged);

/// <summary>A refutation candidate after the claim check: whether a refuter is still needed, and why.</summary>
/// <param name="Finding">The critical or major finding.</param>
/// <param name="NeedsRefuter">False only when the model confidently found the cited code supports the claim.</param>
/// <param name="Note">What the check found, for the report; null when the model gave no confident answer.</param>
public sealed record ClaimCheck(ReviewFinding Finding, bool NeedsRefuter, string? Note);
