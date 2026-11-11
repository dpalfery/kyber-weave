using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using static KyberWeave.Tests.Arbiter.ArbiterEvaluatorFixtures;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 589-7: a shadow rule is log-only, so it must not change the outcome. A shadow rule's
/// neutral effect used to join the combination, where it vetoed a real answer in a family that needs
/// every rule to agree (shortcut, advice, gate). Shadow entries now stay out of the combination.
/// </summary>
public sealed class ArbiterShadowVetoTests : IDisposable
{
    private const string Answer = "answer";
    private const string Applies = "applies";
    private const string Skip = "skip";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-shadow-veto-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public static TheoryData<string, string> SecondaryTriggerAndRealEffect => new()
    {
        { "shortcut", RuleEffects.Answer },
        { "advice", RuleEffects.Advise },
    };

    [Theory]
    [MemberData(nameof(SecondaryTriggerAndRealEffect))]
    public async Task ShadowRuleDoesNotVetoARealAnswerInASecondaryFamily(string secondary, string realEffect)
    {
        ScriptedProvider provider = new() { AnswerRule = _ => Answer };
        KyberWeaveConfig config = ConfigWith([
            AskRule("KW-ARB-REAL-907", secondary, Answer, realEffect),
            AskRule("KW-ARB-SHADOW-907", secondary, Answer, realEffect, shadow: true),
        ]);

        ArbiterEvaluationResult result = await Evaluator(
            _root,
            provider,
            (trigger, ev) => string.Equals(trigger, "delegate", StringComparison.Ordinal) ? [secondary] : [])
            .EvaluateAsync(DelegateEvent(), config);

        ArbiterSecondaryEvaluation entry = Assert.Single(result.Dispatches[0].Secondary);
        Assert.Equal(realEffect, entry.Outcome);
    }

    [Fact]
    public async Task ShadowRuleDoesNotForceAGateWhoseRealAnswerSkips()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["KW-ARB-GATE-REAL-907"] = Skip,
                ["KW-ARB-GATE-SHADOW-907"] = Applies,
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule("KW-ARB-GATE-REAL-907", "gate.select", Skip, RuleEffects.NotApplicable),
            AskRule("KW-ARB-GATE-SHADOW-907", "gate.select", Applies, RuleEffects.Applies, shadow: true),
        ]);

        ArbiterEvaluationResult result = await Evaluator(_root, provider)
            .EvaluateAsync(DelegateEvent(target: "gate"), config);

        Assert.Equal(RuleEffects.NotApplicable, result.Dispatches[0].Outcome);
    }

    [Fact]
    public async Task ShadowOnlyGate_StaysAtItsNeutralEffect()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["KW-ARB-GATE-ONLY-907"] = Applies,
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule("KW-ARB-GATE-ONLY-907", "gate.select", Applies, RuleEffects.Applies, shadow: true),
        ]);

        ArbiterEvaluationResult result = await Evaluator(_root, provider)
            .EvaluateAsync(DelegateEvent(target: "gate"), config);

        Assert.Equal(ArbiterTriggerFamilies.NeutralEffect(ArbiterTriggerFamily.Gate), result.Dispatches[0].Outcome);
    }

    [Fact]
    public async Task ShadowStep0ShortcutRule_DoesNotVetoARealStep0Answer()
    {
        ScriptedProvider provider = new();
        ArbiterRule real = new(
            "KW-ARB-SHORT-REAL-907",
            "shortcut",
            "Step-0 question.",
            [Answer],
            new Dictionary<string, string>(StringComparer.Ordinal) { [Answer] = RuleEffects.Answer },
            [new RuleDecideClause(RulePredicate.Exists("caller", true), Answer)]);
        ArbiterRule shadow = real with { Id = "KW-ARB-SHORT-SHADOW-907", Shadow = true };
        KyberWeaveConfig config = ConfigWith([real, shadow]);

        ArbiterEvaluationResult result = await Evaluator(
            _root,
            provider,
            (trigger, ev) => string.Equals(trigger, "delegate", StringComparison.Ordinal) ? ["shortcut"] : [])
            .EvaluateAsync(DelegateEvent(), config);

        ArbiterSecondaryEvaluation entry = Assert.Single(result.Dispatches[0].Secondary);
        Assert.Equal(RuleEffects.Answer, entry.Outcome);
    }
}
