namespace KyberWeave.Contracts.Repositories;

/// <summary>What an update's arbiter hook refresh reads: the projects that enable the arbiter and the global targets.</summary>
public interface IArbiterRefreshTargets
{
    /// <summary>The registered, non-stale projects whose configuration enables the arbiter.</summary>
    /// <returns>The project roots.</returns>
    /// <exception cref="IOException">The project registry cannot be read.</exception>
    IReadOnlyList<string> CollectEnabledProjectRoots();

    /// <summary>The global targets configured for the user.</summary>
    /// <param name="home">The user's home directory.</param>
    /// <returns>The configured targets; empty when none.</returns>
    IReadOnlyList<string> ReadGlobalTargets(string home);
}
