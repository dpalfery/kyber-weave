using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using static KyberWeave.Tests.Arbiter.ArbiterEvaluatorFixtures;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 589-6: a rule the provider did not evaluate records its family's neutral effect.
/// It used to record <c>allow</c> for every family, which is not an effect a model-target, advice or
/// shortcut rule may record, and a gate primary recorded <c>allow</c> too.
/// </summary>
public sealed class ArbiterNotEvaluatedEffectTests : IDisposable
{
    private const string PrimaryRuleId = "KW-ARB-ASK-906";
    private const string SecondaryRuleId = "KW-ARB-NE-906";
    private const string GateRuleId = "KW-ARB-GATE-906";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-not-evaluated-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public static TheoryData<string, string> SecondaryTriggerAndAnswerEffect => new()
    {
        { "shortcut", RuleEffects.Answer },
        { "advice", RuleEffects.Advise },
        { "delegate.model", RuleEffects.Retarget },
    };

    [Theory]
    [MemberData(nameof(SecondaryTriggerAndAnswerEffect))]
    public async Task NotEvaluatedSecondary_RecordsItsFamilyNeutralEffect(string secondary, string answerEffect)
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrimaryRuleId] = "within-task",
                [SecondaryRuleId] = ArbiterStep1BatchResult.NotEvaluated,
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(PrimaryRuleId, "delegate", "within-task", RuleEffects.Allow),
            AskRule(SecondaryRuleId, secondary, "answer", answerEffect),
        ]);

        await Evaluator(_root, provider, (trigger, ev) =>
                string.Equals(trigger, "delegate", StringComparison.Ordinal) ? [secondary] : [])
            .EvaluateAsync(DelegateEvent(), config);

        ArbiterDecisionRule rule = Records(_root).Single(record => record.Trigger == secondary).Rules.Single();
        Assert.Equal(ArbiterTriggerFamilies.NeutralEffect(ArbiterTriggerFamilies.ForTrigger(secondary)), rule.Effect);
    }

    [Fact]
    public async Task NotEvaluatedGatePrimary_RecordsAnEffectTheGateFamilyAllows()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GateRuleId] = ArbiterStep1BatchResult.NotEvaluated,
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(GateRuleId, "gate.select", "applies", RuleEffects.Applies),
        ]);

        await Evaluator(_root, provider).EvaluateAsync(DelegateEvent(target: "gate"), config);

        ArbiterDecisionRule rule = Records(_root).Single(record => record.Trigger == "gate.select").Rules.Single();
        IReadOnlySet<string> allowed = ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.Gate);
        Assert.True(allowed.Contains(rule.Effect), $"gate.select recorded '{rule.Effect}', which the gate family does not allow.");
    }
}
