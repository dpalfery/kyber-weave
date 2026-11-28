using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Delivery;

namespace KyberWeave.Contracts.Services;

/// <summary>Pushes a finished run's branch and creates or updates its pull request, described from what the run recorded.</summary>
public interface IRunDeliveryRunner
{
    /// <summary>Delivers the run in <paramref name="repoRoot"/>, or refuses and changes nothing.</summary>
    /// <param name="repoRoot">The run's worktree.</param>
    /// <param name="config">The repository's effective configuration.</param>
    /// <param name="request">What the conductor asks for.</param>
    /// <param name="cancellationToken">Cancels the git and GitHub CLI runs.</param>
    Task<DeliveryOutcome> RunAsync(string repoRoot, KyberWeaveConfig config, DeliveryRequest request, CancellationToken cancellationToken = default);
}
