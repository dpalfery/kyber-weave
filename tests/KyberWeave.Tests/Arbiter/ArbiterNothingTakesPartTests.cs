using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using static KyberWeave.Tests.Arbiter.ArbiterEvaluatorFixtures;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 589-D: when no rule takes part in the combination, the outcome is the family's neutral
/// effect, the same effect the record shows for a not-evaluated or shadow entry. For a gate that is
/// <c>applies</c> (run the gate), not <c>not-applicable</c>, which the combine of an empty set gave before.
/// </summary>
public sealed class ArbiterNothingTakesPartTests : IDisposable
{
    private const string GateRuleId = "KW-ARB-GATE-NE-D";
    private const string ConductorRuleId = "KW-ARB-ASK-D";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-nothing-takes-part-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task GateWhoseOnlyQuestionIsNotEvaluated_OutcomeIsNeutralApplies()
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

        ArbiterEvaluationResult result = await Evaluator(_root, provider)
            .EvaluateAsync(DelegateEvent(target: "gate"), config);

        Assert.Equal(RuleEffects.Applies, result.Outcome);
        Assert.Equal(RuleEffects.Applies, result.Dispatches[0].Outcome);
    }

    [Fact]
    public async Task GateWithNoEnabledRule_OutcomeIsNeutralApplies()
    {
        KyberWeaveConfig config = ConfigWith([
            AskRule(GateRuleId, "gate.select", "applies", RuleEffects.Applies) with { Enabled = false },
        ]);

        ArbiterEvaluationResult result = await Evaluator(_root, new ScriptedProvider())
            .EvaluateAsync(DelegateEvent(target: "gate"), config);

        Assert.Equal(RuleEffects.Applies, result.Outcome);
    }

    [Fact]
    public async Task ConductorWhoseOnlyQuestionIsNotEvaluated_OutcomeIsNeutralAllow()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ConductorRuleId] = ArbiterStep1BatchResult.NotEvaluated,
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(ConductorRuleId, "delegate", "within-task", RuleEffects.Allow),
        ]);

        ArbiterEvaluationResult result = await Evaluator(_root, provider).EvaluateAsync(DelegateEvent(), config);

        Assert.Equal(RuleEffects.Allow, result.Outcome);
    }
}
