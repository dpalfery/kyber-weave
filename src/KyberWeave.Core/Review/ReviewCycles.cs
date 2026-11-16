using KyberWeave.Data.Review;

namespace KyberWeave.Core.Review;

/// <summary>Forwards the two review-cycle calls the Docs delivery still makes statically to the layered store and planner.</summary>
/// <remarks>Delete once the delivery runner takes <c>IReviewCycleStore</c> from the container.</remarks>
public static class ReviewCycles
{
    /// <inheritdoc cref="KyberWeave.Contracts.Repositories.IReviewCycleStore.ReadFinished" />
    public static IReadOnlyList<ReviewCycleRecord> ReadFinished(string reviewFolder) =>
        new FileReviewCycleStore().ReadFinished(reviewFolder);

    /// <inheritdoc cref="ReviewCyclePlanning.Belonging" />
    public static PriorCycles Belonging(
        IReadOnlyList<ReviewCycleRecord> finished,
        ReviewCycleIdentity? current,
        Func<string, bool> isAncestorOfHead) =>
        ReviewCyclePlanning.Belonging(finished, current, isAncestorOfHead);
}
