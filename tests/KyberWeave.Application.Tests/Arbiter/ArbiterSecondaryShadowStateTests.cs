using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using static KyberWeave.Tests.Arbiter.ArbiterEvaluatorFixtures;
using KyberWeave.Tests.Arbiter;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// Review finding 589-C: a shadow secondary question the provider did not evaluate must keep its shadow
/// state on the record, as a shadow primary question does. It used to lose <c>shadow</c> and would-effect.
/// </summary>
public sealed class ArbiterSecondaryShadowStateTests
{
    private const string PrimaryRuleId = "KW-ARB-ASK-C";
    private const string ShadowGateRuleId = "KW-ARB-GATE-SHADOW-C";

    private readonly InMemoryArbiterState _arbiter = new();

    [Fact]
    public async Task NotEvaluatedShadowSecondary_KeepsShadowAndWouldEffect()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrimaryRuleId] = "within-task",
                [ShadowGateRuleId] = ArbiterStep1BatchResult.NotEvaluated,
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(PrimaryRuleId, "delegate", "within-task", RuleEffects.Allow),
            AskRule(ShadowGateRuleId, "gate.select", "applies", RuleEffects.Applies, shadow: true),
        ]);

        await _arbiter.Evaluator(
            provider, (trigger, ev) =>
                string.Equals(trigger, "delegate", StringComparison.Ordinal) ? ["gate.select"] : [])
            .EvaluateAsync(DelegateEvent(), config);

        ArbiterDecisionRule rule = _arbiter.Records.Single(record => record.Trigger == "gate.select").Rules.Single();
        Assert.True(rule.Shadow);
        Assert.Equal(RuleEffects.Applies, rule.WouldEffect);
        Assert.Equal(RuleEffects.Applies, rule.Effect);
    }
}
