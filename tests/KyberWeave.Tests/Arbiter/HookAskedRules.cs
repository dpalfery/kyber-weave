using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The review-command rules as the hook used to ask them. The shipped <c>KW-ARB-LENS-001</c>, <c>-NITPICK-001</c>,
/// <c>-DUP-001</c> and <c>-CLAIM-001</c> answer from step 0 on the hook, because <c>review seats</c> and
/// <c>review verdict</c> ask them (ADR 0079, decision 10). A host rule can still ask on the hook, so the hook-side
/// mechanism stays tested on this form: the step-0 hand-off clause removed, and the shadow state the rules shipped with.
/// </summary>
internal static class HookAskedRules
{
    private static readonly string[] ShippedInShadow = ["KW-ARB-NITPICK-001", "KW-ARB-DUP-001"];

    public static ArbiterRule AsHookAsked(ArbiterRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        RuleDecideClause[]? decide = rule.Decide is null
            ? null
            : [.. rule.Decide.Where(clause => !clause.Answer.StartsWith("asked-by-review-", StringComparison.Ordinal))];
        return rule with
        {
            Decide = decide is { Length: 0 } ? null : decide,
            Shadow = rule.Shadow || ShippedInShadow.Contains(rule.Id, StringComparer.Ordinal),
        };
    }
}
