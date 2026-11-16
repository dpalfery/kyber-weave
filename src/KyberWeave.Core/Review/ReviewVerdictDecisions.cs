using KyberWeave.Application.Review;
using KyberWeave.Contracts.Services;
using KyberWeave.Data.Review;

namespace KyberWeave.Core.Review;

/// <summary>
/// The verdict command's four decision-model steps for the callers that have no container, asked over the real file
/// system.
/// </summary>
/// <remarks>
/// The logic is <see cref="ReviewDecisionSteps"/> in the application layer behind <see cref="IReviewDecisionSteps"/>, and
/// the pure rules are <see cref="ReviewVerdictRules"/>. This stays for the review commands that have not moved onto the
/// container, and for the tests that call it.
/// </remarks>
public static class ReviewVerdictDecisions
{
    private static readonly IReviewDecisionSteps Steps = new ReviewDecisionSteps(new FileCitedSourceFiles(), new FileContainedFileReader());

    /// <summary>The diagnostic code every decision-model line of the verdict report carries.</summary>
    public const string Code = ReviewVerdictRules.Code;

    /// <summary>Does this finding name a nitpick or a substantive defect.</summary>
    public const string NitpickRule = ReviewVerdictRules.NitpickRule;

    /// <summary>Do two findings name the same defect.</summary>
    public const string DuplicateRule = ReviewVerdictRules.DuplicateRule;

    /// <summary>Does the cited code support the finding's claim.</summary>
    public const string ClaimRule = ReviewVerdictRules.ClaimRule;

    /// <summary>Does the stated severity match the stated impact.</summary>
    public const string SeverityRule = ReviewVerdictRules.SeverityRule;

    /// <summary>Does the finding belong to the class a host severity override describes.</summary>
    public const string OverrideRule = ReviewVerdictRules.OverrideRule;

    /// <inheritdoc cref="ReviewVerdictRules.MaxQuestionsPerStep" />
    public const int MaxQuestionsPerStep = ReviewVerdictRules.MaxQuestionsPerStep;

    /// <inheritdoc cref="ReviewVerdictRules.MaxFindingsPerFile" />
    public const int MaxFindingsPerFile = ReviewVerdictRules.MaxFindingsPerFile;

    /// <inheritdoc cref="ReviewVerdictRules.MaxFindingsPerStep" />
    public const int MaxFindingsPerStep = ReviewVerdictRules.MaxFindingsPerStep;

    /// <inheritdoc cref="ReviewVerdictRules.GroupByFile" />
    internal static Dictionary<string, List<int>> GroupByFile(IReadOnlyList<ReviewFinding> findings, out int leftOut) =>
        ReviewVerdictRules.GroupByFile(findings, out leftOut);

    /// <inheritdoc cref="ReviewVerdictRules.NearbyPairs" />
    internal static (List<(int A, int B)> Pairs, int CandidateCount, int LeftOut) NearbyPairs(IReadOnlyList<ReviewFinding> findings) =>
        ReviewVerdictRules.NearbyPairs(findings);

    /// <inheritdoc cref="ReviewVerdictRules.ExcerptIsIn" />
    internal static bool ExcerptIsIn(string excerpt, string window) => ReviewVerdictRules.ExcerptIsIn(excerpt, window);

    /// <inheritdoc cref="ReviewVerdictRules.SeverityName" />
    public static string SeverityName(ReviewSeverity severity) => ReviewVerdictRules.SeverityName(severity);

    /// <inheritdoc cref="IReviewDecisionSteps.MergeDuplicatesAsync" />
    public static Task<(List<ReviewFinding> Kept, List<MergedFindings> Merged)> MergeDuplicatesAsync(
        ReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default) =>
        Steps.MergeDuplicatesAsync(model, findings, cancellationToken);

    /// <inheritdoc cref="IReviewDecisionSteps.DropNitpicksAsync" />
    public static Task<(List<ReviewFinding> Kept, List<(ReviewFinding Finding, double? Confidence)> Nitpicks)> DropNitpicksAsync(
        ReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default) =>
        Steps.DropNitpicksAsync(model, findings, cancellationToken);

    /// <inheritdoc cref="IReviewDecisionSteps.CheckClaimsAsync" />
    public static Task<IReadOnlyList<ClaimCheck>> CheckClaimsAsync(
        ReviewDecisionModel model, string repositoryRoot, IReadOnlyList<ReviewFinding> candidates, CancellationToken cancellationToken = default) =>
        Steps.CheckClaimsAsync(model, repositoryRoot, candidates, cancellationToken);

    /// <inheritdoc cref="IReviewDecisionSteps.CheckSeveritiesAsync" />
    public static Task<IReadOnlyList<(ReviewFinding Finding, string Direction, double? Confidence)>> CheckSeveritiesAsync(
        ReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default) =>
        Steps.CheckSeveritiesAsync(model, findings, cancellationToken);

    /// <inheritdoc cref="IReviewDecisionSteps.MatchOverridesAsync" />
    public static Task<(List<ReviewFinding> Findings, List<(ReviewFinding Finding, SeverityOverrideRow Row, double? Confidence)> Matched)> MatchOverridesAsync(
        ReviewDecisionModel model, SeverityOverrides overrides, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default) =>
        Steps.MatchOverridesAsync(model, overrides, findings, cancellationToken);

    /// <inheritdoc cref="IReviewDecisionSteps.Surroundings" />
    internal static string? Surroundings(string repositoryRoot, ReviewFinding finding, Action<string>? onFailure = null) =>
        Steps.Surroundings(repositoryRoot, finding, onFailure);

    /// <inheritdoc cref="IReviewDecisionSteps.EvidenceLines" />
    internal static string EvidenceLines(string repositoryRoot, ReviewFinding finding) => Steps.EvidenceLines(repositoryRoot, finding);
}
