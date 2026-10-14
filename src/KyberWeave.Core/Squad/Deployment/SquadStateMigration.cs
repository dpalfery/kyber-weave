namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Moves Squad lock and receipt state from its legacy per-user locations into the home-level
/// layout, and imports individual projects into the registry.
/// </summary>
/// <remarks>
/// Seam only: both entry points throw until the migration task lands. The class exists now so
/// the CLI and the tests can be written against its shape before its behaviour is implemented.
/// </remarks>
public sealed class SquadStateMigration
{
    private readonly SquadStateStore _stateStore;
    private readonly ISquadUserPaths _userPaths;

    /// <summary>Creates a migration over the given state store and user paths.</summary>
    public SquadStateMigration(SquadStateStore stateStore, ISquadUserPaths userPaths)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(userPaths);
        _stateStore = stateStore;
        _userPaths = userPaths;
    }

    /// <summary>Migrates legacy global state into the home-level layout.</summary>
    /// <param name="invokingCwd">
    /// The working directory of the invoking command, or <see langword="null"/> when unknown. It
    /// is used to recognise the legacy root that belongs to the invoking project.
    /// </param>
    /// <exception cref="NotImplementedException">Always, until the migration task lands.</exception>
    public SquadMigrationReport MigrateGlobal(string? invokingCwd)
    {
        throw new NotImplementedException("Squad global state migration is not implemented yet.");
    }

    /// <summary>Imports one project's legacy state into the home-level registry.</summary>
    /// <param name="root">The project root whose legacy state should be imported.</param>
    /// <exception cref="NotImplementedException">Always, until the migration task lands.</exception>
    public SquadMigrationReport ImportProject(string root)
    {
        throw new NotImplementedException("Squad project import is not implemented yet.");
    }
}
