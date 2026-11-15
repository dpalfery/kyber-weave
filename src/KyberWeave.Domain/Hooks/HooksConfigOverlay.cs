namespace KyberWeave.Domain.Hooks;

/// <summary>A repository config file after parsing: every value is optional and replaces the default when set.</summary>
/// <param name="TimeBudgetSeconds">Replacement total time budget.</param>
/// <param name="StopOnFirstBlocking">Replacement stop-on-first-blocking flag.</param>
/// <param name="MaxOutputLines">Replacement per-rule line limit.</param>
/// <param name="MaxOutputBytes">Replacement per-rule byte limit.</param>
/// <param name="Rules">Rule overrides and additions.</param>
/// <param name="Warnings">Problems found while parsing.</param>
public sealed record HooksConfigOverlay(
    int? TimeBudgetSeconds,
    bool? StopOnFirstBlocking,
    int? MaxOutputLines,
    int? MaxOutputBytes,
    IReadOnlyList<HookRuleOverlay> Rules,
    IReadOnlyList<string> Warnings);

/// <summary>A rule from the repository file. Set only the fields to change on a built-in rule with the same id.</summary>
/// <param name="Id">The rule id.</param>
/// <param name="Kind">Replacement kind.</param>
/// <param name="Include">Replacement include globs.</param>
/// <param name="Exclude">Replacement exclude globs.</param>
/// <param name="Run">Replacement command.</param>
/// <param name="TimeoutSeconds">Replacement per-command timeout.</param>
/// <param name="Blocking">Replacement blocking flag.</param>
/// <param name="Enabled">Replacement enabled flag.</param>
public sealed record HookRuleOverlay(
    string Id,
    HookRuleKind? Kind,
    IReadOnlyList<string>? Include,
    IReadOnlyList<string>? Exclude,
    IReadOnlyList<string>? Run,
    int? TimeoutSeconds,
    bool? Blocking,
    bool? Enabled);
