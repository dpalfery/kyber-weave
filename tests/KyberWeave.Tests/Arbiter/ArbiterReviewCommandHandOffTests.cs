using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The four review questions <c>review seats</c> and <c>review verdict</c> now ask are answered from step 0 on the hook,
/// so a seat or refuter dispatch never sends the same question to the model a second time.
/// </summary>
public sealed class ArbiterReviewCommandHandOffTests
{
    [Theory]
    [InlineData("lens.spawn", "lens.name", "security", "KW-ARB-LENS-001", "asked-by-review-seats")]
    [InlineData("lens.returned", "finding.id", "security/x", "KW-ARB-NITPICK-001", "asked-by-review-verdict")]
    [InlineData("refute.spawn", "finding.id", "security/x", "KW-ARB-CLAIM-001", "asked-by-review-verdict")]
    [InlineData("refute.spawn", "finding.id", "security/x", "KW-ARB-DUP-001", "asked-by-review-verdict")]
    public void TheHookAnswersFromStepZeroAndAsksNoQuestion(string trigger, string fact, string value, string ruleId, string answer)
    {
        ArbiterFactSet facts = new(new Dictionary<string, ArbiterFact>(StringComparer.Ordinal)
        {
            [fact] = new(value, ArbiterFactLabel.Asserted),
        });

        ArbiterOutcome outcome = RuleEngine.EvaluateStep0(ArbiterConfig.ProductDefaults.Rules, facts, trigger);
        ArbiterRuleAnswer ruleAnswer = Assert.Single(outcome.RuleAnswers, entry => entry.RuleId == ruleId);
        ArbiterRule rule = Assert.Single(ArbiterConfig.ProductDefaults.Rules, entry => entry.Id == ruleId);

        Assert.Equal(answer, ruleAnswer.Answer);
        Assert.False(rule.ShouldAsk(ruleAnswer.Answer));
        Assert.Equal(RuleEffects.Allow, rule.Effects[answer]);
    }
}
