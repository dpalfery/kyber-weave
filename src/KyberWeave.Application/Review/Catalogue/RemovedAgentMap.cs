using KyberWeave.Core.Agents.Roster;

namespace KyberWeave.Core.Review.Catalogue;

/// <summary>
/// The agents the removal phase deletes, and the command that moves a host's configuration off them.
/// </summary>
/// <remarks>
/// The names stay here after the files are gone, because a host's <c>review.task-gates</c> entries outlive the
/// agents that wrote them: Requirement 13.2 keeps an agent-keyed entry resolving for two minor releases through
/// <c>TechnologyAgentMap</c>, and Requirement 13.4 reports each one for a removed agent as a warning that
/// names <see cref="MigrationCommand"/>. The names themselves are <see cref="AgentRoles.Retired"/>, the one list
/// all readers share, so the departure of an agent and the warning about its config entry cannot disagree.
/// </remarks>
public static class RemovedAgentMap
{
    /// <summary>The command that rewrites an agent-keyed task gate as a technology-keyed one.</summary>
    public const string MigrationCommand = "kyber-weave review migrate-task-gates";

    /// <summary>
    /// The fourteen agents the removal phase deletes: the ten technology agents,
    /// <c>sql-database-architect</c>, <c>test-dev</c>, <c>azure-reader</c> and <c>github-devops</c>.
    /// </summary>
    /// <remarks>
    /// <c>docs-dev</c> stays, because it follows the documentation settings a host already customises, and
    /// <c>devops</c> is its replacement rather than one of the departed.
    /// </remarks>
    public static IReadOnlyList<string> Agents { get; } = [.. AgentRoles.Retired.Keys];

    /// <summary>Whether the removal phase deletes <paramref name="agent"/>.</summary>
    /// <param name="agent">An agent slug, or null.</param>
    public static bool IsRemoved(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
            return false;

        return AgentRoles.Retired.ContainsKey(agent.Trim());
    }
}
