namespace KyberWeave.Core.Arbiter;

/// <summary>The Arbiter configuration an unconfigured host runs with.</summary>
public static class ArbiterConfigDefaults
{
    /// <summary>
    /// Product defaults for an unconfigured host: disabled, no provider, every shipped rule enabled.
    /// </summary>
    /// <remarks>
    /// Loaded from the embedded role arbiter-rules files rather than hardcoded so the
    /// shipped catalogue has exactly one source of truth: the files the validator and
    /// the documentation both read.
    /// </remarks>
    public static ArbiterConfig Product { get; } = ArbiterConfigLoader.LoadEmbeddedDefaults();
}
