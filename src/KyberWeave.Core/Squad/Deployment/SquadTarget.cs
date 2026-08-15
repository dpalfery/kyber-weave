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
    Gemini,
    Antigravity,
    Warp,
    Factory
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
            SquadTarget.Gemini,
            SquadTarget.Antigravity,
            SquadTarget.Warp,
            SquadTarget.Factory
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
            ["gemini"] = SquadTarget.Gemini,
            ["antigravity"] = SquadTarget.Antigravity,
            ["warp"] = SquadTarget.Warp,
            ["factory"] = SquadTarget.Factory,
            ["factory-droids"] = SquadTarget.Factory
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
        SquadTarget.Gemini => "gemini",
        SquadTarget.Antigravity => "antigravity",
        SquadTarget.Warp => "warp",
        SquadTarget.Factory => "factory",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown Squad target.")
    };

    /// <summary>
    /// Parses repeated or comma-separated target arguments into a first-seen ordered set.
    /// </summary>
    public static IReadOnlyList<SquadTarget> Parse(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var parsed = new List<SquadTarget>();
        var seen = new HashSet<SquadTarget>();
        foreach (var value in values)
        {
            if (value is null)
                throw UnknownTarget(string.Empty);

            foreach (var segment in value.Split(',', StringSplitOptions.TrimEntries))
            {
                if (string.Equals(segment, "all", StringComparison.OrdinalIgnoreCase))
                {
                    AddUnique(ApprovedTargets, parsed, seen);
                    continue;
                }

                if (!TargetsByToken.TryGetValue(segment, out var target))
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
        foreach (var candidate in candidates)
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
