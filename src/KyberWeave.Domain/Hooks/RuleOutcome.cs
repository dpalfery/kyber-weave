namespace KyberWeave.Domain.Hooks;

/// <summary>How one planned command ended.</summary>
/// <param name="RuleId">The rule that produced the command.</param>
/// <param name="Kind">The rule kind.</param>
/// <param name="Blocking">Whether the rule's failure blocks.</param>
/// <param name="ExitCode">The command's exit code.</param>
/// <param name="TimedOut">Whether the command hit its time limit and was killed.</param>
/// <param name="Output">Merged stdout and stderr.</param>
public sealed record RuleOutcome(string RuleId, HookRuleKind Kind, bool Blocking, int ExitCode, bool TimedOut, string Output)
{
    /// <summary>Gets whether the command ran and reported a problem.</summary>
    public bool Failed => !TimedOut && ExitCode != 0;

    /// <summary>Gets whether the command failed or timed out, so the agent should hear about it.</summary>
    public bool NeedsAttention => TimedOut || ExitCode != 0;
}
