using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using static KyberWeave.Tests.Arbiter.ArbiterEvaluatorFixtures;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 589-4: a <c>ForEach</c> rule whose list is missing or has the wrong shape must answer
/// undecidable and stay in the record. (A present, empty list is "no candidates"; see
/// <see cref="ArbiterForEachEmptyListTests"/>.) It used to vanish: no question, no record entry, and
/// no effect in the combination, so a conductor rule quietly allowed. Undecidable is the fail-safe
/// answer, so the family's undecidable effect (escalate for a conductor) is what the record shows.
/// </summary>
public sealed class ArbiterForEachUnreadableListTests : IDisposable
{
    private const string WithinTask = "within-task";
    private const string RuleId = "KW-ARB-FOREACH-GONE";
    private const string ListFact = "plan.files";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-foreach-gone-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public static TheoryData<string> UnreadableLists => ["missing", "wrong-shape"];

    [Theory]
    [MemberData(nameof(UnreadableLists))]
    public async Task UnreadableForEachList_AnswersUndecidableAndStaysInTheRecord(string kind)
    {
        ScriptedProvider provider = new() { AnswerRule = _ => WithinTask };
        KyberWeaveConfig config = ConfigWith([
            ForEachRule(RuleId, "delegate", ListFact, WithinTask, RuleEffects.Allow),
        ]);
        PlanFacts plan = new(facts => kind switch
        {
            "missing" => facts,
            "wrong-shape" => facts.With(ListFact, "src/a.cs", ArbiterFactLabel.Derived),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown list shape."),
        });

        ArbiterEvaluationResult result = await Evaluator(_root, provider, planReader: plan)
            .EvaluateAsync(DelegateEvent(), config);

        Assert.Empty(provider.Asked);
        // Undecidable allows in every family (ADR 0072); the record still shows the undecidable answer.
        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterDecisionRecord record = Assert.Single(Records(_root));
        ArbiterDecisionRule entry = Assert.Single(record.Rules);
        Assert.Equal(RuleId, entry.Id);
        Assert.Equal(RuleEngine.Undecidable, entry.Answer);
        Assert.Equal(RuleEffects.Allow, entry.Effect);
        Assert.Null(entry.Candidate);
    }
}
