using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Application.Update;

/// <summary>Reads the projects and global targets the update's arbiter hook refresh covers, from the user's registry and configuration.</summary>
/// <param name="state">Lists the registered projects.</param>
/// <param name="userConfig">Reads the per-user host config's Arbiter global targets.</param>
/// <param name="configs">Reads each project's configuration, which says whether it enables the Arbiter.</param>
internal sealed class CliArbiterRefreshTargets(
    ISquadStateStore state,
    IUserHostConfigStore userConfig,
    IKyberWeaveConfigRepository configs) : IArbiterRefreshTargets
{
    /// <inheritdoc />
    public IReadOnlyList<string> CollectEnabledProjectRoots()
    {
        List<string> roots = [];
        foreach (SquadProjectRegistryEntry entry in state.ListProjects())
        {
            if (entry.Stale)
                continue;

            KyberWeaveConfigLoadResult loaded = configs.TryLoad(entry.Path);
            if (loaded.Success && loaded.Config?.Arbiter.Enabled == true)
                roots.Add(entry.Path);
        }

        return roots;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadGlobalTargets(string home) => userConfig.ReadGlobalTargets(home);
}
