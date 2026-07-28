namespace KyberWeave.Core.Configuration;

/// <summary>Outcome of attempting to load a combined Kyber-Weave config file.</summary>
public sealed class KyberWeaveConfigLoadResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public string? ConfigPath { get; init; }

    public KyberWeaveConfig? Config { get; init; }

    public static KyberWeaveConfigLoadResult Ok(KyberWeaveConfig config, string? configPath) =>
        new() { Success = true, Config = config, ConfigPath = configPath };

    public static KyberWeaveConfigLoadResult Fail(string error, string? configPath) =>
        new() { Success = false, Error = error, ConfigPath = configPath };
}
