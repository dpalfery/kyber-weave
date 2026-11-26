using KyberWeave.Domain.Update;

namespace KyberWeave.Contracts.Services;

/// <summary>Resolves a GitHub Release and replaces the running CLI and its sibling binaries in place.</summary>
public interface ISelfUpdateService
{
    /// <summary>Runs one update, or the finishing half of one.</summary>
    /// <param name="options">What the user asked for.</param>
    /// <param name="log">Receives one line per step.</param>
    /// <returns>The exit code and the line to print.</returns>
    /// <exception cref="SelfUpdateException">The host could not be resolved; every update failure is returned as an outcome.</exception>
    SelfUpdateOutcome Run(SelfUpdateOptions options, Action<string> log);
}
