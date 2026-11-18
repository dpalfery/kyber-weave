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

    private static readonly HashSet<string> WriterProfiles = new(StringComparer.Ordinal)
    {
        "worker",
        "documentation",
        ArbiterSquadCatalog.ArchitectCapabilityProfile,
        "product-planning",
    };

    /// <summary>Whether a specialist with <paramref name="capabilityProfile" /> writes project files.</summary>
    /// <remarks>
    /// The planners (<c>architect</c>, <c>product-planning</c>) are writers here: their plan, specification and
    /// task files belong on the run branch, so planning before run start would leave them in the canonical checkout.
    /// <c>publishing-worker</c> (the devops agent) is left out on purpose: it is the route that runs
    /// <c>run start</c> for a harness with no shell, so gating it on a started run would refuse its own fix.
    /// </remarks>
    public static bool IsWriterProfile(string? capabilityProfile) =>
        capabilityProfile is not null && WriterProfiles.Contains(capabilityProfile);
}
