using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Preflights install, update and uninstall operations against the deployment tree on disk.</summary>
public interface ISquadDeploymentPlanner
{
    /// <summary>Preflights a new installation without changing the deployment tree.</summary>
    SquadDeploymentPlan CreateInstall(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadLock squadLock,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        IReadOnlyList<SquadDegradation> degradations,
        bool adopt,
        TimeProvider timeProvider,
        ISquadGlobalRootResolver? globalRoots = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
        IReadOnlyList<SquadRenderedBlock>? blocks = null);

    /// <summary>Preflights an update without changing the deployment tree.</summary>
    SquadDeploymentPlan CreateUpdate(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadLock squadLock,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        SquadReceipt previousReceipt,
        IReadOnlyList<SquadDegradation> degradations,
        bool replaceManaged,
        TimeProvider timeProvider,
        ISquadGlobalRootResolver? globalRoots = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
        IReadOnlyList<SquadRenderedBlock>? blocks = null);

    /// <summary>Preflights an uninstall without changing the deployment tree.</summary>
    SquadDeploymentPlan CreateUninstall(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadReceipt receipt,
        ISquadGlobalRootResolver? globalRoots = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
        SquadTarget? onlyTarget = null);

    /// <summary>Finds the paths a deployment would write that exist and are not Squad's own.</summary>
    IReadOnlyList<SquadUnmanagedPathCollision> CollectUnmanagedCollisions(
        string targetRoot,
        SquadDeploymentScope scope,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        ISquadGlobalRootResolver? globalRoots,
        SquadReceipt? receipt = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null);
}
