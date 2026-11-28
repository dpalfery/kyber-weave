namespace KyberWeave.Contracts.Services;

/// <summary>Detects a local Ollama daemon new enough to suggest the <c>nimble</c> model.</summary>
public interface IOllamaProbe
{
    /// <summary>
    /// Probes an Ollama <c>/api/version</c> URL for a 0.35 or later daemon without blocking a thread; the daemon answers
    /// <c>{"version":"x.y.z"}</c>. Any transport, parse or version failure means not found, never an error.
    /// </summary>
    /// <param name="versionUri">The <c>/api/version</c> URL; only http and https are probed.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The outcome: detected with the version when a 0.35 or later daemon answered, else not detected.</returns>
    Task<OllamaProbeResult> TryDetectAsync(Uri versionUri, CancellationToken cancellationToken);
}

/// <summary>The outcome of probing for a local Ollama daemon.</summary>
/// <param name="Detected">True when a 0.35 or later daemon answered.</param>
/// <param name="Version">The version the daemon reported, or null when none did.</param>
public sealed record OllamaProbeResult(bool Detected, string? Version);
