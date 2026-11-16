using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Applies a deployment plan to disk as one recoverable transaction.</summary>
public interface ISquadTransaction
{
    /// <summary>Gets the warnings the last execution raised.</summary>
    IReadOnlyList<string> Warnings { get; }

    /// <summary>Applies a plan.</summary>
    void Execute(SquadDeploymentPlan plan);

    /// <summary>Applies a plan, running a callback while the transaction holds its lease.</summary>
    void Execute(SquadDeploymentPlan plan, Action<SquadDeploymentPlan>? underLease);

    /// <summary>Completes or rolls back a transaction a crashed run left behind.</summary>
    void Recover(string targetRoot, SquadDeploymentScope scope);
}

/// <summary>Creates deployment transactions over the state store.</summary>
public interface ISquadTransactionFactory
{
    /// <summary>Creates a transaction.</summary>
    /// <param name="observer">Told of each step, or null.</param>
    ISquadTransaction Create(ISquadTransactionObserver? observer);
}
