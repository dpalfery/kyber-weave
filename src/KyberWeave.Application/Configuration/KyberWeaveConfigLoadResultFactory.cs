namespace KyberWeave.Core.Configuration;

/// <summary>
/// Builds the two outcomes of <see cref="KyberWeaveConfigLoadResult"/>, so the result itself stays data.
/// </summary>
public static class KyberWeaveConfigLoadResultFactory
{
    /// <summary>A load that produced <paramref name="config"/>.</summary>
    /// <param name="config">The configuration read.</param>
    /// <param name="configPath">The file it came from, or null when the product defaults were used.</param>
    public static KyberWeaveConfigLoadResult Ok(KyberWeaveConfig config, string? configPath) =>
        new(true, config, configPath: configPath);

    /// <summary>A load that failed with <paramref name="error"/>.</summary>
    /// <param name="error">Why it failed.</param>
    /// <param name="configPath">The file it tried to read, when one was named.</param>
    public static KyberWeaveConfigLoadResult Fail(string error, string? configPath) =>
        new(false, error: error, configPath: configPath);
}
