using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A provider answer whose score sets a floor but reports no confidence cannot clear that floor. The provider client
/// already answers undecidable for that shape; the evaluator applies the floor again, so a provider that drops the
/// confidence field cannot let its answer decide the dispatch. Failure this guards: a dispatch gated on an answer the
/// provider never proved above its floor.
/// </summary>
public sealed class ArbiterNullConfidenceFloorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-nullconf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// The answer <c>within-task</c> maps to allow, and the conductor's undecidable answer escalates, so an answer that
    /// reached the dispatch would show as allow where the floor makes it escalate.
    /// </summary>
    [Fact]
    public async Task AnAnswerWithAFloorAndNoConfidenceIsUndecidable()
    {
        ArbiterEvaluator evaluator = ArbiterEvaluatorFixtures.Evaluator(_root, new FakeArbiterProvider
        {
            AnswerAsked = "within-task",
            ScoreForAll = new ArbiterStep1Score([0.9, 0.1], null, 0.8),
            Model = "stub",
        });
        // The answer would escalate if it stood; floored to undecidable it allows (ADR 0072).
        ArbiterRule rule = ArbiterEvaluatorFixtures.AskRule("KW-ARB-ASK-001", "delegate", "within-task", RuleEffects.Escalate);
        KyberWeaveConfig config = ArbiterEvaluatorFixtures.ConfigWith([rule with { Ask = rule.Ask! with { ConfidenceAtLeast = 0.8 } }]);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(ArbiterEvaluatorFixtures.DelegateEvent(), config);

        Assert.Equal(RuleEffects.Allow, result.Outcome);
    }

    /// <summary>
    /// A score can carry a threshold with no confidence when its rule sets no floor, as a <c>noul</c> probability ceiling
    /// does. Failure this guards: applying every score's threshold as a floor turns those answers undecidable.
    /// </summary>
    [Fact]
    public async Task AnAnswerWithNoConfidenceButNoRuleFloorStands()
    {
        ArbiterEvaluator evaluator = ArbiterEvaluatorFixtures.Evaluator(_root, new FakeArbiterProvider
        {
            AnswerAsked = "within-task",
            ScoreForAll = new ArbiterStep1Score([0.9, 0.1], null, 0.8),
            Model = "stub",
        });
        KyberWeaveConfig config = ArbiterEvaluatorFixtures.ConfigWith([
            ArbiterEvaluatorFixtures.AskRule("KW-ARB-ASK-001", "delegate", "within-task", RuleEffects.Allow),
        ]);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(ArbiterEvaluatorFixtures.DelegateEvent(), config);

        Assert.Equal(RuleEffects.Allow, result.Outcome);
    }
}
