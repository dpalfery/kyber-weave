namespace KyberWeave.Core.Arbiter.Rules;

/// <summary>
/// The questions a rule asks. They live here rather than on <see cref="ArbiterRule"/> so the record stays data.
/// </summary>
public static class ArbiterRuleExtensions
{
    /// <summary>Whether the rule asks its step-1 question for this step-0 answer.</summary>
    /// <remarks>
    /// A rule that has both <c>decide</c> and <c>ask</c> asks only when
    /// <c>decide</c> answers undecidable.
    /// </remarks>
    /// <param name="rule">The rule the answer came from.</param>
    /// <param name="decideAnswer">The step-0 answer.</param>
    /// <returns>True when the rule carries a question and the step-0 answer left it undecidable.</returns>
    public static bool ShouldAsk(this ArbiterRule rule, string decideAnswer)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(decideAnswer);
        return rule.Ask is not null && string.Equals(decideAnswer, "undecidable", StringComparison.Ordinal);
    }
}
