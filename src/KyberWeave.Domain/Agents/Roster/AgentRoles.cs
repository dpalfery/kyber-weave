namespace KyberWeave.Core.Agents.Roster;

/// <summary>
/// The shipped Squad agent names that carry a role in the routing code, and the predicates over them.
/// </summary>
/// <remarks>
/// The names are declared here so a caller comparison, a delegation roster and a rendered constant cannot
/// spell the same agent differently. <see cref="Retired"/> is the one map of the names the roster removal
/// deleted to the agent that does their work now.
/// </remarks>
public static class AgentRoles
{
    /// <summary>The planner that owns plans, specifications and task files.</summary>
    public const string Architect = "architect";

    /// <summary>The planner that owns a formal specification phase.</summary>
    public const string ProductOwner = "product-owner";

    /// <summary>The review council's root agent.</summary>
    public const string CodeReviewer = "code-reviewer";

    /// <summary>The orchestrator a human starts.</summary>
    public const string Conductor = "conductor";

    /// <summary>The root agent that fixes one bounded defect.</summary>
    public const string BugCrusher = "bug-crusher";

    /// <summary>The read-only investigator the bug-crusher starts.</summary>
    public const string BugCrusherInvestigator = "bug-crusher-investigator";

    /// <summary>The documentation worker.</summary>
    public const string DocsDev = "docs-dev";

    /// <summary>The delivery agent, which replaces the removed <c>github-devops</c>.</summary>
    public const string Devops = "devops";

    /// <summary>The read-only live-platform researcher.</summary>
    public const string PlatformResearcher = "platform-researcher";

    /// <summary>One seat of the review council.</summary>
    public const string ReviewLens = "review-lens";

    /// <summary>The review council's triage seat.</summary>
    public const string ReviewTriage = "review-triage";

    /// <summary>The read-only external-fact researcher.</summary>
    public const string ResearchAgent = "research-agent";

    /// <summary>The read-only reviewer of a saved plan.</summary>
    public const string PlanReviewer = "plan-reviewer";

    /// <summary>
    /// Whether <paramref name="name"/> is one of the two planner agents. The profile
    /// <c>product-planning</c> is not an agent; see <see cref="CapabilityProfiles.IsPlanner"/>.
    /// </summary>
    /// <param name="name">An agent name, or null.</param>
    /// <param name="comparison">How to compare the name; the default is the canonical exact match.</param>
    public static bool IsPlanner(string? name, StringComparison comparison = StringComparison.Ordinal) =>
        string.Equals(name, Architect, comparison) || string.Equals(name, ProductOwner, comparison);

    /// <summary>Whether <paramref name="name"/> is the review council's root agent.</summary>
    /// <param name="name">An agent name, or null.</param>
    /// <param name="comparison">How to compare the name; the default is the canonical exact match.</param>
    public static bool IsReviewer(string? name, StringComparison comparison = StringComparison.Ordinal) =>
        string.Equals(name, CodeReviewer, comparison);

    /// <summary>
    /// The names the roster removal deletes, each mapped to the agent that does its work afterwards.
    /// A removed technology developer maps to the developer kind: the task's difficulty then chooses its tier.
    /// </summary>
    /// <remarks>
    /// The lookup ignores case because a plan's hand-written <c>Role:</c> line is not normalised: an author
    /// who writes <c>CSharp-Dev</c> must still reach the developer tier. The values stay canonical.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Retired { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp-dev"] = DeveloperTiers.Medium,
            ["dal-dev"] = DeveloperTiers.Medium,
            ["maui-dev"] = DeveloperTiers.Medium,
            ["pulumi-dev"] = DeveloperTiers.Medium,
            ["typescript-dev"] = DeveloperTiers.Medium,
            ["react-dev"] = DeveloperTiers.Medium,
            ["rust-dev"] = DeveloperTiers.Medium,
            ["tauri-dev"] = DeveloperTiers.Medium,
            ["go-dev"] = DeveloperTiers.Medium,
            ["python-dev"] = DeveloperTiers.Medium,
            ["test-dev"] = DeveloperTiers.Medium,
            ["sql-database-architect"] = Architect,
            ["azure-reader"] = PlatformResearcher,
            ["github-devops"] = Devops,
        };
}
