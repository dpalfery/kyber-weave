using KyberWeave.Core.Agents.Roster;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Core.RunStart;

/// <summary>
/// Which specialists write files, and the fix a refused dispatch names, for the question whether a run has been started
/// before an orchestrator hands out work that writes files (code, docs, or a plan or specification).
/// </summary>
public static class RunStartEvidenceRules
{
    /// <summary>The fix a refused dispatch names.</summary>
    public const string Remedy = ArbiterRunStartRemedy.Text;

    /// <summary>Whether a specialist with <paramref name="capabilityProfile" /> writes project files.</summary>
    /// <param name="capabilityProfile">The agent's capability profile.</param>
    public static bool IsWriterProfile(string? capabilityProfile) => CapabilityProfiles.IsWriter(capabilityProfile);
}
