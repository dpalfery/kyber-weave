using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>Computes the review verdict from the council's seat output, or a findings document, and the gate report.</summary>
public interface IReviewVerdictService
{
    /// <summary>
    /// Computes the verdict and writes the findings document the verdict was computed from, without blocking a thread.
    /// A refused input is a report with its diagnostics and an exit code, never an exception.
    /// </summary>
    /// <param name="request">What the run reads and writes.</param>
    /// <param name="cancellationToken">Cancels the decision model's calls.</param>
    /// <returns>The report, the exit code, and the line a candidate run prints after the report.</returns>
    Task<ReviewVerdictResult> DecideAsync(ReviewVerdictRequest request, CancellationToken cancellationToken = default);
}
