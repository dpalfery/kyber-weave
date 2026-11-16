using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Cli.Update;

/// <summary>Reads the projects and global targets the update's arbiter hook refresh covers, from the user's registry and configuration.</summary>
/// <param name="readEnvironment">Reads one environment variable; decides which home the registry is read from.</param>
/// <param name="userConfig">Reads the per-user host config's Arbiter global targets.</param>
internal sealed class CliArbiterRefreshTargets(Func<string, string?> readEnvironment, IUserHostConfigStore userConfig) : IArbiterRefreshTargets
{
    /// <inheritdoc />
    public IReadOnlyList<string> CollectEnabledProjectRoots()
    {
        ISquadUserPaths userPaths = SquadHostDefaults.ResolveUserPaths(null, readEnvironment);
        SquadStateStore stateStore = new(userPaths);
        List<string> roots = [];
        foreach (SquadProjectRegistryEntry entry in stateStore.ListProjects())
        {
            if (entry.Stale)
                continue;

            KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(entry.Path);
            if (loaded.Success && loaded.Config?.Arbiter.Enabled == true)
                roots.Add(entry.Path);
        }

        return roots;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadGlobalTargets(string home) => userConfig.ReadGlobalTargets(home);
}
