using KyberWeave.Arbiter.Hooks;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Canonical target tokens, aliases, and parsing for Kyber-Squad.</summary>
public static class SquadTargetCatalog
{
    private static readonly IReadOnlyList<SquadTarget> ApprovedTargets =
        Array.AsReadOnly(HarnessRegistry.SquadTargets.ToArray());

    private static readonly IReadOnlyDictionary<string, SquadTarget> TargetsByToken = BuildTargetsByToken();

    /// <summary>The approved target roster in stable presentation order.</summary>
    public static IReadOnlyList<SquadTarget> All => ApprovedTargets;

    /// <summary>Returns the canonical command-line token for a target.</summary>
    public static string GetToken(SquadTarget target) => HarnessRegistry.SquadTokenFor(target);

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

    private static Dictionary<string, SquadTarget> BuildTargetsByToken()
    {
        Dictionary<string, SquadTarget> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (HarnessDescriptor row in HarnessRegistry.Rows)
        {
            if (row.SquadTarget is not { } target || row.SquadToken is null)
                continue;

            map[row.SquadToken] = target;
            foreach (string alias in row.SquadAliases)
                map[alias] = target;
        }

        return map;
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
