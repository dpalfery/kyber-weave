using KyberWeave.Application.Review;
using KyberWeave.Contracts.Services;
using KyberWeave.Data.Review;

namespace KyberWeave.Core.Review;

/// <summary>
/// The decision-model step of <c>review seats</c> for the callers that have no container: after the file-pattern step,
/// each lens that is a matter of judgement is asked the Arbiter's lens-applicability question
/// (<c>KW-ARB-LENS-001</c>). Only a confident "does not apply" leaves a lens unseated.
/// </summary>
/// <remarks>
/// The logic is <see cref="ReviewDecisionSteps"/> in the application layer behind <see cref="IReviewDecisionSteps"/>, and
/// the pure rules are <see cref="ReviewLensRules"/>.
/// </remarks>
public static class ReviewLensApplicability
{
    private static readonly IReviewDecisionSteps Steps = new ReviewDecisionSteps(new FileCitedSourceFiles(), new FileContainedFileReader());

    /// <summary>The rule whose question, state and ceiling the step reuses.</summary>
    public const string Rule = ReviewLensRules.Rule;

    /// <summary>The lenses never asked: always seated for a code change, or decided by the gate run.</summary>
    public static IReadOnlyList<string> NeverAsked => ReviewLensRules.NeverAsked;

    /// <inheritdoc cref="IReviewDecisionSteps.NotApplicableLensesAsync" />
    public static Task<IReadOnlyDictionary<string, string>> NotApplicableAsync(
        ReviewDecisionModel model, IReadOnlyList<string> seated, string changeSummary, CancellationToken cancellationToken = default) =>
        Steps.NotApplicableLensesAsync(model, seated, changeSummary, cancellationToken);

    /// <inheritdoc cref="ReviewLensRules.Summarize" />
    public static string Summarize(IReadOnlyList<string> changed, string patch) => ReviewLensRules.Summarize(changed, patch);
}
