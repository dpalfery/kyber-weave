namespace KyberWeave.Domain.Hooks;

/// <summary>Which file a hook configuration overlay came from.</summary>
public enum HookConfigSource
{
    /// <summary>The repository's <c>.kyber-weave/hooks.yml</c>: it may tune the built-in rules only.</summary>
    Repository,

    /// <summary>The user's <c>~/.kyber-weave/hooks.yml</c>: it may tune the built-in rules and add its own.</summary>
    User,
}

/// <summary>Merges parsed hook configuration over the built-in defaults.</summary>
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
    /// <param name="overlay">The parsed file.</param>
    /// <param name="source">Which file the overlay came from, which decides whether it may name a new command.</param>
    /// <returns>
    /// The merged config. A rule whose id matches a default replaces only the fields it sets. A repository file may
    /// tune the built-in rules but not add one: a new rule needs a command, and a command comes from the user's own
    /// file. The user's file may add a rule, which must set kind, include and run, or it is dropped with a warning.
    /// Numeric overlays outside their allowed range (time budget 1 to 120 seconds, rule timeout 1 to 300 seconds,
    /// output 1 to 500 lines and 100 to 100000 bytes) are clamped to the nearest limit with a warning.
    /// </returns>
    public static HooksConfig Merge(HooksConfig defaults, HooksConfigOverlay overlay, HookConfigSource source)
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
                // Tuning a built-in rule must not become a way to swap its command: the same trust rule that
                // refuses a new rule applies to the command of an existing one.
                HookRuleOverlay effective = change;
                if (source != HookConfigSource.User && change.Run is { Count: > 0 })
                {
                    warnings.Add(
                        $"rule '{change.Id}': the repository file may tune a built-in rule but cannot change its " +
                        "command; the command was ignored. Set it in the user file, ~/.kyber-weave/hooks.yml.");
                    effective = change with { Run = null };
                }

                rules[index] = Apply(rules[index], effective, warnings);
                continue;
            }

            if (source != HookConfigSource.User)
            {
                warnings.Add(
                    $"rule '{change.Id}' ignored: the repository file tunes the built-in rules but cannot add a " +
                    "command; put a new rule in the user file, ~/.kyber-weave/hooks.yml.");
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
            warnings.Add($"{scope}: '{name}' must be between {min} and {max}; got {given}; using {clamped}.");
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
