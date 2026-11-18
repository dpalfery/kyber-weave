using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using static KyberWeave.Tests.Arbiter.ArbiterEvaluatorFixtures;
using KyberWeave.Tests.Arbiter;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// Review finding 589-3: a <c>ForEach</c> list with a repeated item must still give each question its
/// own rule id. The expanded id was the rule id plus the item text, so two equal items produced two
/// questions with one id, and the provider keeps one entry per id, so one answer was lost.
/// </summary>
public sealed class ArbiterForEachDuplicateItemTests
{
    private const string WithinTask = "within-task";
    private const string RuleId = "KW-ARB-FOREACH-DUP";
    private static readonly string[] DuplicateFiles = ["src/a.cs", "src/a.cs"];

    private readonly InMemoryArbiterState _arbiter = new();

    [Fact]
    public async Task DuplicateListItems_EachQuestionHasItsOwnRuleId()
    {
        ScriptedProvider provider = new() { AnswerRule = _ => WithinTask };
        KyberWeaveConfig config = ConfigWith([
            ForEachRule(RuleId, "delegate", "plan.files", WithinTask, RuleEffects.Allow),
        ]);
        PlanFacts plan = new(facts => facts.With("plan.files", DuplicateFiles, ArbiterFactLabel.Derived));

        await _arbiter.Evaluator(
            provider, planReader: plan).EvaluateAsync(DelegateEvent(), config);

        List<string> ids = provider.Asked.Select(rule => rule.Id).ToList();
        Assert.True(
            ids.Distinct(StringComparer.Ordinal).Count() == ids.Count,
            "Each question needs its own id; got: " + string.Join(", ", ids));
        ArbiterDecisionRecord record = Assert.Single(_arbiter.Records);
        Assert.Equal(2, record.Rules.Count);
        Assert.All(record.Rules, entry => Assert.Equal("src/a.cs", entry.Candidate));
    }
}
