namespace KyberWeave.Core.Arbiter;

/// <summary>One dispatch's outcome inside an <see cref="ArbiterEvaluationResult"/>.</summary>
/// <param name="Trigger">The classified trigger, or null when the dispatch passed with none.</param>
/// <param name="Outcome">The trigger's combined effect.</param>
public sealed record ArbiterDispatchEvaluation(string? Trigger, string Outcome);

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
