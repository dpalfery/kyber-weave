using KyberWeave.Core.Review;

namespace KyberWeave.Domain.Hooks;

/// <summary>Chooses the commands that apply to one changed file.</summary>
public static class HookRouter
{
    /// <summary>Gets the enabled rules whose globs match a changed file.</summary>
    /// <param name="config">The effective config.</param>
    /// <param name="relativePath">The repository-relative changed file.</param>
    /// <returns>The matching rules in config order, so callers can skip work that only some kinds need.</returns>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is empty, absolute, or climbs out of the repository with <c>..</c>.</exception>
    public static IReadOnlyList<HookRule> MatchingRules(HooksConfig config, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(config);
        string path = Normalize(relativePath);
        return [.. config.Rules.Where(r => Matches(r, path))];
    }

    /// <summary>Plans the commands for a changed file.</summary>
    /// <param name="config">The effective config.</param>
    /// <param name="relativePath">The repository-relative changed file.</param>
    /// <param name="facts">The project facts for the file.</param>
    /// <param name="notes">When given, receives one line for each rule left out because <c>{testClasses}</c> is embedded in a larger argument.</param>
    /// <returns>The commands from <see cref="Plan(IReadOnlyList{HookRule}, string, ProjectFacts, ICollection{string}?)"/> for the matching rules.</returns>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is empty, absolute, or climbs out of the repository with <c>..</c>.</exception>
    public static IReadOnlyList<PlannedCommand> Plan(HooksConfig config, string relativePath, ProjectFacts facts, ICollection<string>? notes = null) =>
        Plan(MatchingRules(config, relativePath), relativePath, facts, notes);

    /// <summary>Plans the commands for rules that already matched a changed file.</summary>
    /// <param name="matchingRules">The rules from <see cref="MatchingRules"/>.</param>
    /// <param name="relativePath">The repository-relative changed file.</param>
    /// <param name="facts">The project facts for the file.</param>
    /// <param name="notes">When given, receives one line for each rule left out because <c>{testClasses}</c> is embedded in a larger argument.</param>
    /// <returns>
    /// Commands in order: lint rules first, then test rules, each in config order. A rule is left out when a token
    /// it uses has no value for this file, or when <c>{testClasses}</c> appears inside a larger argument instead of
    /// standing alone.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is empty, absolute, or climbs out of the repository with <c>..</c>.</exception>
    public static IReadOnlyList<PlannedCommand> Plan(IReadOnlyList<HookRule> matchingRules, string relativePath, ProjectFacts facts, ICollection<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(matchingRules);
        ArgumentNullException.ThrowIfNull(facts);
        relativePath = Normalize(relativePath);

        List<PlannedCommand> planned = [];
        foreach (HookRule rule in matchingRules.Where(r => r.Kind == HookRuleKind.Lint).Concat(matchingRules.Where(r => r.Kind == HookRuleKind.Test)))
        {
            if (TryExpand(rule.Run, relativePath, facts, out List<string>? argv))
            {
                planned.Add(new PlannedCommand(rule.Id, rule.Kind, argv, rule.TimeoutSeconds, rule.Blocking));
            }
            else if (notes is not null && rule.Run.Any(HasEmbeddedTestClasses))
            {
                notes.Add($"rule '{rule.Id}' was not run: {{testClasses}} must be a whole argument, not part of a longer one.");
            }
        }

        return planned;
    }

    private static string Normalize(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string unified = relativePath.Replace('\\', '/');
        if (unified.StartsWith('/') || Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException("The path must be relative to the repository root.", nameof(relativePath));
        }

        List<string> segments = [];
        foreach (string segment in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new ArgumentException("The path must stay inside the repository root.", nameof(relativePath));
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return segments.Count == 0
            ? throw new ArgumentException("The path must name a file.", nameof(relativePath))
            : string.Join('/', segments);
    }

    private static bool Matches(HookRule rule, string path) =>
        rule.Enabled
        && rule.Run.Count > 0
        && rule.Include.Any(pattern => PathGlob.IsMatch(pattern, path))
        && !rule.Exclude.Any(pattern => PathGlob.IsMatch(pattern, path));

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

            if (HasEmbeddedTestClasses(arg))
            {
                return false;
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

    private static bool HasEmbeddedTestClasses(string arg) =>
        arg != "{testClasses}" && arg.Contains("{testClasses}", StringComparison.Ordinal);

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
