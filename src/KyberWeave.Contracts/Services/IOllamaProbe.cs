namespace KyberWeave.Contracts.Services;

/// <summary>Detects a local Ollama daemon new enough to suggest the <c>nimble</c> model.</summary>
public interface IOllamaProbe
{
    /// <summary>
    /// Probes an Ollama <c>/api/version</c> URL for a 0.35 or later daemon, which answers <c>{"version":"x.y.z"}</c>.
    /// Any transport, parse or version failure means not found, never an error.
    /// </summary>
    /// <param name="versionUri">The <c>/api/version</c> URL; only http and https are probed.</param>
    /// <param name="version">Receives the daemon version when one answered.</param>
    /// <returns>True when a daemon of 0.35 or later answered.</returns>
    bool TryDetect(Uri versionUri, out string? version);
}
