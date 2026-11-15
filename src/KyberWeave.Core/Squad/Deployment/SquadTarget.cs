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

/// <summary>Canonical target tokens, aliases, and parsing for Kyber-Squad.</summary>
public static class SquadTargetCatalog
{
    private static readonly IReadOnlyList<SquadTarget> ApprovedTargets =
        Array.AsReadOnly(
        [
            SquadTarget.Codex,
            SquadTarget.Cursor,
            SquadTarget.Claude,
            SquadTarget.Copilot,
            SquadTarget.OpenCode,
            SquadTarget.Kilo,
            SquadTarget.Antigravity,
            SquadTarget.Warp,
            SquadTarget.Factory,
            SquadTarget.Pi,
            SquadTarget.ZCode,
            SquadTarget.Devin,
            SquadTarget.CommandCode
        ]);

    private static readonly IReadOnlyDictionary<string, SquadTarget> TargetsByToken =
        new Dictionary<string, SquadTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = SquadTarget.Codex,
            ["cursor"] = SquadTarget.Cursor,
            ["claude"] = SquadTarget.Claude,
            ["copilot"] = SquadTarget.Copilot,
            ["github-copilot"] = SquadTarget.Copilot,
            ["opencode"] = SquadTarget.OpenCode,
            ["kilo"] = SquadTarget.Kilo,
            ["antigravity"] = SquadTarget.Antigravity,
            ["warp"] = SquadTarget.Warp,
            ["factory"] = SquadTarget.Factory,
            ["factory-droids"] = SquadTarget.Factory,
            ["pi"] = SquadTarget.Pi,
            ["zcode"] = SquadTarget.ZCode,
            ["devin"] = SquadTarget.Devin,
            ["commandcode"] = SquadTarget.CommandCode,
            ["command-code"] = SquadTarget.CommandCode
        };

    /// <summary>The approved target roster in stable presentation order.</summary>
    public static IReadOnlyList<SquadTarget> All => ApprovedTargets;

    /// <summary>Returns the canonical command-line token for a target.</summary>
    public static string GetToken(SquadTarget target) => target switch
    {
        SquadTarget.Codex => "codex",
        SquadTarget.Cursor => "cursor",
        SquadTarget.Claude => "claude",
        SquadTarget.Copilot => "copilot",
        SquadTarget.OpenCode => "opencode",
        SquadTarget.Kilo => "kilo",
        SquadTarget.Antigravity => "antigravity",
        SquadTarget.Warp => "warp",
        SquadTarget.Factory => "factory",
        SquadTarget.Pi => "pi",
        SquadTarget.ZCode => "zcode",
        SquadTarget.Devin => "devin",
        SquadTarget.CommandCode => "commandcode",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown Squad target.")
    };

    /// <summary>
    /// Returns how <paramref name="target"/> projects canonical agents. This is a fact about the
    /// harness, so it lives here beside its token rather than in the registry's validation: adding a
    /// harness states it once, and every renderer is checked against it.
    /// </summary>
    public static SquadAgentProjection GetAgentProjection(SquadTarget target) => target switch
    {
        SquadTarget.Codex or SquadTarget.Cursor or SquadTarget.Claude or SquadTarget.Copilot or
            SquadTarget.OpenCode or SquadTarget.Kilo or SquadTarget.Antigravity or SquadTarget.Factory or
            SquadTarget.Pi or SquadTarget.ZCode or SquadTarget.Devin or SquadTarget.CommandCode =>
            SquadAgentProjection.Native,
        SquadTarget.Warp => SquadAgentProjection.RoleSkillFallback,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown Squad target.")
    };

    /// <summary>
    /// Returns whether a dispatched agent on <paramref name="target"/> can dispatch agents of its own, as
    /// rendered (ADR 0079). A harness is <see cref="SquadSubagentDispatch.Nested"/> only where that was
    /// verified and needs no operator setting Squad does not write; every other harness takes the safe path.
    /// </summary>
    /// <remarks>
    /// Claude nests by default (three layers) when the agent lists <c>Agent</c>; Cursor lets a direct
    /// sub-agent launch one more layer; Copilot CLI nested a <c>review-lens</c> under a dispatched
    /// <c>code-reviewer</c> live, and VS Code needs <c>chat.subagents.allowInvocationsFromSubagents</c>; Pi
    /// nests to depth 2 when the middle agent lists <c>allowed_subagents</c>, which its renderer emits from
    /// <c>delegates-to</c>. OpenCode and Kilo refuse a nested task unless the operator raises
    /// <c>subagent_depth</c> above its default of 1, Factory gives a droid no <c>Task</c> tool, Devin withholds
    /// nested delegation (ADR 0025), and Codex, Antigravity, Warp, ZCode and Command Code are unverified.
    /// </remarks>
    public static SquadSubagentDispatch GetSubagentDispatch(SquadTarget target) => target switch
    {
        SquadTarget.Claude or SquadTarget.Copilot or SquadTarget.Cursor or SquadTarget.Pi =>
            SquadSubagentDispatch.Nested,
        SquadTarget.Codex or SquadTarget.OpenCode or SquadTarget.Kilo or SquadTarget.Antigravity or
            SquadTarget.Warp or SquadTarget.Factory or SquadTarget.ZCode or SquadTarget.Devin or
            SquadTarget.CommandCode =>
            SquadSubagentDispatch.TopLevelOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown Squad target.")
    };

    /// <summary>
    /// Names the tool a dispatched agent on <paramref name="target"/> calls to start a sub-agent, as the
    /// model sees it, so canonical text can say the tool literally instead of describing it. Copilot CLI
    /// exposes <c>task</c> (parameter <c>agent_type</c>) and VS Code <c>agent</c>; one rendered file serves both.
    /// </summary>
    public static string GetSubagentDispatchTool(SquadTarget target) => target switch
    {
        SquadTarget.Claude => "`Agent` tool (parameter `subagent_type`)",
        SquadTarget.Copilot => "`task` tool (parameter `agent_type`; in VS Code the tool is `agent`)",
        SquadTarget.Cursor => "`Task` tool",
        _ => "tool that starts a sub-agent"
    };

    /// <summary>
    /// Resolves one exact canonical token without throwing. Returns <see langword="false"/> for an
    /// unknown or null token, so recovery paths can degrade instead of aborting.
    /// </summary>
    public static bool TryResolve(string? token, out SquadTarget target)
    {
        if (token is not null)
        {
            // Same normalization Parse applies to each comma-separated segment (TrimEntries):
            // surrounding whitespace is not part of the token. Comma-containing input and "all"
            // expand to zero or more than one target, so they never resolve to a single target.
            string trimmed = token.Trim();
            if (trimmed.Length != 0 && TargetsByToken.TryGetValue(trimmed, out target))
                return true;
        }

        target = default;
        return false;
    }

    /// <summary>
    /// Parses repeated or comma-separated target arguments into a first-seen ordered set.
    /// </summary>
    public static IReadOnlyList<SquadTarget> Parse(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        List<SquadTarget> parsed = new List<SquadTarget>();
        HashSet<SquadTarget> seen = new HashSet<SquadTarget>();
        foreach (string? value in values)
        {
            if (value is null)
                throw UnknownTarget(string.Empty);

            foreach (string segment in value.Split(',', StringSplitOptions.TrimEntries))
            {
                if (string.Equals(segment, "all", StringComparison.OrdinalIgnoreCase))
                {
                    AddUnique(ApprovedTargets, parsed, seen);
                    continue;
                }

                if (!TargetsByToken.TryGetValue(segment, out SquadTarget target))
                    throw UnknownTarget(segment);

                AddUnique([target], parsed, seen);
            }
        }

        return parsed;
    }

    private static void AddUnique(
        IEnumerable<SquadTarget> candidates,
        ICollection<SquadTarget> parsed,
        ISet<SquadTarget> seen)
    {
        foreach (SquadTarget candidate in candidates)
        {
            if (seen.Add(candidate))
                parsed.Add(candidate);
        }
    }

    private static ArgumentException UnknownTarget(string value) =>
        new(
            $"Unknown Squad target '{value}'. Known targets: " +
            $"{string.Join(", ", ApprovedTargets.Select(GetToken))}, all.",
            nameof(value));
}
