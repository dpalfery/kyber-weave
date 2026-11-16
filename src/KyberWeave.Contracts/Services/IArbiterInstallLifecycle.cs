using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Services;

/// <summary>Runs the Squad deployment that <c>arbiter install</c> performs for one harness target.</summary>
/// <remarks>
/// The release comes from a local checkout when the CLI runs inside one, otherwise from the published release, the same
/// choice the Squad instance service makes.
/// </remarks>
public interface IArbiterInstallLifecycle
{
    /// <summary>
    /// Installs the Arbiter hooks for <paramref name="target"/>. When the root already holds a receipt, the deployment is
    /// updated for the target together with every target the receipt records, so an install never drops another harness.
    /// </summary>
    /// <param name="root">The deployment root.</param>
    /// <param name="scope">Project or global.</param>
    /// <param name="target">The harness to install.</param>
    /// <param name="existingReceipt">The receipt already recorded for the root, or null.</param>
    /// <param name="dryRun">Whether to plan without writing.</param>
    /// <param name="wiring">The Arbiter settings carried into the render.</param>
    /// <param name="cancellationToken">Cancels the deployment.</param>
    /// <returns>The lifecycle result.</returns>
    Task<SquadLifecycleResult> InstallAsync(
        string root,
        SquadDeploymentScope scope,
        SquadTarget target,
        SquadReceipt? existingReceipt,
        bool dryRun,
        SquadArbiterWiring wiring,
        CancellationToken cancellationToken);
}
