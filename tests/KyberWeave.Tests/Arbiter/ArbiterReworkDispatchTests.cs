using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Regression contract for a rework dispatch (a delegate whose first body line is <c>FAILURE-CLUSTER: &lt;key&gt;</c>).
/// Defect: the classifier made <c>delegate.failure</c> the PRIMARY trigger, so the primary <c>delegate</c> rules and
/// the <c>delegate.model</c> model-targeting secondary stopped running on rework. Required: the rework dispatch
/// classifies as the normal <c>delegate</c> primary, and <c>delegate.failure</c> is a SECONDARY that derives in
/// addition, evaluated only when the primary allows, sharing the one step-1 batch and budget.
/// RED: until the classifier and secondary derivation are corrected, the primary is <c>delegate.failure</c> and the
/// retarget decision and the primary rule record are absent.
/// </summary>
public sealed class ArbiterReworkDispatchTests : IDisposable
{
    private const string PlanReadRule = "KW-ARB-PLANREAD-001";
    private const string ModelRule = "KW-ARB-DIFFICULTY-001";
    private const string FailureRule = "KW-ARB-FAILURE-001";
    private const string DelegateTrigger = "delegate";
    private const string ModelTrigger = "delegate.model";
    private const string FailureTrigger = "delegate.failure";
    private const string Same = "same";
    private const string Different = "different";
    private const string Hard = "hard";
    private const string NoPlan = "no-plan";
    private const string Session = "session-1";
    private const string ClusterKey = "cluster-a";
    private const string FirstFailure = "expected 3 got 2 at Widget.cs:12";
    private const string SecondFailure = "expected 3 got 2 at Widget.cs:12 (rerun)";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-rework-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    private static DateTimeOffset Now => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Answers each named rule with its own answer at 0.95 confidence, and records the rule ids of every call.</summary>
    private sealed class AnswerByRuleProvider(IReadOnlyDictionary<string, string> answers) : IArbiterProvider
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(rules.Where(rule => rule.Ask is not null).Select(rule => rule.Id).ToArray());
            Dictionary<string, string> chosen = rules
                .Where(rule => rule.Ask is not null && answers.ContainsKey(rule.Id))
                .ToDictionary(rule => rule.Id, rule => answers[rule.Id], StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = chosen.Keys
                .ToDictionary(id => id, _ => new ArbiterStep1Score(null, 0.95, 0.85), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(chosen, "jev-1.13.0", null, RequestSent: true, scores));
        }
    }

    private sealed class PassThroughGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    private sealed class PassThroughPlanReader : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) => facts;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ArbiterRule Shipped(string id) =>
        Assert.Single(ArbiterConfig.ProductDefaults.Rules, rule => string.Equals(rule.Id, id, StringComparison.Ordinal));

    /// <summary>The shipped rule as an enforced rule: same clauses, shadow off.</summary>
    private static ArbiterRule Enforced(string id) => Shipped(id) with { Shadow = false };

    private static KyberWeaveConfig ConfigWith(IReadOnlyList<ArbiterRule> rules) =>
        new()
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = rules },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };

    /// <summary>
    /// The rules every rework test enforces: the primary delegate rule, the model-targeting rule and the failure rule.
    /// Model targeting is set to <c>enforce</c> because the shipped mode is <c>shadow</c>, which forces
    /// <c>KW-ARB-DIFFICULTY-001</c> to keep the worker's model rather than retarget it.
    /// </summary>
    private static KyberWeaveConfig ReworkConfig() =>
        new()
        {
            Arbiter = new ArbiterConfig
            {
                Enabled = true,
                Rules = [Enforced(PlanReadRule), Enforced(ModelRule), Enforced(FailureRule)],
                ModelTargeting = ArbiterModelTargetingConfig.Defaults with { Mode = "enforce" },
            },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };

    private ArbiterEvaluator Evaluator(IArbiterProvider provider) =>
        new(
            provider,
            _ => null,
            new InFlightLedger(ArbiterDirectory),
            new DecisionLog(ArbiterDirectory),
            new PassThroughGitFacts(),
            new PassThroughPlanReader(),
            new FixedClock(Now),
            SecondaryTriggers.For);

    /// <summary>A conductor's rework dispatch to an implementation worker, carrying the cluster key and the failing output.</summary>
    private static ArbiterEvent Rework(string failureText) => new()
    {
        Harness = "claude",
        Phase = "pre",
        Target = "csharp-dev",
        Prompt = "KYBER-ARBITER: true\n\nFAILURE-CLUSTER: " + ClusterKey + "\n\nFAILURE:\n" + failureText + "\n\nRework the change.",
        HarnessCaller = "conductor",
        IsDispatch = true,
        Session = Session,
    };

    /// <summary>
    /// A rework dispatch to an implementation target gets the model-targeting retarget decision (the
    /// <c>delegate.model</c> secondary) AND the failure question (the <c>delegate.failure</c> secondary) in the same
    /// evaluation, so the retarget is not silently dropped on the rework path.
    /// </summary>
    [Fact]
    public async Task AReworkDispatchGetsTheRetargetDecisionAndTheFailureQuestion()
    {
        await Evaluator(new AnswerByRuleProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FailureRule] = Different,
            [ModelRule] = Hard,
        })).EvaluateAsync(Rework(FirstFailure), ReworkConfig());

        AnswerByRuleProvider provider = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FailureRule] = Same,
            [ModelRule] = Hard,
        });
        ArbiterEvaluationResult result = await Evaluator(provider).EvaluateAsync(Rework(SecondFailure), ReworkConfig());

        IReadOnlyList<string> asked = Assert.Single(provider.Calls);
        Assert.Contains(ModelRule, asked);
        Assert.Contains(FailureRule, asked);

        ArbiterDispatchEvaluation dispatch = Assert.Single(result.Dispatches);
        ArbiterSecondaryEvaluation retarget = Assert.Single(dispatch.Secondary, secondary => secondary.Trigger == ModelTrigger);
        Assert.Equal(RuleEffects.Retarget, retarget.Outcome);
        Assert.Single(dispatch.Secondary, secondary => secondary.Trigger == FailureTrigger);
    }

    /// <summary>
    /// The primary <c>delegate</c> rules still run on a rework dispatch: the plan-read rule (a primary delegate rule)
    /// writes its decision record under the <c>delegate</c> trigger with its answer, not only the failure record.
    /// </summary>
    [Fact]
    public async Task AnExistingPrimaryDelegateRuleStillFiresOnAReworkDispatch()
    {
        ArbiterEvaluationResult result = await Evaluator(new AnswerByRuleProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ModelRule] = Hard,
        })).EvaluateAsync(Rework(FirstFailure), ReworkConfig());

        ArbiterDecisionRecord primary = Assert.Single(
            new DecisionLog(ArbiterDirectory).ReadAll(),
            record => record.Trigger == DelegateTrigger && record.LedgerId == result.LedgerId);
        ArbiterDecisionRule planRead = Assert.Single(primary.Rules, rule => rule.Id == PlanReadRule);
        Assert.Equal(NoPlan, planRead.Answer);
    }
}
