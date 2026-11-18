using KyberWeave.Core.Arbiter;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>The capability profiles of specialists that write project files, which a run must be started for.</summary>
public static class RunWriterProfiles
{
    /// <summary>The fix a refused dispatch names.</summary>
    public const string Remedy = ArbiterRunStartRemedy.Text;

    private static readonly HashSet<string> Profiles = new(StringComparer.Ordinal)
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
    /// <param name="capabilityProfile">The agent's capability profile.</param>
    /// <returns>True when the profile writes project files.</returns>
    public static bool IsWriter(string? capabilityProfile) =>
        capabilityProfile is not null && Profiles.Contains(capabilityProfile);
}
