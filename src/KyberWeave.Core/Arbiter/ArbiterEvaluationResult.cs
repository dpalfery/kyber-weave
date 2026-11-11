namespace KyberWeave.Core.Arbiter;

/// <summary>One dispatch's outcome inside an <see cref="ArbiterEvaluationResult"/>.</summary>
/// <param name="Trigger">The classified trigger, or null when the dispatch passed with none.</param>
/// <param name="Outcome">The trigger's combined effect.</param>
public sealed record ArbiterDispatchEvaluation(string? Trigger, string Outcome)
{
    /// <summary>The secondary triggers evaluated behind this dispatch, in derivation order.</summary>
    public IReadOnlyList<ArbiterSecondaryEvaluation> Secondary { get; init; } = [];
}

/// <summary>One secondary trigger's outcome behind a primary dispatch.</summary>
/// <remarks>
/// A secondary outcome is neutral when its rules were shadowed or the primary did not allow:
/// it then reports the family's neutral effect, and the would-be effect lives on its record.
/// </remarks>
/// <param name="Trigger">The secondary trigger.</param>
/// <param name="Outcome">The secondary trigger's combined effect after shadow and suppression.</param>
/// <param name="Payload">The answers the secondary rules gave, comma-separated, or null when none did.</param>
public sealed record ArbiterSecondaryEvaluation(string Trigger, string Outcome, string? Payload)
{
    /// <summary>The decision record this secondary wrote, which a substitute digest names.</summary>
    public string? DecisionId { get; init; }
}

/// <summary>
/// The evaluator's answer for one event: the event-level outcome and every
/// dispatch's outcome. An error result is never <c>allow</c> and carries
/// <c>KW-ARB-HOOK-001</c>.
/// </summary>
/// <remarks>
/// The event escalates when any dispatch does; otherwise the first dispatch's
/// outcome stands. Pass-through dispatches (no trigger) report <c>allow</c>.
/// </remarks>
/// <param name="Outcome">The event-level outcome.</param>
/// <param name="IsError">Whether evaluation failed and this is an error result.</param>
/// <param name="ErrorCode">The hook error code, when <see cref="IsError"/>.</param>
/// <param name="ErrorMessage">The failure message, when <see cref="IsError"/>.</param>
/// <param name="Dispatches">One outcome per evaluated dispatch, in order.</param>
/// <param name="LedgerId">The ledger event this evaluation appended (empty on error before append).</param>
public sealed record ArbiterEvaluationResult(
    string Outcome,
    bool IsError,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<ArbiterDispatchEvaluation> Dispatches,
    string LedgerId);
