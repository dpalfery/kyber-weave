using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Arbiter;

/// <summary>Installs the Arbiter hooks through the Squad lifecycle, over the release the host prefers for an instance install.</summary>
/// <param name="lifecycle">Creates the lifecycle service for one run; the service owns its release source for that run.</param>
public sealed class ArbiterInstallLifecycle(Func<ISquadLifecycleService> lifecycle) : IArbiterInstallLifecycle
{
    /// <inheritdoc />
    public Task<SquadLifecycleResult> InstallAsync(
        string root,
        SquadDeploymentScope scope,
        SquadTarget target,
        SquadReceipt? existingReceipt,
        bool dryRun,
        SquadArbiterWiring wiring,
        CancellationToken cancellationToken)
    {
        ISquadLifecycleService service = lifecycle();
        if (existingReceipt is null)
        {
            SquadInstallRequest request = new(
                root,
                scope,
                [target],
                DryRun: dryRun,
                Arbiter: wiring);
            return service.InstallAsync(request, cancellationToken);
        }

        HashSet<string> existingTargetTokens = existingReceipt.Files
            .Select(f => f.Target)
            .Concat(existingReceipt.Blocks.Select(b => b.Target))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        List<SquadTarget> combinedTargets = [target];
        foreach (string token in existingTargetTokens)
        {
            if (SquadTargetCatalog.TryResolve(token, out SquadTarget existingTarget) && !combinedTargets.Contains(existingTarget))
            {
                combinedTargets.Add(existingTarget);
            }
        }

        SquadUpdateRequest updateRequest = new(
            root,
            scope,
            combinedTargets,
            DryRun: dryRun,
            Arbiter: wiring);
        return service.UpdateAsync(updateRequest, cancellationToken);
    }
}
