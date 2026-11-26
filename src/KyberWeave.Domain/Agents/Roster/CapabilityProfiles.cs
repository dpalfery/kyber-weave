namespace KyberWeave.Core.Agents.Roster;

/// <summary>
/// The shipped capability profile names, their target classes, and the set that writes project files.
/// </summary>
/// <remarks>
/// The names mirror <c>products/kyber-squad/profiles/capabilities.yml</c>; a content test keeps the two in step.
/// The target classes are fixed: implementation, planner, read-only, and <c>not-squad</c> for the orchestrator.
/// </remarks>
public static class CapabilityProfiles
{
    /// <summary>The implementation profile a developer tier and <c>docs-dev</c> use.</summary>
    public const string Worker = "worker";

    /// <summary>The delivery profile, which writes but is deliberately not a writer for run start.</summary>
    public const string PublishingWorker = "publishing-worker";

    /// <summary>The documentation profile.</summary>
    public const string Documentation = "documentation";

    /// <summary>The architect's shared profile.</summary>
    public const string Architect = "architect";

    /// <summary>The architect's Copilot target envelope.</summary>
    public const string ArchitectCopilot = "architect-copilot";

    /// <summary>The architect's Devin target envelope.</summary>
    public const string ArchitectDevin = "architect-devin";

    /// <summary>The product owner's shared profile.</summary>
    public const string ProductPlanning = "product-planning";

    /// <summary>The product owner's Devin target envelope.</summary>
    public const string ProductPlanningDevin = "product-planning-devin";

    /// <summary>The read-only investigator profile.</summary>
    public const string Investigator = "investigator";

    /// <summary>The plain read-only profile.</summary>
    public const string ReadOnly = "read-only";

    /// <summary>One review council seat's profile.</summary>
    public const string ReviewSeat = "review-seat";

    /// <summary>The review council's root profile.</summary>
    public const string Reviewer = "reviewer";

    /// <summary>The live-platform researcher's profile.</summary>
    public const string PlatformReader = "platform-reader";

    /// <summary>The orchestrator's profile, which is not Squad routable.</summary>
    public const string Orchestrator = "orchestrator";

    /// <summary>The profile of the root agent that fixes one bounded defect.</summary>
    public const string RootFixer = "root-fixer";

    /// <summary>The implementation target class.</summary>
    public const string Implementation = "implementation";

    /// <summary>The planner target class.</summary>
    public const string Planner = "planner";

    /// <summary>The read-only target class.</summary>
    public const string ReadOnlyClass = "read-only";

    /// <summary>The class of a profile the Squad does not route.</summary>
    public const string NotSquad = "not-squad";

    /// <summary>Every shipped profile name and the target class it maps to.</summary>
    public static IReadOnlyDictionary<string, string> TargetClasses { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Worker] = Implementation,
            [PublishingWorker] = Implementation,
            [Documentation] = Implementation,
            [RootFixer] = Implementation,
            [Architect] = Planner,
            [ArchitectCopilot] = Planner,
            [ArchitectDevin] = Planner,
            [ProductPlanning] = Planner,
            [ProductPlanningDevin] = Planner,
            [Investigator] = ReadOnlyClass,
            [ReadOnly] = ReadOnlyClass,
            [ReviewSeat] = ReadOnlyClass,
            [Reviewer] = ReadOnlyClass,
            [PlatformReader] = ReadOnlyClass,
            [Orchestrator] = NotSquad,
        };

    private static readonly HashSet<string> Writers = new(StringComparer.Ordinal)
    {
        Worker,
        Documentation,
        Architect,
        ArchitectCopilot,
        ArchitectDevin,
        ProductPlanning,
        ProductPlanningDevin,
        RootFixer,
    };

    /// <summary>The target class of <paramref name="capabilityProfile"/>, or <c>not-squad</c> when unknown.</summary>
    /// <param name="capabilityProfile">A capability profile name.</param>
    public static string TargetClassFor(string capabilityProfile)
    {
        ArgumentNullException.ThrowIfNull(capabilityProfile);
        return TargetClasses.TryGetValue(capabilityProfile, out string? targetClass)
            ? targetClass
            : NotSquad;
    }

    /// <summary>Whether a specialist with <paramref name="capabilityProfile"/> writes project files.</summary>
    /// <remarks>
    /// The planners (<c>architect</c>, <c>product-planning</c> and their target envelopes) are writers: their
    /// plan, specification and task files belong on the run branch. <c>publishing-worker</c> (the devops agent)
    /// is left out on purpose: it is the route that runs <c>run start</c> for a harness with no shell, so gating
    /// it on a started run would refuse its own fix.
    /// </remarks>
    /// <param name="capabilityProfile">The agent's capability profile, or null.</param>
    public static bool IsWriter(string? capabilityProfile) =>
        capabilityProfile is not null && Writers.Contains(capabilityProfile);

    /// <summary>Whether <paramref name="capabilityProfile"/> is a planner profile.</summary>
    /// <param name="capabilityProfile">A capability profile name, or null.</param>
    public static bool IsPlanner(string? capabilityProfile) =>
        capabilityProfile is not null
        && TargetClasses.TryGetValue(capabilityProfile, out string? targetClass)
        && string.Equals(targetClass, Planner, StringComparison.Ordinal);
}
