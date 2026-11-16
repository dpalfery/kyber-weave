namespace KyberWeave.Core.Arbiter;

/// <summary>One rule's step-0 answer with its mapped effect.</summary>
/// <param name="RuleId">The rule that answered.</param>
/// <param name="Answer">The answer name, or <c>undecidable</c>.</param>
/// <param name="Step">Always 0 for step-0 answers.</param>
/// <param name="Effect">The effect the answer contributes: its mapped effect, or the family's neutral effect when the rule is shadow.</param>
/// <param name="Shadow">Whether the rule is shadow, so its effect is log-only.</param>
/// <param name="WouldEffect">For a shadow rule, the mapped effect it would have contributed; otherwise null.</param>
public sealed record ArbiterRuleAnswer(
    string RuleId,
    string Answer,
    int Step,
    string Effect,
    bool Shadow = false,
    string? WouldEffect = null);

/// <summary>Step-0's combined verdict for one trigger firing.</summary>
/// <param name="Trigger">The trigger that fired.</param>
/// <param name="Family">The trigger's family.</param>
/// <param name="RuleAnswers">Per-rule answers in evaluation order.</param>
/// <param name="CombinedEffect">The family's combination of the per-rule effects.</param>
/// <param name="Step0ShortCircuitsStep1">
/// Whether step 0 already produced the family's strongest effect
/// (<c>escalate</c>, <c>skip</c> or <c>verify</c>), so the caller skips step 1.
/// </param>
public sealed record ArbiterOutcome(
    string Trigger,
    ArbiterTriggerFamily Family,
    IReadOnlyList<ArbiterRuleAnswer> RuleAnswers,
    string CombinedEffect,
    bool Step0ShortCircuitsStep1);
