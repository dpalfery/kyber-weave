using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Contracts.Services;

/// <summary>Creates the refresher that re-renders arbiter hooks after an update, reporting through the caller's log.</summary>
public interface IArbiterHookRefresherFactory
{
    /// <summary>Creates a refresher.</summary>
    /// <param name="log">Receives one line per step or warning.</param>
    /// <returns>The refresher.</returns>
    IArbiterHookRefresher Create(Action<string> log);
}
