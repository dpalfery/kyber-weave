namespace KyberWeave.Core.Squad.Deployment;

/// <summary>A coding harness supported by Kyber-Squad.</summary>
public enum SquadTarget
{
    Codex,
    Cursor,
    Claude,
    Copilot,
    OpenCode,
    Kilo,
    Antigravity,
    Warp,
    Factory,
    Pi,
    ZCode,
    Devin,
    CommandCode
}

/// <summary>How a harness projects canonical agents, which decides what its output may contain.</summary>
public enum SquadAgentProjection
{
    /// <summary>
    /// The harness has an agent primitive. Agents and skills occupy separate namespaces, so no
    /// output carries a <c>role-</c> prefix and a shared identity projects at most one skill.
    /// </summary>
    Native,

    /// <summary>
    /// The harness has no agent primitive. Every agent lowers to a role skill, emits no agent file,
    /// and a distinct-body collision ships both the canonical skill and a <c>role-</c> skill.
    /// </summary>
    RoleSkillFallback
}

/// <summary>Whether a dispatched agent on a harness can itself dispatch agents.</summary>
public enum SquadSubagentDispatch
{
    /// <summary>
    /// A sub-agent can start sub-agents as rendered, so one <c>code-reviewer</c> dispatch runs the whole
    /// review council.
    /// </summary>
    Nested,

    /// <summary>
    /// Only the session a human started can dispatch, or nesting is unverified or off by default, so that
    /// session seats the review council itself through the <c>review-council</c> skill.
    /// </summary>
    TopLevelOnly
}
