namespace KyberWeave.Domain.Hooks;

/// <summary>Chooses the commands that apply to one changed file.</summary>
public static class HookRouter
{
    /// <summary>Gets whether any enabled rule of <paramref name="kind"/> matches the path by glob alone.</summary>
    /// <param name="config">The effective config.</param>
    /// <param name="kind">The rule kind to look for.</param>
    /// <param name="relativePath">The repository-relative changed file.</param>
    /// <returns>True when a matching rule exists, so callers can skip work that only that kind needs.</returns>
    public static bool AnyRuleMatches(HooksConfig config, HookRuleKind kind, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Rules.Any(r => Matches(r, relativePath) && r.Kind == kind);
    }

    /// <summary>Plans the commands for a changed file.</summary>
    /// <param name="config">The effective config.</param>
    /// <param name="relativePath">The repository-relative changed file.</param>
    /// <param name="facts">The project facts for the file.</param>
    /// <returns>
    /// Commands in order: lint rules first, then test rules, each in config order. A rule is left out when it is
    /// disabled, its globs do not match, or a token it uses has no value for this file.
    /// </returns>
    public static IReadOnlyList<PlannedCommand> Plan(HooksConfig config, string relativePath, ProjectFacts facts)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(facts);

        List<PlannedCommand> planned = [];
        foreach (HookRule rule in config.Rules.Where(r => Matches(r, relativePath)).OrderBy(r => r.Kind))
        {
            if (TryExpand(rule.Run, relativePath, facts, out List<string>? argv))
            {
                planned.Add(new PlannedCommand(rule.Id, rule.Kind, argv, rule.TimeoutSeconds, rule.Blocking));
            }
        }

        return planned;
    }

    private static bool Matches(HookRule rule, string path) =>
        rule.Enabled
        && rule.Run.Count > 0
        && GlobMatcher.IsMatchAny(rule.Include, path)
        && !GlobMatcher.IsMatchAny(rule.Exclude, path);

    private static bool TryExpand(IReadOnlyList<string> run, string file, ProjectFacts facts, out List<string> argv)
    {
        argv = [];
        foreach (string arg in run)
        {
            if (arg == "{testClasses}")
            {
                if (facts.TestClasses.Count == 0)
                {
                    return false;
                }

                argv.AddRange(facts.TestClasses);
                continue;
            }

            string expanded = arg
                .Replace("{file}", file, StringComparison.Ordinal)
                .Replace("{root}", facts.Root, StringComparison.Ordinal);
            if (!TryReplace(ref expanded, "{target}", facts.Project ?? facts.Marker)
                || !TryReplace(ref expanded, "{marker}", facts.Marker)
                || !TryReplace(ref expanded, "{project}", facts.Project)
                || !TryReplace(ref expanded, "{testProject}", facts.TestProject))
            {
                return false;
            }

            argv.Add(expanded);
        }

        return true;
    }

    private static bool TryReplace(ref string text, string token, string? value)
    {
        if (!text.Contains(token, StringComparison.Ordinal))
        {
            return true;
        }

        if (value is null)
        {
            return false;
        }

        text = text.Replace(token, value, StringComparison.Ordinal);
        return true;
    }
}
