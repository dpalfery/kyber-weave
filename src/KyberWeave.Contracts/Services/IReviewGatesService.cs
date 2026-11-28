using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Contracts.Services;

/// <summary>Runs the deterministic gates a review executes, either the repository's declared suite or one agent's task gates.</summary>
public interface IReviewGatesService
{
    /// <summary>Runs the gates the request scopes and reports what they proved.</summary>
    /// <param name="request">The options the command parsed.</param>
    /// <param name="indexExecutor">Runs the CodeGraph index child, bounded by the entry point.</param>
    /// <param name="output">Receives the lines a run prints before and after the report, in order.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    ReviewCommandResult Run(
        ReviewGatesRequest request,
        IProcessExecutor indexExecutor,
        TextWriter output,
        CancellationToken cancellationToken = default);

    /// <summary>Runs the gates the request scopes and reports what they proved, without blocking a thread.</summary>
    /// <param name="request">The options the command parsed.</param>
    /// <param name="indexExecutor">Runs the CodeGraph index child, bounded by the entry point.</param>
    /// <param name="output">Receives the lines a run prints before and after the report, in order.</param>
    /// <param name="cancellationToken">Cancels the run and stops active gates.</param>
    Task<ReviewCommandResult> RunAsync(
        ReviewGatesRequest request,
        IProcessRunner indexExecutor,
        TextWriter output,
        CancellationToken cancellationToken = default);
}
