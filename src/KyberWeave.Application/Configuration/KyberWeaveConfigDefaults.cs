using KyberWeave.Core.Arbiter;

namespace KyberWeave.Core.Configuration;

/// <summary>The host configuration an unconfigured host runs with.</summary>
public static class KyberWeaveConfigDefaults
{
    /// <summary>Every section at its product default, the Arbiter with its shipped rules.</summary>
    public static KyberWeaveConfig Product { get; } = new() { Arbiter = ArbiterConfigDefaults.Product };
}
