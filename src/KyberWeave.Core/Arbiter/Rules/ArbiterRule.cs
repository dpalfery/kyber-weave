namespace KyberWeave.Core.Arbiter.Rules;

/// <summary>One step-0 clause: the first matching <c>when</c> answers.</summary>
/// <param name="When">The predicate gating this answer.</param>
/// <param name="Answer">The answer name when the predicate holds.</param>
public sealed record RuleDecideClause(RulePredicate When, string Answer);

/// <summary>
/// A step-1 question in the trigger's batched call. <c>State</c> maps named
/// fields to facts, and to facts only, so a rule declares exactly what
/// leaves the machine.
/// </summary>
/// <param name="Type">One of <c>choice</c>, <c>score</c> or <c>noul</c>.</param>
/// <param name="State">Named fields mapped to dotted fact names.</param>
/// <param name="Criteria">Answer criteria, for <c>choice</c>.</param>
/// <param name="Levels">Answer levels, for <c>score</c>.</param>
/// <param name="Question">The question text, for <c>noul</c>.</param>
/// <param name="InstructionsFrom">A <c>lens:</c> name or repository-relative path.</param>
/// <param name="ConfidenceAtLeast">Floor for <c>choice</c> and <c>score</c>; below it the answer is undecidable.</param>
/// <param name="ProbabilityBelow">Ceiling for <c>noul</c>.</param>
/// <param name="When">Gates the question on step-0 answers (<c>rules.&lt;id&gt;.answer</c>).</param>
/// <param name="TunedFor">Models the threshold was set for.</param>
public sealed record RuleAsk(
    string Type,
    IReadOnlyDictionary<string, string> State,
    IReadOnlyDictionary<string, string>? Criteria = null,
    IReadOnlyList<string>? Levels = null,
    string? Question = null,
    string? InstructionsFrom = null,
    double? ConfidenceAtLeast = null,
    double? ProbabilityBelow = null,
    string? When = null,
    IReadOnlyList<string>? TunedFor = null);

/// <summary>Effect names a rule may use; the trigger family fixes the allowed set.</summary>
public static class RuleEffects
{
    /// <summary>The trigger proceeds.</summary>
    public const string Allow = "allow";

    /// <summary>The trigger escalates with an envelope.</summary>
    public const string Escalate = "escalate";

    /// <summary>A lens spawn is denied; a review note is recorded.</summary>
    public const string Skip = "skip";

    /// <summary>A refutation is denied; the finding is kept.</summary>
    public const string Verify = "verify";

    /// <summary>Post-dispatch additional context on a lens return.</summary>
    public const string Annotate = "annotate";

    /// <summary>A gate applies and runs.</summary>
    public const string Applies = "applies";

    /// <summary>A gate does not apply.</summary>
    public const string NotApplicable = "not-applicable";
}

/// <summary>
/// A harness-neutral rule: step-0 <c>decide</c> predicates with first-match
/// semantics, an optional step-1 <c>ask</c>, and the answer-to-effect map.
/// A rule with both asks only when <c>decide</c> is undecidable.
/// </summary>
/// <param name="Id">Stable rule id, e.g. <c>KW-ARB-SCOPE-001</c>.</param>
/// <param name="Trigger">The trigger this rule belongs to.</param>
/// <param name="Question">The question the rule answers.</param>
/// <param name="Answers">The answer names this rule may produce.</param>
/// <param name="Effects">Each answer mapped to its effect.</param>
/// <param name="Decide">Step-0 clauses in priority order.</param>
/// <param name="Ask">Step-1 question, when the rule asks one.</param>
/// <param name="Enabled">Disabled rules answer undecidable.</param>
public sealed record ArbiterRule(
    string Id,
    string Trigger,
    string Question,
    IReadOnlyList<string> Answers,
    IReadOnlyDictionary<string, string> Effects,
    IReadOnlyList<RuleDecideClause>? Decide = null,
    RuleAsk? Ask = null,
    bool Enabled = true)
{
    /// <summary>Whether the rule asks its step-1 question for this step-0 answer.</summary>
    /// <remarks>
    /// A rule that has both <c>decide</c> and <c>ask</c> asks only when
    /// <c>decide</c> answers undecidable.
    /// </remarks>
    public bool ShouldAsk(string decideAnswer)
    {
        ArgumentNullException.ThrowIfNull(decideAnswer);
        return Ask is not null && string.Equals(decideAnswer, RuleEngine.Undecidable, StringComparison.Ordinal);
    }
}
