using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using static KyberWeave.Tests.Arbiter.ArbiterEvaluatorFixtures;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 589-5: a secondary trigger is delivered when its primary reaches the family's
/// allow-equivalent, which is the family's neutral effect. The check compared the primary against
/// <c>allow</c> only, so a gate primary at <c>applies</c> (its neutral) suppressed every secondary.
/// </summary>
public sealed class ArbiterSecondaryAllowEquivalentTests : IDisposable
{
    private const string GateRuleId = "KW-ARB-GATE-905";
    private const string SecondaryRuleId = "KW-ARB-MODEL-905";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-allow-equiv-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task GatePrimaryAtNeutralApplies_DeliversItsSecondary()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GateRuleId] = "applies",
                [SecondaryRuleId] = "retarget",
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(GateRuleId, "gate.select", "applies", RuleEffects.Applies),
            AskRule(SecondaryRuleId, "delegate.model", "retarget", RuleEffects.Retarget),
        ]);

        ArbiterEvaluationResult result = await Evaluator(
            _root,
            provider,
            (trigger, ev) => string.Equals(trigger, "gate.select", StringComparison.Ordinal) ? ["delegate.model"] : [])
            .EvaluateAsync(DelegateEvent(target: "gate"), config);

        Assert.Equal(RuleEffects.Applies, result.Dispatches[0].Outcome);
        ArbiterSecondaryEvaluation secondary = Assert.Single(result.Dispatches[0].Secondary);
        Assert.Equal(RuleEffects.Retarget, secondary.Outcome);
    }
}
