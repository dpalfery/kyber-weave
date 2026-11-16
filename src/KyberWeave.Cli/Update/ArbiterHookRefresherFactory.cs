using KyberWeave.Contracts.Services;

namespace KyberWeave.Cli.Update;

/// <summary>Creates the in-process arbiter hook refresher over one environment reader.</summary>
/// <param name="readEnvironment">Reads one environment variable.</param>
internal sealed class ArbiterHookRefresherFactory(Func<string, string?> readEnvironment) : IArbiterHookRefresherFactory
{
    /// <inheritdoc />
    public IArbiterHookRefresher Create(Action<string> log) => new ArbiterHookRefresher(log, readEnvironment);
}
