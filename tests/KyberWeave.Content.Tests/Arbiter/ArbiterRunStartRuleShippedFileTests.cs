using KyberWeave.Core.Agents.Roster;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-RUN-001</c> answers not-applicable for any dispatch whose target profile is not in its list, so the
/// list must be exactly <see cref="CapabilityProfiles.Writers"/>. The Copilot CLI hook path
/// (<c>ConductorGuardRules.RunStartTargetOf</c>) already reads that code set; a list that differs leaves the two
/// enforcement paths disagreeing for the profile that is missing.
/// </summary>
public sealed class ArbiterRunStartRuleShippedFileTests
{
    private const string RunStartRule = "KW-ARB-RUN-001";

    private const string TargetProfileFact = "delegation.target-profile";

    [Fact]
    public void TheRulesWriterProfileListEqualsTheCapabilityWriterSet()
    {
        // The loader expands a rule that names several triggers into one entry per trigger.
        ArbiterRule[] rules =
        [
            .. ArbiterConfigDefaults.Product.Rules
                .Where(entry => string.Equals(entry.Id, RunStartRule, StringComparison.Ordinal))
        ];
        Assert.NotEmpty(rules);

        string[] expected = [.. CapabilityProfiles.Writers.Order(StringComparer.Ordinal)];
        foreach (ArbiterRule rule in rules)
        {
            RulePredicate profileClause = Assert.Single(
                    rule.Decide!,
                    clause => clause.When.Operator == RuleOperator.Not
                              && clause.When.Children.Any(child =>
                                  child.Operator == RuleOperator.In
                                  && string.Equals(child.Fact, TargetProfileFact, StringComparison.Ordinal)))
                .When.Children.Single(child => child.Operator == RuleOperator.In);

            string[] listed =
            [
                .. Assert.IsAssignableFrom<IEnumerable<object?>>(profileClause.Operand!.LiteralValue)
                    .Select(item => Assert.IsType<string>(item))
            ];

            Assert.Equal(expected, listed.Order(StringComparer.Ordinal));
        }
    }
}
