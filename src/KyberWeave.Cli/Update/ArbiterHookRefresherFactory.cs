using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;

namespace KyberWeave.Cli.Update;

/// <summary>Creates the in-process arbiter hook refresher over one environment reader.</summary>
/// <param name="readEnvironment">Reads one environment variable.</param>
/// <param name="userConfig">Reads the per-user host config's Arbiter global targets.</param>
internal sealed class ArbiterHookRefresherFactory(
    Func<string, string?> readEnvironment,
    IUserHostConfigStore userConfig) : IArbiterHookRefresherFactory
{
    /// <inheritdoc />
    public IArbiterHookRefresher Create(Action<string> log) => new ArbiterHookRefresher(userConfig, log, readEnvironment);
}
