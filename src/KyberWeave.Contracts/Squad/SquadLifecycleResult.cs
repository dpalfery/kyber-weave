using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The result of executing a Kyber-Squad lifecycle operation.</summary>
/// <remarks>
///     <see cref="Warnings" /> carries what a committed transaction could not finish, such as a registry
///     write that failed after the deployment itself committed. The deployment is never rolled back for
///     them, so a caller that prints only success has hidden the problem.
/// </remarks>
public sealed record SquadLifecycleResult(
    bool Success,
    SquadDeploymentPlan? Plan = null,
    SquadReceipt? Receipt = null,
    SquadLock? Lock = null,
    IReadOnlyList<SquadDegradation>? Degradations = null,
    IReadOnlyList<string>? Errors = null,
    bool DryRun = false,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<SquadModelDiffItem>? ModelChanges = null);
