namespace KyberWeave.Core.Configuration;

/// <summary>Outcome of attempting to load a combined Kyber-Weave config file.</summary>
/// <param name="success">Whether a configuration was produced.</param>
/// <param name="config">The configuration, when the load succeeded.</param>
/// <param name="error">Why the load failed, when it did.</param>
/// <param name="configPath">The file read, when one was.</param>
public sealed class KyberWeaveConfigLoadResult(
    bool success,
    KyberWeaveConfig? config = null,
    string? error = null,
    string? configPath = null)
{
    public bool Success { get; } = success;

    public KyberWeaveConfig? Config { get; } = config;

    public string? Error { get; } = error;

    public string? ConfigPath { get; } = configPath;
}
