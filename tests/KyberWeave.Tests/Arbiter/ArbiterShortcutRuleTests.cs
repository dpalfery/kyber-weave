using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.7 (shortcuts): three step-1 rules that answer a dispatch without spawning its
/// worker when the Arbiter is confident the answer is trivial, an intake is a plan or spec request, or
/// a change needs no documentation. <c>KW-ARB-TRIAGE-001</c> sits on <c>triage.shortcut</c> (derived from
/// a <c>bug-crusher</c> investigate), <c>KW-ARB-INTAKE-001</c> on <c>intake.shortcut</c> (derived only from
/// <c>delegate.planner</c> with an <c>INTAKE:</c> marker), and <c>KW-ARB-DOCIMPACT-001</c> on
/// <c>docs-impact.shortcut</c> (derived from a docs-dev dispatch with a <c>DOCS-IMPACT: true</c> first line).
/// RED: <c>SubstituteDigests</c> does not exist, the three rules are not shipped, <c>SecondaryTriggers.For</c>
/// derives none of the shortcut triggers, and <c>LedgerFacts.NotDispatchedLedgerIds</c> does not exist yet.
/// </summary>
/// <remarks>
/// <para>
/// Fail-safe: an unsure, low-confidence or failed provider answer leaves the dispatch exactly as it was
/// (the expensive route proceeds). A shadow rule only logs what it would have done. A harness whose hook is
/// not confirmed runs no rule at all, and that is covered by the hook tests rather than here.
/// </para>
/// <para>
/// Assumed names (GREEN must match): <c>SubstituteDigests.Triage(decisionId)</c>,
/// <c>SubstituteDigests.Intake(input, recommendation, decisionId)</c>, <c>SubstituteDigests.DocsImpact(decisionId)</c>
/// in <c>KyberWeave.Core.Arbiter</c>; <c>LedgerFacts.NotDispatchedLedgerIds(DecisionLog)</c> returns the ledger
/// ids whose decision was refused or answered, so the audit does not count a withheld worker as a spawn.
/// </para>
/// </remarks>
public sealed class ArbiterShortcutRuleTests : IDisposable
{
    private const string TriageRule = "KW-ARB-TRIAGE-001";
    private const string IntakeRule = "KW-ARB-INTAKE-001";
    private const string DocsImpactRule = "KW-ARB-DOCIMPACT-001";
    private const string TriageTrigger = "triage.shortcut";
    private const string IntakeTrigger = "intake.shortcut";
    private const string DocsImpactTrigger = "docs-impact.shortcut";
    private const string Trivial = "trivial";
    private const string NeedsArchitect = "needs-architect";
    private const string Plan = "plan";
    private const string Spec = "spec";
    private const string NoDocChange = "no-doc-change";
    private const string DocChange = "doc-change";
    private const string Touches = "touches";
    private const string GovernedPathsFact = "docs.governed-paths-changed";
    private const string Answered = RuleEffects.Answer;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-shortcut-" + Guid.NewGuid().ToString("N"));

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
    /// Answers every step-1 question with one answer and one confidence, and counts the calls so a test can
    /// prove a question was never asked. <paramref name="requestSent"/> <c>false</c> models a provider failure.
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
                .ToDictionary(rule => rule.Id, _ => new ArbiterStep1Score(null, confidence, 0.9), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.13.0", null, RequestSent: true, scores));
        }
    }

    /// <summary>Adds one step-0 fact to every evaluation, the way the git reader does for the docs rule.</summary>
    private sealed class FactGitFacts(string name, bool value) : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) =>
            facts.With(name, value, ArbiterFactLabel.Derived);
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

    private static ArbiterEvaluator Evaluator(
        string arbiterDirectory,
        IArbiterProvider provider,
        IArbiterGitFacts? gitFacts = null) =>
        new(
            provider,
            _ => null,
            new InFlightLedger(arbiterDirectory),
            new DecisionLog(arbiterDirectory),
            gitFacts ?? new PassThroughGitFacts(),
            new PassThroughPlanReader(),
            new FixedClock(Now),
            SecondaryTriggers.For);

    private static string Marked(string body) => "KYBER-ARBITER: true\n\n" + body;

    /// <summary>A bug-crusher investigating a defect: the derivation's only input for <c>triage.shortcut</c>.</summary>
    private static ArbiterEvent BugCrusherInvestigate() => new()
    {
        Harness = "claude",
        Phase = "pre",
        Target = "bug-crusher-investigator",
        Prompt = Marked("Find the single site that throws."),
        HarnessCaller = "bug-crusher",
        IsDispatch = true,
        Session = "session-1",
    };

    /// <summary>A conductor's planner dispatch carrying the intake marker.</summary>
    private static ArbiterEvent IntakePlanner() => new()
    {
        Harness = "claude",
        Phase = "pre",
        Target = "architect",
        Prompt = Marked("INTAKE: add a retry to the uploader"),
        HarnessCaller = "conductor",
        IsDispatch = true,
        Session = "session-1",
    };

    /// <summary>A conductor's docs-dev dispatch whose first body line is the docs-impact marker.</summary>
    private static ArbiterEvent DocsImpactDispatch() => new()
    {
        Harness = "claude",
        Phase = "pre",
        Target = "docs-dev",
        Prompt = Marked("DOCS-IMPACT: true\n\nRecord the retry setting in the uploader guide."),
        HarnessCaller = "conductor",
        IsDispatch = true,
        Session = "session-1",
    };

    // ---- Shipped shape (acceptance: trigger, shadow, floor, answers and effects) ----

    /// <summary>The triage shortcut is a step-1 choice on <c>triage.shortcut</c>, with a 0.9 floor, shipped in shadow.</summary>
    [Fact]
    public void TriageShortcutShipsInShadowOnTheTriageTriggerWithANinePointZeroFloor()
    {
        ArbiterRule rule = Shipped(TriageRule);

        Assert.Equal(TriageTrigger, rule.Trigger);
        Assert.True(rule.Shadow);
        Assert.NotNull(rule.Ask);
        Assert.Equal("choice", rule.Ask.Type);
        Assert.Equal(0.9, rule.Ask.ConfidenceAtLeast);
    }

    /// <summary>A trivial defect answers the investigate with the digest; a defect that needs the architect keeps the dispatch.</summary>
    [Fact]
    public void TriageTrivialAnswersAndNeedsArchitectKeeps()
    {
        ArbiterRule rule = Shipped(TriageRule);

        Assert.Equal(Answered, rule.Effects[Trivial]);
        Assert.Equal(RuleEffects.Keep, rule.Effects[NeedsArchitect]);
    }

    /// <summary>The intake shortcut is a step-1 choice on <c>intake.shortcut</c>, floor 0.9, shipped in shadow.</summary>
    [Fact]
    public void IntakeShortcutShipsInShadowOnTheIntakeTriggerWithANinePointZeroFloor()
    {
        ArbiterRule rule = Shipped(IntakeRule);

        Assert.Equal(IntakeTrigger, rule.Trigger);
        Assert.True(rule.Shadow);
        Assert.NotNull(rule.Ask);
        Assert.Equal("choice", rule.Ask.Type);
        Assert.Equal(0.9, rule.Ask.ConfidenceAtLeast);
    }

    /// <summary>A plan or spec request is answered with the intake recommendation rather than a planner spawn.</summary>
    [Fact]
    public void IntakePlanAndSpecAreBothAnswered()
    {
        ArbiterRule rule = Shipped(IntakeRule);

        Assert.Equal(Answered, rule.Effects[Plan]);
        Assert.Equal(Answered, rule.Effects[Spec]);
    }

    /// <summary>
    /// The docs-impact shortcut reads the step-0 fact <c>touches</c>: a change whose paths would need docs keeps
    /// the dispatch and never reaches the provider; otherwise the step-1 floor of 0.9 decides.
    /// </summary>
    [Fact]
    public void DocsImpactShortcutShipsInShadowWithAStepZeroTouchesFactAndANinePointZeroFloor()
    {
        ArbiterRule rule = Shipped(DocsImpactRule);

        Assert.Equal(DocsImpactTrigger, rule.Trigger);
        Assert.True(rule.Shadow);
        Assert.NotNull(rule.Decide);
        Assert.Equal(RuleEffects.Keep, rule.Effects[Touches]);
        Assert.NotNull(rule.Ask);
        Assert.Equal(0.9, rule.Ask.ConfidenceAtLeast);
        Assert.Equal(Answered, rule.Effects[NoDocChange]);
        Assert.Equal(RuleEffects.Keep, rule.Effects[DocChange]);
    }

    /// <summary>A governed path in the change answers <c>touches</c> at step 0, so the provider is not asked.</summary>
    [Fact]
    public void DocsImpactTouchesAnswersAtStepZeroAndIsNotAsked()
    {
        ArbiterRule rule = Shipped(DocsImpactRule);
        ArbiterFactSet facts = new ArbiterFactSet().With(GovernedPathsFact, true, ArbiterFactLabel.Derived);

        string answer = RuleEngine.Decide(rule, facts);

        Assert.Equal(Touches, answer);
        Assert.False(rule.ShouldAsk(answer));
    }

    // ---- Derivation (acceptance: each shortcut comes only from its own dispatch shape) ----

    /// <summary>A bug-crusher investigate of the investigator derives the triage shortcut.</summary>
    [Fact]
    public void ABugCrusherInvestigateDerivesTheTriageShortcut()
    {
        IReadOnlyList<string> triggers = SecondaryTriggers.For("investigate", BugCrusherInvestigate());

        Assert.Contains(TriageTrigger, triggers);
    }

    /// <summary>An investigate from any other caller does not derive the triage shortcut.</summary>
    [Fact]
    public void AnUnrelatedInvestigateDerivesNoShortcut()
    {
        ArbiterEvent architect = BugCrusherInvestigate() with { HarnessCaller = "architect" };
        ArbiterEvent otherTarget = BugCrusherInvestigate() with { Target = "csharp-dev" };

        Assert.Empty(SecondaryTriggers.For("investigate", architect));
        Assert.Empty(SecondaryTriggers.For("investigate", otherTarget));
    }

    /// <summary>The intake shortcut derives from a planner dispatch carrying the intake marker.</summary>
    [Fact]
    public void AnIntakePlannerDispatchDerivesTheIntakeShortcut()
    {
        Assert.Contains(IntakeTrigger, SecondaryTriggers.For("delegate.planner", IntakePlanner()));
    }

    /// <summary>The intake shortcut derives ONLY from <c>delegate.planner</c>: the same prompt on a plain delegate derives nothing.</summary>
    [Fact]
    public void TheIntakeShortcutIsNotDerivedFromAPlainDelegate()
    {
        Assert.DoesNotContain(IntakeTrigger, SecondaryTriggers.For("delegate", IntakePlanner()));
    }

    /// <summary>A planner dispatch without the intake marker derives no intake shortcut.</summary>
    [Fact]
    public void APlannerDispatchWithoutTheIntakeMarkerDerivesNoShortcut()
    {
        ArbiterEvent plain = IntakePlanner() with { Prompt = Marked("Write the plan for the uploader.") };

        Assert.Empty(SecondaryTriggers.For("delegate.planner", plain));
    }

    /// <summary>A docs-dev delegate whose first body line is the docs-impact marker derives the docs-impact shortcut.</summary>
    [Fact]
    public void ADocsDevDispatchWithTheDocsImpactMarkerDerivesTheDocsImpactShortcut()
    {
        Assert.Contains(DocsImpactTrigger, SecondaryTriggers.For("delegate", DocsImpactDispatch()));
    }

    /// <summary>A docs-dev delegate without the marker derives nothing.</summary>
    [Fact]
    public void ADocsDevDispatchWithoutTheMarkerDerivesNoShortcut()
    {
        ArbiterEvent plain = DocsImpactDispatch() with { Prompt = Marked("Update the uploader guide.") };

        Assert.Empty(SecondaryTriggers.For("delegate", plain));
    }

    // ---- Triage through the evaluator (acceptance: enforced answer denies, shadow only logs) ----

    /// <summary>An enforced trivial answer denies the investigator dispatch: the event outcome is <c>answer</c>.</summary>
    [Fact]
    public async Task AnEnforcedTrivialTriageAnswerDeniesTheInvestigatorDispatch()
    {
        ScriptedProvider provider = new(Trivial, 0.95);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            BugCrusherInvestigate(), ConfigWith([Enforced(TriageRule)]));

        Assert.Equal(Answered, result.Outcome);
        ArbiterSecondaryEvaluation secondary = Assert.Single(Assert.Single(result.Dispatches).Secondary);
        Assert.Equal(TriageTrigger, secondary.Trigger);
        Assert.Equal(Answered, secondary.Outcome);
    }

    /// <summary>
    /// The deny reason is the exact triage substitute digest for the decision that answered. The decision id is
    /// read back from the decision record, never invented.
    /// </summary>
    [Fact]
    public async Task TheTriageDenyReasonCarriesTheSubstituteDigestForItsDecision()
    {
        ScriptedProvider provider = new(Trivial, 0.95);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        await evaluator.EvaluateAsync(BugCrusherInvestigate(), ConfigWith([Enforced(TriageRule)]));

        IReadOnlyList<ArbiterDecisionRecord> records = new DecisionLog(ArbiterDirectory).ReadAll();
        ArbiterDecisionRecord triage = Assert.Single(records, record => record.Trigger == TriageTrigger);
        string expected = string.Join(
            "\n",
            "REPRODUCED: not-run (Arbiter shortcut)",
            $"ROOT_CAUSE: trivial single-site defect (Arbiter decision {triage.Id})",
            "BLAST_RADIUS: single file",
            "VERDICT: TRIVIAL",
            $"ARBITER_DECISION: {triage.Id}");
        Assert.Equal(expected, SubstituteDigests.Triage(triage.Id));
    }

    /// <summary>The shipped shadow triage rule never denies: it logs the answer it would have given.</summary>
    [Fact]
    public async Task TheShadowTriageAnswerOnlyLogsWhatItWouldHaveDone()
    {
        ScriptedProvider provider = new(Trivial, 0.95);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            BugCrusherInvestigate(), ConfigWith([Shipped(TriageRule)]));

        Assert.NotEqual(Answered, result.Outcome);
        ArbiterDecisionRecord triage = Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll(), record => record.Trigger == TriageTrigger);
        ArbiterDecisionRule rule = Assert.Single(triage.Rules, r => r.Id == TriageRule);
        Assert.Equal(Answered, rule.WouldEffect);
    }

    /// <summary>A trivial answer below the 0.9 floor is undecidable, so the investigator is dispatched as before.</summary>
    [Fact]
    public async Task ATriageAnswerBelowTheFloorLeavesTheDispatchUnchanged()
    {
        ScriptedProvider provider = new(Trivial, 0.8);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            BugCrusherInvestigate(), ConfigWith([Enforced(TriageRule)]));

        Assert.NotEqual(Answered, result.Outcome);
        Assert.False(result.IsError);
    }

    /// <summary>A provider that did not answer leaves the dispatch unchanged: the expensive route proceeds.</summary>
    [Fact]
    public async Task AProviderFailureLeavesTheTriageDispatchUnchanged()
    {
        ScriptedProvider provider = new(Trivial, 0.95, requestSent: false);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            BugCrusherInvestigate(), ConfigWith([Enforced(TriageRule)]));

        Assert.NotEqual(Answered, result.Outcome);
        Assert.False(result.IsError);
    }

    /// <summary>An answer of <c>needs-architect</c> keeps the investigator dispatch.</summary>
    [Fact]
    public async Task ANeedsArchitectTriageAnswerKeepsTheDispatch()
    {
        ScriptedProvider provider = new(NeedsArchitect, 0.95);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            BugCrusherInvestigate(), ConfigWith([Enforced(TriageRule)]));

        Assert.NotEqual(Answered, result.Outcome);
    }

    // ---- Intake through the evaluator ----

    /// <summary>An enforced plan answer on an intake returns the intake recommendation instead of spawning the planner.</summary>
    [Fact]
    public async Task AnEnforcedIntakePlanAnswerIsAnsweredNotSpawned()
    {
        ScriptedProvider provider = new(Plan, 0.95);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            IntakePlanner(), ConfigWith([Enforced(IntakeRule)]));

        Assert.Equal(Answered, result.Outcome);
    }

    /// <summary>The intake digest is the exact recommendation for the decision that answered.</summary>
    [Fact]
    public async Task TheIntakeAnswerCarriesTheIntakeRecommendationDigest()
    {
        ScriptedProvider provider = new(Spec, 0.95);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        await evaluator.EvaluateAsync(IntakePlanner(), ConfigWith([Enforced(IntakeRule)]));

        ArbiterDecisionRecord intake = Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll(), record => record.Trigger == IntakeTrigger);
        string expected = string.Join(
            "\n",
            "STATUS: INTAKE_RECOMMENDATION",
            "INPUT: open request",
            "RECOMMENDATION: SPEC",
            $"RATIONALE: Arbiter shortcut (decision {intake.Id})",
            "GAPS: none",
            "OPEN_QUESTIONS: none",
            $"ARBITER_DECISION: {intake.Id}");
        Assert.Equal(expected, SubstituteDigests.Intake("open request", "SPEC", intake.Id));
    }

    /// <summary>A shadow intake answer changes nothing the host sees.</summary>
    [Fact]
    public async Task TheShadowIntakeAnswerLeavesThePlannerDispatchUnchanged()
    {
        ScriptedProvider provider = new(Plan, 0.95);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            IntakePlanner(), ConfigWith([Shipped(IntakeRule)]));

        Assert.NotEqual(Answered, result.Outcome);
    }

    /// <summary>An intake answer below the floor is undecidable and the planner runs as before.</summary>
    [Fact]
    public async Task AnIntakeAnswerBelowTheFloorLeavesThePlannerDispatchUnchanged()
    {
        ScriptedProvider provider = new(Plan, 0.7);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            IntakePlanner(), ConfigWith([Enforced(IntakeRule)]));

        Assert.NotEqual(Answered, result.Outcome);
    }

    // ---- Docs-impact through the evaluator ----

    /// <summary>A change that touches no governed path, answered with confidence, returns the no-doc-change digest.</summary>
    [Fact]
    public async Task AnUntouchedDocsImpactAnsweredWithConfidenceIsAnswered()
    {
        ScriptedProvider provider = new(NoDocChange, 0.95);
        ArbiterEvaluator evaluator = Evaluator(
            ArbiterDirectory, provider, new FactGitFacts(GovernedPathsFact, false));

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            DocsImpactDispatch(), ConfigWith([Enforced(DocsImpactRule)]));

        Assert.Equal(Answered, result.Outcome);
    }

    /// <summary>A governed path in the change keeps the dispatch at step 0 and the provider is never asked.</summary>
    [Fact]
    public async Task ATouchedDocsImpactKeepsTheDispatchWithoutAskingTheProvider()
    {
        ScriptedProvider provider = new(NoDocChange, 0.95);
        ArbiterEvaluator evaluator = Evaluator(
            ArbiterDirectory, provider, new FactGitFacts(GovernedPathsFact, true));

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            DocsImpactDispatch(), ConfigWith([Enforced(DocsImpactRule)]));

        Assert.Equal(0, provider.CallCount);
        Assert.NotEqual(Answered, result.Outcome);
    }

    /// <summary>The docs-impact digest is the exact no-doc-change text for the decision that answered.</summary>
    [Fact]
    public async Task TheDocsImpactAnswerCarriesTheNoDocChangeDigest()
    {
        ScriptedProvider provider = new(NoDocChange, 0.95);
        ArbiterEvaluator evaluator = Evaluator(
            ArbiterDirectory, provider, new FactGitFacts(GovernedPathsFact, false));

        await evaluator.EvaluateAsync(DocsImpactDispatch(), ConfigWith([Enforced(DocsImpactRule)]));

        ArbiterDecisionRecord docs = Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll(), record => record.Trigger == DocsImpactTrigger);
        string expected = string.Join(
            "\n",
            $"DOCS_REVIEW: no doc change with justification — Arbiter shortcut (decision {docs.Id})",
            "RECOMMENDATIONS: none",
            $"ARBITER_DECISION: {docs.Id}");
        Assert.Equal(expected, SubstituteDigests.DocsImpact(docs.Id));
    }

    // ---- Audit truthfulness (acceptance: an answered shortcut is not counted as a spawn) ----

    /// <summary>
    /// An answered shortcut withheld its worker, so its ledger id is in the not-dispatched set. The audit reads
    /// that set to count spawns, and a withheld worker must not count as one.
    /// </summary>
    [Fact]
    public async Task AnAnsweredShortcutIsRecordedAsANotDispatchedDispatch()
    {
        ScriptedProvider provider = new(Trivial, 0.95);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            BugCrusherInvestigate(), ConfigWith([Enforced(TriageRule)]));

        Assert.Contains(result.LedgerId, LedgerFacts.NotDispatchedLedgerIds(new DecisionLog(ArbiterDirectory)));
    }

    /// <summary>A shadow shortcut dispatched the worker, so its ledger id is not in the not-dispatched set.</summary>
    [Fact]
    public async Task AShadowShortcutDoesNotMarkTheDispatchAsNotDispatched()
    {
        ScriptedProvider provider = new(Trivial, 0.95);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            BugCrusherInvestigate(), ConfigWith([Shipped(TriageRule)]));

        Assert.DoesNotContain(result.LedgerId, LedgerFacts.NotDispatchedLedgerIds(new DecisionLog(ArbiterDirectory)));
    }

    // ---- Shipped pins (acceptance: the three ids are in the catalogue and shipped in shadow) ----

    /// <summary>All three shortcut rules ship in shadow, so an upgrade withholds no worker before calibration.</summary>
    [Fact]
    public void TheThreeShortcutRulesShipInShadow()
    {
        Assert.All(new[] { TriageRule, IntakeRule, DocsImpactRule }, id => Assert.True(Shipped(id).Shadow, id));
    }
}
