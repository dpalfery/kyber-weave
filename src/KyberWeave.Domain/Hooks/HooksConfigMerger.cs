namespace KyberWeave.Domain.Hooks;

/// <summary>Merges a parsed repository config over the built-in defaults.</summary>
public static class HooksConfigMerger
{
    private const int MinTimeBudgetSeconds = 1;
    private const int MaxTimeBudgetSeconds = 120;
    private const int MinRuleTimeoutSeconds = 1;
    private const int MaxRuleTimeoutSeconds = 300;
    private const int MinOutputLines = 1;
    private const int MaxOutputLinesLimit = 500;
    private const int MinOutputBytes = 100;
    private const int MaxOutputBytesLimit = 100_000;

    /// <summary>Applies <paramref name="overlay"/> to <paramref name="defaults"/>.</summary>
    /// <param name="defaults">The configuration to start from.</param>
    /// <param name="overlay">The parsed repository file.</param>
    /// <returns>
    /// The merged config. A rule whose id matches a default replaces only the fields it sets; any other rule is
    /// appended and must set kind, include and run, or it is dropped with a warning. Numeric overlays outside their
    /// allowed range (time budget 1 to 120 seconds, rule timeout 1 to 300 seconds, output 1 to 500 lines and 100 to
    /// 100000 bytes) are clamped to the nearest limit with a warning.
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
                rules[index] = Apply(rules[index], change, warnings);
                continue;
            }

            if (change.Kind is null || change.Include is null || change.Run is null || change.Run.Count == 0)
            {
                warnings.Add($"rule '{change.Id}' ignored: a new rule needs kind, include and run.");
                continue;
            }

            rules.Add(Apply(new HookRule(change.Id, change.Kind.Value, [], [], [], 25, true, true), change, warnings));
        }

        return new HooksConfig(
            Clamp(overlay.TimeBudgetSeconds, defaults.TimeBudgetSeconds, MinTimeBudgetSeconds, MaxTimeBudgetSeconds, "time-budget-seconds", warnings),
            overlay.StopOnFirstBlocking ?? defaults.StopOnFirstBlocking,
            Clamp(overlay.MaxOutputLines, defaults.MaxOutputLines, MinOutputLines, MaxOutputLinesLimit, "max-output-lines", warnings),
            Clamp(overlay.MaxOutputBytes, defaults.MaxOutputBytes, MinOutputBytes, MaxOutputBytesLimit, "max-output-bytes", warnings),
            rules,
            warnings);
    }

    private static int Clamp(int? value, int fallback, int min, int max, string name, List<string> warnings, string scope = "hooks.yml")
    {
        if (value is not { } given)
        {
            return fallback;
        }

        int clamped = Math.Clamp(given, min, max);
        if (clamped != given)
        {
            warnings.Add($"{scope}: '{name}' must be between {min} and {max}; using {clamped}.");
        }

        return clamped;
    }

    private static HookRule Apply(HookRule rule, HookRuleOverlay change, List<string> warnings) => rule with
    {
        Kind = change.Kind ?? rule.Kind,
        Include = change.Include ?? rule.Include,
        Exclude = change.Exclude ?? rule.Exclude,
        Run = change.Run is { Count: > 0 } ? change.Run : rule.Run,
        TimeoutSeconds = Clamp(change.TimeoutSeconds, rule.TimeoutSeconds, MinRuleTimeoutSeconds, MaxRuleTimeoutSeconds, "timeout-seconds", warnings, $"hooks.yml: rule '{change.Id}'"),
        Blocking = change.Blocking ?? rule.Blocking,
        Enabled = change.Enabled ?? rule.Enabled
    };
}
