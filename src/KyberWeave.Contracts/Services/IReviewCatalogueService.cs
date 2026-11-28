using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>Prints the catalogue <c>review:</c> block to output and writes no file.</summary>
/// <remarks>
/// The block is the same one <c>docs init</c> would write: it is rendered by the same planner for the declared
/// technologies (or <c>--technology</c>), which detects each family's project roots the same way. The plan only reads,
/// so the run leaves every file under the path untouched. It exits 0, or 2 for a technology the catalogue does not know.
/// </remarks>
public interface IReviewCatalogueService
{
    /// <summary>Prints the catalogue block the request names without blocking a thread.</summary>
    /// <param name="request">The options the command parsed.</param>
    /// <param name="output">Receives the block, or the no-family comments.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    Task<ReviewCommandResult> RunAsync(
        ReviewCatalogueRequest request,
        TextWriter output,
        CancellationToken cancellationToken = default);
}
