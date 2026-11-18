using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The deployment planner over the real adapters, for the tests whose subject is what a real disk holds. A test
/// that does not need a disk builds the planner over the shared in-memory fakes instead.
/// </summary>
public static class RealSquadDeployment
{
    /// <summary>The planner over the physical file system, path resolver and process environment.</summary>
    public static SquadDeploymentPlanner Planner { get; } = new(
        new PhysicalFileSystem(),
        SquadFileSystemPathSemantics.Instance,
        new PhysicalPathResolver(new PhysicalFileSystem()));

    /// <summary>A transaction over the physical file system, the operating-system mutex and the real path rules.</summary>
    /// <param name="store">The state store the transaction journals in.</param>
    /// <param name="observer">Told of each durable step; none when null.</param>
    /// <returns>The transaction.</returns>
    public static SquadTransaction Transaction(ISquadStateStore store, ISquadTransactionObserver? observer = null) =>
        new(
            store,
            new PhysicalFileSystem(),
            SquadFileSystemPathSemantics.Instance,
            new MutexLease(),
            new SystemIdGenerator(),
            observer);

    /// <summary>A transaction factory over the same real adapters.</summary>
    /// <param name="store">The state store the transactions journal in.</param>
    /// <returns>The factory.</returns>
    public static SquadTransactionFactory TransactionFactory(ISquadStateStore store) =>
        new(
            store,
            new PhysicalFileSystem(),
            SquadFileSystemPathSemantics.Instance,
            new MutexLease(),
            new SystemIdGenerator());
}
