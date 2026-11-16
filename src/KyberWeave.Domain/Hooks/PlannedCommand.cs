namespace KyberWeave.Domain.Hooks;

/// <summary>A rule resolved to a concrete command for one changed file.</summary>
/// <param name="RuleId">The rule that produced it.</param>
/// <param name="Kind">The rule kind.</param>
/// <param name="Argv">The program followed by its arguments, tokens already replaced.</param>
/// <param name="TimeoutSeconds">Longest it may run.</param>
/// <param name="Blocking">Whether its failure makes the hook exit 2.</param>
public sealed record PlannedCommand(
    string RuleId,
    HookRuleKind Kind,
    IReadOnlyList<string> Argv,
    int TimeoutSeconds,
    bool Blocking);
