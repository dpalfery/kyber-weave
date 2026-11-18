using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using static KyberWeave.Tests.Arbiter.ArbiterEvaluatorFixtures;
using KyberWeave.Tests.Arbiter;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// A <c>ForEach</c> list that is present and empty is a definite "no candidates", not uncertainty.
/// The rule is not sent, records once with its family's neutral effect and the evidence
/// <c>no candidates</c>, is not counted as undecidable, and joins the combination as its neutral effect.
/// Absent or wrong-shape lists stay undecidable (see <see cref="ArbiterForEachUnreadableListTests"/>).
/// </summary>
public sealed class ArbiterForEachEmptyListTests
{
    private const string WithinTask = "within-task";
    private const string RuleId = "KW-ARB-FOREACH-NONE";
    private const string RealRuleId = "KW-ARB-REAL-NONE";
    private const string ListFact = "plan.files";

    private static readonly string[] NoFiles = [];

    private readonly InMemoryArbiterState _arbiter = new();

    [Fact]
    public async Task EmptyList_RecordsOneNeutralNoCandidatesEntryAndIsNotSent()
    {
        ScriptedProvider provider = new() { AnswerRule = _ => WithinTask };
        KyberWeaveConfig config = ConfigWith([
            ForEachRule(RuleId, "delegate", ListFact, WithinTask, RuleEffects.Escalate),
        ]);
        PlanFacts plan = new(facts => facts.With(ListFact, NoFiles, ArbiterFactLabel.Derived));

        ArbiterEvaluationResult result = await _arbiter.Evaluator(
            provider, planReader: plan)
            .EvaluateAsync(DelegateEvent(), config);

        Assert.Empty(provider.Asked);
        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterDecisionRule entry = Assert.Single(_arbiter.Records.Single().Rules);
        Assert.Equal(RuleId, entry.Id);
        Assert.Equal(RuleEffects.Allow, entry.Effect);
        Assert.Equal("no candidates", entry.Evidence);
        Assert.NotEqual(RuleEngine.Undecidable, entry.Answer);
        Assert.Null(entry.Candidate);
    }

    [Fact]
    public async Task EmptyListInASecondaryFamily_CountsAsItsNeutralEffectInTheCombination()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RealRuleId] = "answer",
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(RealRuleId, "shortcut", "answer", RuleEffects.Answer),
            ForEachRule(RuleId, "shortcut", ListFact, WithinTask, RuleEffects.Answer),
        ]);
        PlanFacts plan = new(facts => facts.With(ListFact, NoFiles, ArbiterFactLabel.Derived));

        ArbiterEvaluationResult result = await _arbiter.Evaluator(
            provider,
            (trigger, ev) => string.Equals(trigger, "delegate", StringComparison.Ordinal) ? ["shortcut"] : [],
            plan)
            .EvaluateAsync(DelegateEvent(), config);

        // The real answer alone would give answer. The neutral spawn from the empty list joins the
        // combination, and shortcut answers only when every rule agrees, so the outcome is spawn.
        ArbiterSecondaryEvaluation secondary = Assert.Single(result.Dispatches[0].Secondary);
        Assert.Equal(RuleEffects.Spawn, secondary.Outcome);
    }
}
