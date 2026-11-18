using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;

namespace KyberWeave.Cli.Update;

/// <summary>Creates the in-process arbiter hook refresher over the container's ports.</summary>
/// <param name="environment">Reads the home directory and the variables that locate a harness's global hook file.</param>
/// <param name="userConfig">Reads the per-user host config's Arbiter global targets.</param>
/// <param name="configs">Reads the project and per-user host configuration.</param>
/// <param name="lifecycle">Runs the Squad update that re-renders a project's hooks.</param>
/// <param name="state">Reads and rewrites the global Squad receipt.</param>
/// <param name="files">Reads the hook files before rewriting them.</param>
/// <param name="wiring">Scans a project for retired MCP server keys.</param>
internal sealed class ArbiterHookRefresherFactory(
    IProcessEnvironment environment,
    IUserHostConfigStore userConfig,
    IKyberWeaveConfigRepository configs,
    ISquadLifecycleService lifecycle,
    ISquadStateStore state,
    IWritableFileSystem files,
    IMcpWiringService wiring) : IArbiterHookRefresherFactory
{
    /// <inheritdoc />
    public IArbiterHookRefresher Create(Action<string> log) =>
        new ArbiterHookRefresher(environment, userConfig, configs, lifecycle, state, files, wiring, log);
}
