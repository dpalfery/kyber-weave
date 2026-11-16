using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// Answers which review cycle a run is in, from the cycle folders the council already wrote: the cycle number, whether
/// it is a full review or a verifier cycle, the prior findings to re-check, and the gates to run again.
/// </summary>
public interface IReviewCycleService
{
    /// <summary>Reports the cycle the request names.</summary>
    /// <param name="request">The options the command parsed.</param>
    /// <param name="output">Receives the answer, as lines or as one JSON document.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    ReviewCommandResult Run(
        ReviewCycleRequest request,
        TextWriter output,
        CancellationToken cancellationToken = default);
}
