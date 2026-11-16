using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Chooses the harness targets a Squad operation applies to, looking at the files already in the root.</summary>
public interface ISquadTargetResolver
{
    /// <summary>Resolves targets according to the operation-specific precedence contract.</summary>
    /// <param name="request">The explicit, configured and recorded targets and the root to inspect.</param>
    /// <returns>The decision for the host to render or act upon.</returns>
    SquadTargetResolutionDecision Resolve(SquadTargetResolutionRequest request);
}
