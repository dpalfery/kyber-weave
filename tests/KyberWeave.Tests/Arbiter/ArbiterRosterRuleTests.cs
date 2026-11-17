using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-ROSTER-001</c> judges only a caller the Squad catalog names with a <c>delegates-to</c> list. A
/// helper agent spawned in an ordinary session, an unidentified caller, and a Squad agent that delegates to
/// no one are not the Arbiter's business: they are allowed, not escalated.
/// </summary>
public sealed class ArbiterRosterRuleTests
{
    [Theory]
    [InlineData("conductor", "dev-medium", "listed")]
    [InlineData("architect", "research-agent", "listed")]
    [InlineData("architect", "dev-medium", "not-listed")]
    [InlineData("conductor", "general-purpose", "not-listed")]
    [InlineData("general-purpose", "dev-medium", "no-roster")]
    [InlineData("Explore", "research-agent", "no-roster")]
    [InlineData("unidentified", "dev-medium", "no-roster")]
    [InlineData("dev-medium", "research-agent", "no-roster")]
    public void AnswersFromTheCatalogRoster(string caller, string target, string expected)
    {
        ArbiterRule rule = ArbiterConfig.ProductDefaults.Rules.First(
            candidate => string.Equals(candidate.Id, "KW-ARB-ROSTER-001", StringComparison.Ordinal));
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "investigate", target, "Do the thing.", caller, ArbiterCallerSources.Harness, KyberWeaveConfig.ProductDefaults);

        string answer = RuleEngine.Decide(rule, facts);

        Assert.Equal(expected, answer);
        Assert.Equal(expected == "not-listed" ? "escalate" : "allow", rule.Effects[answer]);
    }
}
