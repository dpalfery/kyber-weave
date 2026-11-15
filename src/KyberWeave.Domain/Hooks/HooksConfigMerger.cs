namespace KyberWeave.Domain.Hooks;

/// <summary>Merges a parsed repository config over the built-in defaults.</summary>
public static class HooksConfigMerger
{
    /// <summary>Applies <paramref name="overlay"/> to <paramref name="defaults"/>.</summary>
    /// <param name="defaults">The configuration to start from.</param>
    /// <param name="overlay">The parsed repository file.</param>
    /// <returns>
    /// The merged config. A rule whose id matches a default replaces only the fields it sets; any other rule is
    /// appended and must set kind, include and run, or it is dropped with a warning.
    /// </returns>
    public static HooksConfig Merge(HooksConfig defaults, HooksConfigOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(overlay);

        List<HookRule> rules = [.. defaults.Rules];
        List<string> warnings = [.. defaults.Warnings, .. overlay.Warnings];

        foreach (HookRuleOverlay change in overlay.Rules)
        {
            int index = rules.FindIndex(r => string.Equals(r.Id, change.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                rules[index] = Apply(rules[index], change);
                continue;
            }

            if (change.Kind is null || change.Include is null || change.Run is null || change.Run.Count == 0)
            {
                warnings.Add($"rule '{change.Id}' ignored: a new rule needs kind, include and run.");
                continue;
            }

            rules.Add(new HookRule(
                change.Id,
                change.Kind.Value,
                change.Include,
                change.Exclude ?? [],
                change.Run,
                change.TimeoutSeconds ?? 25,
                change.Blocking ?? true,
                change.Enabled ?? true));
        }

        return new HooksConfig(
            overlay.TimeBudgetSeconds ?? defaults.TimeBudgetSeconds,
            overlay.StopOnFirstBlocking ?? defaults.StopOnFirstBlocking,
            overlay.MaxOutputLines ?? defaults.MaxOutputLines,
            overlay.MaxOutputBytes ?? defaults.MaxOutputBytes,
            rules,
            warnings);
    }

    private static HookRule Apply(HookRule rule, HookRuleOverlay change) => rule with
    {
        Kind = change.Kind ?? rule.Kind,
        Include = change.Include ?? rule.Include,
        Exclude = change.Exclude ?? rule.Exclude,
        Run = change.Run is { Count: > 0 } ? change.Run : rule.Run,
        TimeoutSeconds = change.TimeoutSeconds ?? rule.TimeoutSeconds,
        Blocking = change.Blocking ?? rule.Blocking,
        Enabled = change.Enabled ?? rule.Enabled
    };
}
