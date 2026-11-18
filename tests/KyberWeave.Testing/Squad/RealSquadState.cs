using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// Builds the real Squad state store and migration over the real adapters, for the tests whose subject is what
/// the state looks like on a disk. The classes themselves take their seams, so every test that does not need a
/// real disk builds them over <see cref="InMemorySquadState"/> instead.
/// </summary>
public static class RealSquadState
{
    /// <summary>The state store over the real file system, path resolver and process environment.</summary>
    /// <param name="userPaths">The home and application-data directories the state is anchored to.</param>
    /// <returns>The store.</returns>
    public static SquadStateStore Store(ISquadUserPaths userPaths) =>
        new(
            userPaths,
            new PhysicalFileSystem(),
            SquadFileSystemPathSemantics.Instance,
            new PhysicalPathResolver(new PhysicalFileSystem()),
            new ProcessEnvironment(),
            new MutexLease());

    /// <summary>The migration over a store, with the real file system, lease and clock.</summary>
    /// <param name="store">The store the migration resolves its locations from.</param>
    /// <param name="userPaths">The legacy locations; the store's own when null.</param>
    /// <returns>The migration.</returns>
    public static SquadStateMigration Migration(ISquadStateStore store, ISquadUserPaths? userPaths = null) =>
        new(
            store,
            userPaths ?? store.UserPaths,
            new PhysicalFileSystem(),
            SquadFileSystemPathSemantics.Instance,
            new MutexLease(),
            TimeProvider.System);
}
