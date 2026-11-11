using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.9 (same-failure): a rework dispatch to the same worker that carries the prior failing
/// output. <c>KW-ARB-FAILURE-001</c> sits on the new secondary trigger <c>delegate.failure</c>, which a
/// <c>delegate</c> dispatch to an implementation worker carries (behind its primary) when its prompt body opens with
/// <c>FAILURE-CLUSTER: &lt;key&gt;</c>. The rule asks the provider whether the current <c>FAILURE:</c> text is the
/// same failure as the one stored from the previous attempt. When the answer is <c>same</c> at or above the
/// 0.85 floor, the dispatch gets an advisory line and is never denied.
/// RED: <c>ArbiterFailureNotices</c>, <c>ArbiterRunStore.ReadFailures</c> and the shipped rule do not exist yet,
/// and <c>TriggerClassifier</c> does not yet recognise the <c>FAILURE-CLUSTER</c> marker.
/// </summary>
/// <remarks>
/// <para>
/// Choice of names (GREEN must match): the marker is <c>FAILURE-CLUSTER:</c> as the first body line; the
/// current text is the body after <c>FAILURE:</c>; the store is read through
/// <c>ArbiterRunStore.ReadFailures(planFile, planDigest, session)</c>, mirroring the write
/// <c>AppendFailureAsync</c>. The advisory is <c>ARBITER_SAME_FAILURE: yes prior=&lt;id&gt; decision=&lt;id&gt;</c>,
/// built by <c>ArbiterFailureNotices.SameFailureLine(priorId, decisionId)</c>.
/// </para>
/// <para>
/// Fail-safe: no prior failure, a different or unsure answer, a provider failure, and shadow all leave the
/// dispatch unchanged. The first failure of a cluster is only recorded, never asked about.
/// </para>
/// </remarks>
public sealed class ArbiterSameFailureRuleTests : IDisposable
{
    private const string FailureRule = "KW-ARB-FAILURE-001";
    private const string FailureTrigger = "delegate.failure";
    private const string Same = "same";
    private const string Different = "different";
    private const string Session = "session-1";
    private const string ClusterKey = "cluster-a";
    private const string FirstFailure = "expected 3 got 2 at Widget.cs:12";
    private const string SecondFailure = "expected 3 got 2 at Widget.cs:12 (rerun)";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-failure-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    private static DateTimeOffset Now => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Answers every step-1 question with one answer and confidence, and counts the calls. A non-answering
    /// provider models a failure.
    /// </summary>
    private sealed class ScriptedProvider(string answer, double confidence, bool requestSent = true) : IArbiterProvider
    {
        public int CallCount { get; private set; }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (!requestSent)
            {
                return Task.FromResult(new ArbiterStep1BatchResult(
                    new Dictionary<string, string>(StringComparer.Ordinal), null, null, RequestSent: false));
            }

            Dictionary<string, string> answers = rules
                .Where(rule => rule.Ask is not null)
                .ToDictionary(rule => rule.Id, _ => answer, StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = rules
                .Where(rule => rule.Ask is not null)
                .ToDictionary(rule => rule.Id, _ => new ArbiterStep1Score(null, confidence, 0.85), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.13.0", null, RequestSent: true, scores));
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
        Assert.Single(
            ArbiterConfig.ProductDefaults.Rules,
            rule => string.Equals(rule.Id, id, StringComparison.Ordinal));

    /// <summary>The shipped rule as an enforced rule: same clauses, shadow off.</summary>
    private static ArbiterRule Enforced(string id) => Shipped(id) with { Shadow = false };

    private static KyberWeaveConfig ConfigWith(IReadOnlyList<ArbiterRule> rules) =>
        new()
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = rules },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };

    private static ArbiterEvaluator Evaluator(string arbiterDirectory, IArbiterProvider provider) =>
        new(
            provider,
            _ => null,
            new InFlightLedger(arbiterDirectory),
            new DecisionLog(arbiterDirectory),
            new PassThroughGitFacts(),
            new PassThroughPlanReader(),
            new FixedClock(Now),
            SecondaryTriggers.For);

    /// <summary>A conductor's rework dispatch to a worker, carrying the cluster key and the prior failing output.</summary>
    private static ArbiterEvent Rework(string failureText, string clusterKey = ClusterKey) => new()
    {
        Harness = "claude",
        Phase = "pre",
        Target = "csharp-dev",
        Prompt = "KYBER-ARBITER: true\n\nFAILURE-CLUSTER: " + clusterKey + "\n\nFAILURE:\n" + failureText + "\n\nRework the change.",
        HarnessCaller = "conductor",
        IsDispatch = true,
        Session = Session,
    };

    // ---- Classification (acceptance: the marker derives the new primary trigger) ----

    /// <summary>
    /// A rework dispatch (first body line <c>FAILURE-CLUSTER:</c>) classifies as the normal <c>delegate</c> primary,
    /// so the primary delegate rules still run on it; <c>delegate.failure</c> is a secondary that derives in addition.
    /// Making it the primary would silently drop every primary delegate rule on rework (the regression this guards).
    /// </summary>
    [Fact]
    public void AFailureClusterMarkerClassifiesAsDelegateWithDelegateFailureAsSecondary()
    {
        ArbiterEvent rework = Rework(FirstFailure);

        TriggerClassification classification = TriggerClassifier.ClassifySingle(
            rework, "csharp-dev", rework.Prompt);

        Assert.Equal("delegate", classification.Trigger);
        Assert.Contains(FailureTrigger, SecondaryTriggers.For("delegate", rework));
    }

    /// <summary>A delegate without the marker is still a plain <c>delegate</c>, so the failure rule never sees it.</summary>
    [Fact]
    public void ADelegateWithoutTheMarkerStaysADelegate()
    {
        ArbiterEvent plain = Rework(FirstFailure) with { Prompt = "KYBER-ARBITER: true\n\nImplement the widget." };

        TriggerClassification classification = TriggerClassifier.ClassifySingle(plain, "csharp-dev", plain.Prompt);

        Assert.Equal("delegate", classification.Trigger);
    }

    /// <summary>The marker counts only as the first body line: a later line with the same text does not derive the trigger.</summary>
    [Fact]
    public void TheFailureClusterMarkerCountsOnlyAsTheFirstBodyLine()
    {
        const string prompt = "KYBER-ARBITER: true\n\nImplement the widget.\nFAILURE-CLUSTER: cluster-a";
        ArbiterEvent ev = Rework(FirstFailure) with { Prompt = prompt };

        TriggerClassification classification = TriggerClassifier.ClassifySingle(ev, "csharp-dev", prompt);

        Assert.Equal("delegate", classification.Trigger);
    }

    // ---- Shipped shape (acceptance: trigger, shadow, floor, answers and effects) ----

    /// <summary>The rule sits on <c>delegate.failure</c>, is a step-1 choice with a 0.85 floor, and ships in shadow.</summary>
    [Fact]
    public void TheSameFailureRuleShipsInShadowOnTheDelegateFailureTrigger()
    {
        ArbiterRule rule = Shipped(FailureRule);

        Assert.Equal(FailureTrigger, rule.Trigger);
        Assert.True(rule.Shadow);
        Assert.NotNull(rule.Ask);
        Assert.Equal("choice", rule.Ask.Type);
        Assert.Equal(0.85, rule.Ask.ConfidenceAtLeast);
    }

    /// <summary>A same answer is the advisory effect; a different answer lets the dispatch proceed unchanged.</summary>
    [Fact]
    public void SameIsTheAdvisoryEffectAndDifferentAllows()
    {
        ArbiterRule rule = Shipped(FailureRule);

        Assert.Equal(RuleEffects.SameFailure, rule.Effects[Same]);
        Assert.Equal(RuleEffects.Allow, rule.Effects[Different]);
    }

    // ---- Advisory line (acceptance: exact text, annotate-style) ----

    /// <summary>The advisory names the prior failure and the decision that carries it, in one line.</summary>
    [Fact]
    public void TheSameFailureAdvisoryIsOneExactLine()
    {
        Assert.Equal(
            "ARBITER_SAME_FAILURE: yes prior=prior-1 decision=decision-2",
            ArbiterFailureNotices.SameFailureLine("prior-1", "decision-2"));
    }

    // ---- Store (acceptance: the first failure is recorded, never advised) ----

    /// <summary>The first failure of a cluster is recorded in the failure store and is not asked about or advised.</summary>
    [Fact]
    public async Task TheFirstFailureIsRecordedAndNotAdvised()
    {
        ScriptedProvider provider = new(Same, 0.95);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            Rework(FirstFailure), ConfigWith([Enforced(FailureRule)]));

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(RuleEffects.Allow, result.Outcome);
        string stored = Assert.Single(new ArbiterRunStore(ArbiterDirectory, null, () => Now).ReadFailures(null, null, Session));
        Assert.Contains(FirstFailure, stored, StringComparison.Ordinal);
    }

    // ---- Second failure through the evaluator (acceptance: same at floor advises; the rest does not) ----

    /// <summary>A second failure of the same cluster that the provider calls the same, at or above the floor, is advised.</summary>
    [Fact]
    public async Task ASameClusterSecondFailureAtTheFloorIsAdvised()
    {
        ArbiterEvaluator first = Evaluator(ArbiterDirectory, new ScriptedProvider(Different, 0.95));
        await first.EvaluateAsync(Rework(FirstFailure), ConfigWith([Enforced(FailureRule)]));

        ArbiterEvaluator second = Evaluator(ArbiterDirectory, new ScriptedProvider(Same, 0.9));
        ArbiterEvaluationResult result = await second.EvaluateAsync(
            Rework(SecondFailure), ConfigWith([Enforced(FailureRule)]));

        Assert.Equal(RuleEffects.SameFailure, result.Outcome);
        ArbiterSecondaryEvaluation secondary = Assert.Single(Assert.Single(result.Dispatches).Secondary);
        Assert.Equal(FailureTrigger, secondary.Trigger);
        Assert.Equal(RuleEffects.SameFailure, secondary.Outcome);
    }

    /// <summary>The advisory never denies: the same-failure outcome is not a block on the rework dispatch.</summary>
    [Fact]
    public async Task TheSameFailureAdvisoryNeverDeniesTheDispatch()
    {
        ArbiterEvaluator first = Evaluator(ArbiterDirectory, new ScriptedProvider(Different, 0.95));
        await first.EvaluateAsync(Rework(FirstFailure), ConfigWith([Enforced(FailureRule)]));

        ArbiterEvaluator second = Evaluator(ArbiterDirectory, new ScriptedProvider(Same, 0.95));
        ArbiterEvaluationResult result = await second.EvaluateAsync(
            Rework(SecondFailure), ConfigWith([Enforced(FailureRule)]));

        Assert.NotEqual(RuleEffects.Escalate, result.Outcome);
        Assert.NotEqual(RuleEffects.Verify, result.Outcome);
        Assert.False(result.IsError);
    }

    /// <summary>A same answer below the 0.85 floor is undecidable: the dispatch is unchanged.</summary>
    [Fact]
    public async Task ASameAnswerBelowTheFloorLeavesTheDispatchUnchanged()
    {
        ArbiterEvaluator first = Evaluator(ArbiterDirectory, new ScriptedProvider(Different, 0.95));
        await first.EvaluateAsync(Rework(FirstFailure), ConfigWith([Enforced(FailureRule)]));

        ArbiterEvaluator second = Evaluator(ArbiterDirectory, new ScriptedProvider(Same, 0.8));
        ArbiterEvaluationResult result = await second.EvaluateAsync(
            Rework(SecondFailure), ConfigWith([Enforced(FailureRule)]));

        Assert.Equal(RuleEffects.Allow, result.Outcome);
    }

    /// <summary>A different answer is not advised: the dispatch proceeds unchanged.</summary>
    [Fact]
    public async Task ADifferentFailureIsNotAdvised()
    {
        ArbiterEvaluator first = Evaluator(ArbiterDirectory, new ScriptedProvider(Different, 0.95));
        await first.EvaluateAsync(Rework(FirstFailure), ConfigWith([Enforced(FailureRule)]));

        ArbiterEvaluator second = Evaluator(ArbiterDirectory, new ScriptedProvider(Different, 0.95));
        ArbiterEvaluationResult result = await second.EvaluateAsync(
            Rework(SecondFailure), ConfigWith([Enforced(FailureRule)]));

        Assert.Equal(RuleEffects.Allow, result.Outcome);
    }

    /// <summary>A provider that did not answer leaves the dispatch unchanged, even for a second failure.</summary>
    [Fact]
    public async Task AProviderFailureLeavesTheReworkDispatchUnchanged()
    {
        ArbiterEvaluator first = Evaluator(ArbiterDirectory, new ScriptedProvider(Different, 0.95));
        await first.EvaluateAsync(Rework(FirstFailure), ConfigWith([Enforced(FailureRule)]));

        ArbiterEvaluator second = Evaluator(ArbiterDirectory, new ScriptedProvider(Same, 0.95, requestSent: false));
        ArbiterEvaluationResult result = await second.EvaluateAsync(
            Rework(SecondFailure), ConfigWith([Enforced(FailureRule)]));

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        Assert.False(result.IsError);
    }

    /// <summary>The shipped shadow rule logs the same-failure it would have advised, and the dispatch stays allowed.</summary>
    [Fact]
    public async Task TheShadowSameFailureOnlyLogsWhatItWouldHaveAdvised()
    {
        ArbiterEvaluator first = Evaluator(ArbiterDirectory, new ScriptedProvider(Different, 0.95));
        await first.EvaluateAsync(Rework(FirstFailure), ConfigWith([Shipped(FailureRule)]));

        ArbiterEvaluator second = Evaluator(ArbiterDirectory, new ScriptedProvider(Same, 0.95));
        ArbiterEvaluationResult result = await second.EvaluateAsync(
            Rework(SecondFailure), ConfigWith([Shipped(FailureRule)]));

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterDecisionRecord record = Assert.Single(
            new DecisionLog(ArbiterDirectory).ReadAll(),
            decision => decision.Trigger == FailureTrigger && decision.LedgerId == result.LedgerId);
        ArbiterDecisionRule rule = Assert.Single(record.Rules, r => r.Id == FailureRule);
        Assert.Equal(RuleEffects.SameFailure, rule.WouldEffect);
    }
}
