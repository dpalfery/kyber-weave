using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using static KyberWeave.Tests.Arbiter.ArbiterEvaluatorFixtures;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 589-1: a suppressed secondary rule records its own family's neutral effect. The
/// secondary base used to return <c>keep</c> for every family, so a suppressed <c>shortcut</c> rule
/// was recorded as <c>keep</c>, which is not an effect the shortcut family may record.
/// </summary>
public sealed class ArbiterSuppressedSecondaryEffectTests : IDisposable
{
    private const string PrimaryTrigger = "delegate";
    private const string PrimaryRuleId = "KW-ARB-ASK-901";
    private const string SecondaryRuleId = "KW-ARB-SHORT-901";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-suppressed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The secondary trigger a <c>delegate</c> primary carries in these tests.</summary>
    public static TheoryData<string, string> SecondaryTriggerAndAnswerEffect => new()
    {
        { "shortcut", RuleEffects.Answer },
        { "advice", RuleEffects.Advise },
        { "failure.cluster", RuleEffects.SameFailure },
        { "delegate.model", RuleEffects.Retarget },
    };

    private static IReadOnlyList<string> SecondaryFor(string trigger, string secondary, ArbiterEvent ev) =>
        string.Equals(trigger, PrimaryTrigger, StringComparison.Ordinal) ? [secondary] : [];

    [Fact]
    public async Task SuppressedShortcutSecondary_RecordsShortcutNeutralSpawn_NotKeep()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrimaryRuleId] = "bad",
                [SecondaryRuleId] = "answer",
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(PrimaryRuleId, PrimaryTrigger, "bad", RuleEffects.Escalate),
            AskRule(SecondaryRuleId, "shortcut", "answer", RuleEffects.Answer),
        ]);

        ArbiterEvaluationResult result = await Evaluator(
            _root, provider, (trigger, ev) => SecondaryFor(trigger, "shortcut", ev))
            .EvaluateAsync(DelegateEvent(), config);

        ArbiterSecondaryEvaluation secondary = Assert.Single(result.Dispatches[0].Secondary);
        Assert.Equal(RuleEffects.Spawn, secondary.Outcome);
        ArbiterDecisionRule rule = Records(_root).Single(record => record.Trigger == "shortcut").Rules.Single();
        Assert.Equal(RuleEffects.Spawn, rule.Effect);
        Assert.Equal("suppressed: primary " + RuleEffects.Escalate, rule.Evidence);
    }

    [Theory]
    [MemberData(nameof(SecondaryTriggerAndAnswerEffect))]
    public async Task SuppressedSecondary_RecordsOnlyEffectsItsFamilyAllows(string secondary, string answerEffect)
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrimaryRuleId] = "bad",
                [SecondaryRuleId] = "answer",
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(PrimaryRuleId, PrimaryTrigger, "bad", RuleEffects.Escalate),
            AskRule(SecondaryRuleId, secondary, "answer", answerEffect),
        ]);

        await Evaluator(_root, provider, (trigger, ev) => SecondaryFor(trigger, secondary, ev))
            .EvaluateAsync(DelegateEvent(), config);

        ArbiterDecisionRule rule = Records(_root).Single(record => record.Trigger == secondary).Rules.Single();
        IReadOnlySet<string> allowed = ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamilies.ForTrigger(secondary));
        Assert.True(allowed.Contains(rule.Effect), $"{secondary} recorded '{rule.Effect}', which its family does not allow.");
    }
}
