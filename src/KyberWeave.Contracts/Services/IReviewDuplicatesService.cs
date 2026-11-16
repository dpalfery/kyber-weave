using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>Finds symbols whose bodies are duplicated across the tree, from the CodeGraph index.</summary>
/// <remarks>
/// Declared as a gate under <c>review.gates</c> like any other. It exits 0 whether or not it finds clusters, for the same
/// reason the analyzer gate does: what a gate proves is that the evidence was produced, and whether a cluster is this
/// change's problem is the reviewing council's question, not this command's. A failed <c>--out</c> write is different —
/// the evidence was not produced — and fails the process.
/// </remarks>
public interface IReviewDuplicatesService
{
    /// <summary>Reports the duplicated bodies of the tree the request names.</summary>
    /// <param name="request">The options the command parsed.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    ReviewCommandResult Run(ReviewDuplicatesRequest request, CancellationToken cancellationToken = default);
}
