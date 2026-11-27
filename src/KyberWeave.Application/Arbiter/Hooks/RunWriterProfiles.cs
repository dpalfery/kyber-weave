using KyberWeave.Core.Agents.Roster;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>The capability profiles of specialists that write project files, which a run must be started for.</summary>
public static class RunWriterProfiles
{
    /// <summary>The fix a refused dispatch names.</summary>
    public const string Remedy = ArbiterRunStartRemedy.Text;

    /// <summary>Whether a specialist with <paramref name="capabilityProfile" /> writes project files.</summary>
    /// <param name="capabilityProfile">The agent's capability profile.</param>
    /// <returns>True when the profile writes project files.</returns>
    public static bool IsWriter(string? capabilityProfile) => CapabilityProfiles.IsWriter(capabilityProfile);
}
