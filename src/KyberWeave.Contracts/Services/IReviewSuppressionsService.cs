using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// Refuses a warning suppression the change adds unless it states a reason, and leaves the suppressions that were
/// already in the file alone.
/// </summary>
/// <remarks>
/// This repository asks for "a reason you can state" rather than banning suppressions. So the gate judges added lines
/// only: a change that leaves a committed suppression untouched passes, and a change that adds one without saying why
/// fails with the file and line named. An unreadable change is a failure rather than a pass, because a gate that cannot
/// see the change has no evidence either way.
/// </remarks>
public interface IReviewSuppressionsService
{
    /// <summary>Scans the lines the change adds for a suppression that states no reason.</summary>
    /// <param name="request">The options the command parsed.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    ReviewCommandResult Run(ReviewSuppressionsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Scans the lines the change adds for a suppression that states no reason, without blocking a thread.</summary>
    /// <param name="request">The options the command parsed.</param>
    /// <param name="cancellationToken">Cancels the git reads.</param>
    Task<ReviewCommandResult> RunAsync(
        ReviewSuppressionsRequest request,
        CancellationToken cancellationToken = default);
}
