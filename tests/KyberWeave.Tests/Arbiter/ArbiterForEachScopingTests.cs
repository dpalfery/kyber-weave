using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using static KyberWeave.Tests.Arbiter.ArbiterEvaluatorFixtures;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 589-2: two <c>ForEach</c> rules in one event must each read their own items. The
/// per-item fact was keyed <c>candidate[index]</c> with no rule in the key, so the second rule's item
/// overwrote the first rule's and a question was sent with another rule's value.
/// </summary>
public sealed class ArbiterForEachScopingTests : IDisposable
{
    private const string WithinTask = "within-task";
    private const string RuleA = "KW-ARB-FOREACH-A";
    private const string RuleB = "KW-ARB-FOREACH-B";
    private static readonly string[] FilesA = ["a.cs", "b.cs"];
    private static readonly string[] FilesB = ["x.cs", "y.cs"];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-foreach-scope-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The file value each expansion of <paramref name="ruleId"/> resolves to, in the facts the batch was sent with.</summary>
    private static List<string> ValuesAskedFor(ScriptedProvider provider, ArbiterFactSet facts, string ruleId) =>
        provider.Asked
            .Where(rule => rule.Id.StartsWith(ruleId + "@", StringComparison.Ordinal))
            .Select(rule => facts.TryGet(rule.Ask!.State["file"], out ArbiterFact? fact) ? fact.Value as string : null)
            .OfType<string>()
            .ToList();

    [Fact]
    public async Task TwoForEachRulesInOneEvent_EachQuestionReadsItsOwnItem()
    {
        ScriptedProvider provider = new() { AnswerRule = _ => WithinTask };
        KyberWeaveConfig config = ConfigWith([
            ForEachRule(RuleA, "delegate", "plan.files", WithinTask, RuleEffects.Allow),
            ForEachRule(RuleB, "delegate", "plan.other", WithinTask, RuleEffects.Allow),
        ]);
        PlanFacts plan = new(facts => facts
            .With("plan.files", FilesA, ArbiterFactLabel.Derived)
            .With("plan.other", FilesB, ArbiterFactLabel.Derived));

        await Evaluator(_root, provider, planReader: plan).EvaluateAsync(DelegateEvent(), config);

        ArbiterFactSet facts = Assert.Single(provider.FactsSent);
        Assert.Equal(FilesA, ValuesAskedFor(provider, facts, RuleA));
        Assert.Equal(FilesB, ValuesAskedFor(provider, facts, RuleB));
    }
}
