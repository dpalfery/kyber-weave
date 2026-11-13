using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
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

    /// <summary>Answers every step-1 question <c>within-task</c>, with a score whose floor is set and whose confidence is absent.</summary>
    private sealed class FloorWithoutConfidenceProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            List<ArbiterRule> asked = rules.Where(rule => rule.Ask is not null).ToList();
            Dictionary<string, string> answers = asked.ToDictionary(rule => rule.Id, _ => "within-task", StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = asked.ToDictionary(
                rule => rule.Id,
                _ => new ArbiterStep1Score([0.9, 0.1], null, 0.8),
                StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "stub", null, true, scores));
        }
    }

    /// <summary>
    /// The answer <c>within-task</c> maps to allow, and the conductor's undecidable answer escalates, so an answer that
    /// reached the dispatch would show as allow where the floor makes it escalate.
    /// </summary>
    [Fact]
    public async Task AnAnswerWithAFloorAndNoConfidenceIsUndecidable()
    {
        ArbiterEvaluator evaluator = ArbiterEvaluatorFixtures.Evaluator(_root, new FloorWithoutConfidenceProvider());
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
        ArbiterEvaluator evaluator = ArbiterEvaluatorFixtures.Evaluator(_root, new FloorWithoutConfidenceProvider());
        KyberWeaveConfig config = ArbiterEvaluatorFixtures.ConfigWith([
            ArbiterEvaluatorFixtures.AskRule("KW-ARB-ASK-001", "delegate", "within-task", RuleEffects.Allow),
        ]);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(ArbiterEvaluatorFixtures.DelegateEvent(), config);

        Assert.Equal(RuleEffects.Allow, result.Outcome);
    }
}
