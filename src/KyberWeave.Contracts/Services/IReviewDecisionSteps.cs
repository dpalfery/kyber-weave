using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>The decision-model steps of <c>review verdict</c> and <c>review seats</c>, each asked on the seat files' own text.</summary>
public interface IReviewDecisionSteps
{
    /// <summary>Asks, for each pair of findings on the same file within 20 lines, whether they name the same defect, and folds every confident pair into one finding.</summary>
    Task<(List<ReviewFinding> Kept, List<MergedFindings> Merged)> MergeDuplicatesAsync(
        IReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default);

    /// <summary>Asks whether each minor finding is a nitpick and drops the confident ones.</summary>
    Task<(List<ReviewFinding> Kept, List<(ReviewFinding Finding, double? Confidence)> Nitpicks)> DropNitpicksAsync(
        IReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default);

    /// <summary>Checks each refutation candidate's claim against the cited code before a refuter is sent.</summary>
    Task<IReadOnlyList<ClaimCheck>> CheckClaimsAsync(
        IReviewDecisionModel model, string repositoryRoot, IReadOnlyList<ReviewFinding> candidates, CancellationToken cancellationToken = default);

    /// <summary>Asks whether each finding's stated severity matches its stated impact. Advisory only.</summary>
    Task<IReadOnlyList<(ReviewFinding Finding, string Direction, double? Confidence)>> CheckSeveritiesAsync(
        IReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default);

    /// <summary>Asks whether each finding belongs to the class a host severity override describes, and grades a confident match at the row's severity.</summary>
    Task<(List<ReviewFinding> Findings, List<(ReviewFinding Finding, SeverityOverrideRow Row, double? Confidence)> Matched)> MatchOverridesAsync(
        IReviewDecisionModel model, SeverityOverrides severityOverrides, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default);

    /// <summary>Asks the lens-applicability question for each seated judgement lens and returns the lenses the model confidently ruled out, each with the reason <c>--not-seated</c> records.</summary>
    Task<IReadOnlyDictionary<string, string>> NotApplicableLensesAsync(
        IReviewDecisionModel model, IReadOnlyList<string> seated, string changeSummary, CancellationToken cancellationToken = default);

    /// <summary>The lines around a finding's cited line, wide enough to hold a multi-line excerpt; null when unreadable.</summary>
    /// <param name="repositoryRoot">The repository root the cited file is resolved against.</param>
    /// <param name="finding">The finding whose cited lines are read.</param>
    /// <param name="onFailure">Told the exception type when a read failed, so the cause is not lost with the null.</param>
    string? Surroundings(string repositoryRoot, ReviewFinding finding, Action<string>? onFailure = null);

    /// <summary>The lines of other files the finding's evidence cites, read from the tree as it is now.</summary>
    /// <param name="repositoryRoot">The repository root the cited files are resolved against.</param>
    /// <param name="finding">The finding whose evidence is read.</param>
    string EvidenceLines(string repositoryRoot, ReviewFinding finding);
}
