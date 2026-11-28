using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// Prepares the council's seats: indexes the tree, opens the cycle folder, writes the diff, the evidence and every
/// packet, and prints each seat's dispatch block, the lenses a catalogue file pattern rules out, and the gate command.
/// </summary>
public interface IReviewSeatsService
{
    /// <summary>Prepares the seats of the cycle the request names, without blocking a thread.</summary>
    /// <param name="request">The options the command parsed.</param>
    /// <param name="decisionModel">Builds, without blocking a thread, the model that rules a judgement lens out, from the root and the loaded configuration.</param>
    /// <param name="indexExecutor">Runs the CodeGraph index child, bounded by the entry point.</param>
    /// <param name="output">Receives the lines and dispatch blocks a reviewer reads.</param>
    /// <param name="cancellationToken">Cancels the index child and the decision-model questions.</param>
    Task<ReviewCommandResult> PrepareAsync(
        ReviewSeatsRequest request,
        Func<string, KyberWeaveConfig, CancellationToken, Task<IReviewDecisionModel>> decisionModel,
        IProcessRunner indexExecutor,
        TextWriter output,
        CancellationToken cancellationToken = default);
}
