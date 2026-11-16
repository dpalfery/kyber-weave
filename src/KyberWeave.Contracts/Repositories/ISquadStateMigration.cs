using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Moves legacy Squad state into the home-level layout.</summary>
public interface ISquadStateMigration
{
    /// <summary>Migrates the legacy global receipt.</summary>
    /// <param name="invokingCwd">The directory the command was started from, or null.</param>
    /// <param name="dryRun">Whether to report what would move without writing.</param>
    SquadMigrationReport MigrateGlobal(string? invokingCwd, bool dryRun = false);

    /// <summary>Imports one project's in-repo state into the home registry.</summary>
    /// <param name="root">The project root.</param>
    /// <param name="dryRun">Whether to report what would be imported without writing.</param>
    SquadMigrationReport ImportProject(string root, bool dryRun = false);
}
