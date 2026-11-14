using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.3: the nitpick filter (<c>KW-ARB-NITPICK-001</c> on <c>lens.returned</c>) and its
/// refuter gate (<c>KW-ARB-NITPICK-002</c> on <c>refute.spawn</c>). RED: the two rules are not shipped,
/// <c>GateReportFacts</c> is not yet shared with the lens path, <c>ArbiterFindingNotices</c> does not exist,
/// and the lens return does not yet supply <c>finding.severity</c> or <c>finding.gate-corroborated</c>.
/// </summary>
/// <remarks>
/// <para>
/// Assumed names (GREEN must match): <c>KyberWeave.Core.Arbiter.Facts.ArbiterFindingNotices.DroppedLine(string findingId,
/// string decisionId)</c> returns <c>ARBITER_DROPPED: &lt;finding id&gt; rule=KW-ARB-NITPICK-001 decision=&lt;id&gt;</c>;
/// <c>ArbiterDecisionRule.Suppressed</c> (<c>bool?</c>, omitted unless true) marks a drop that an annotate outranked.
/// The review store is <c>ArbiterRunStore</c> from task 3.1.
/// </para>
/// <para>
/// Both shadow states are asserted: a shipped (shadow) rule must leave the outcome byte-identical to a
/// run without it, and an enforced copy of the same rule must drop.
/// </para>
/// </remarks>
public sealed class ArbiterNitpickRuleTests : IDisposable
{
    private const string NitpickRule = "KW-ARB-NITPICK-001";
    private const string RefuterRule = "KW-ARB-NITPICK-002";
    private const string AnnotateRule = "KW-ARB-QUOTE-001";
    private const string ReviewRun = "RUN-NITPICK";
    private const string FindingId = "nit-1";
    private const string Substantive = "substantive";
    private const string Nitpick = "nitpick";
    private const string Unsure = "unsure";
    private const string Prompt =
        "KYBER-ARBITER: true\nLENS: correctness\nREVIEW_RUN: " + ReviewRun + "\n\nApply the lens.";
    private const string MinorFinding = "id: nit-1\nseverity: Minor\nfile: src/Widget.cs\nline: 12\nclaim: naming";
    private const string CriticalFinding = "id: crit-1\nseverity: Critical\nfile: src/Widget.cs\nline: 12\nclaim: null deref";


    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-nitpick-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    private static DateTimeOffset Now => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Answers and scores per rule id, and counts every call so a test can prove a question was never asked.</summary>
    private sealed class ScriptedProvider : IArbiterProvider
    {
        public int CallCount { get; private set; }

        public IReadOnlyDictionary<string, string> AnswerByRuleId { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, ArbiterStep1Score> ScoreByRuleId { get; init; } =
            new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal);

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Dictionary<string, string> answers = rules
                .Where(rule => AnswerByRuleId.ContainsKey(rule.Id))
                .ToDictionary(rule => rule.Id, rule => AnswerByRuleId[rule.Id], StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = rules
                .Where(rule => ScoreByRuleId.ContainsKey(rule.Id))
                .ToDictionary(rule => rule.Id, rule => ScoreByRuleId[rule.Id], StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(
                answers, "jev-1.13.0", null, RequestSent: true, scores));
        }
    }

    /// <summary>
    /// Answers each step-1 question by the finding id in the facts it was asked with, so two findings in one
    /// return can get different answers. A finding not named in the map is answered <c>substantive</c>.
    /// </summary>
    private sealed class PerFindingProvider(IReadOnlyDictionary<string, string> answerByFindingId) : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            string id = facts.TryGet("finding.id", out ArbiterFact? idFact) && idFact.Value is string text ? text : string.Empty;
            string answer = answerByFindingId.TryGetValue(id, out string? named) ? named : Substantive;
            double confidence = answer == Nitpick ? 0.9 : 0.95;
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

    private static KyberWeaveConfig ConfigWith(IReadOnlyList<ArbiterRule> rules) =>
        new()
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = rules },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };

    private static ArbiterRule Shipped(string id) =>
        HookAskedRules.AsHookAsked(Raw(id));

    /// <summary>The rule exactly as shipped, before <see cref="HookAskedRules.AsHookAsked"/>.</summary>
    private static ArbiterRule Raw(string id) =>
        Assert.Single(
            ArbiterConfig.ProductDefaults.Rules,
            rule => string.Equals(rule.Id, id, StringComparison.Ordinal));

    /// <summary>The shipped rule as an enforced rule: same clauses, shadow off.</summary>
    private static ArbiterRule Enforced(string id) => Shipped(id) with { Shadow = false };

    /// <summary>A lens return whose one finding carries the given severity and corroboration.</summary>
    private static ArbiterEvent LensReturn(string findingYaml) =>
        new()
        {
            Harness = "claude",
            Phase = "return",
            Target = "review-lens",
            Prompt = Prompt,
            HarnessCaller = "code-reviewer",
            IsDispatch = true,
            Session = "session-1",
            ToolOutput = findingYaml,
        };

    private static ArbiterFactSet FindingFacts(string severity, bool gateCorroborated) =>
        new ArbiterFactSet()
            .With("finding.id", FindingId, ArbiterFactLabel.Asserted)
            .With("finding.severity", severity, ArbiterFactLabel.Asserted)
            .With("finding.gate-corroborated", gateCorroborated, ArbiterFactLabel.Asserted);

    private static ArbiterEvaluator Evaluator(string arbiterDirectory, IArbiterProvider provider) =>
        new(
            provider,
            _ => null,
            new InFlightLedger(arbiterDirectory),
            new DecisionLog(arbiterDirectory),
            new PassThroughGitFacts(),
            new PassThroughPlanReader(),
            new FixedClock(Now),
            (_, _) => []);


    // ---- Shipped shape (acceptance: trigger, shadow, answers and effects) ----

    /// <summary>The filter reads a lens return, so it must sit on <c>lens.returned</c>, not a pre-dispatch trigger.</summary>
    [Fact]
    public void TheShippedNitpickRuleRunsOnTheLensReturnedTrigger()
    {
        Assert.Equal("lens.returned", Shipped(NitpickRule).Trigger);
    }

    /// <summary>
    /// The rule ships in shadow: it is evaluated and logged, but its drop does not change what the host
    /// sees, so an upgrade does not silently discard findings before the calibration is run.
    /// </summary>
    [Fact]
    public void TheShippedNitpickRuleIsEnforcedByTheVerdictAndNotAskedByTheHook()
    {
        ArbiterRule rule = Raw(NitpickRule);

        Assert.False(rule.Shadow);
        Assert.Contains(rule.Decide!, clause => clause.Answer == "asked-by-review-verdict");
        Assert.Equal(RuleEffects.Allow, rule.Effects["asked-by-review-verdict"]);
    }

    /// <summary>A nitpick is dropped and a substantive finding is allowed; no other answer reaches an effect.</summary>
    [Fact]
    public void NitpickDropsAndSubstantiveAllows()
    {
        ArbiterRule rule = Shipped(NitpickRule);

        Assert.Equal(RuleEffects.Drop, rule.Effects[Nitpick]);
        Assert.Equal(RuleEffects.Allow, rule.Effects[Substantive]);
    }

    /// <summary>The question is a choice with a 0.85 floor: a weaker answer is undecidable and the finding is kept.</summary>
    [Fact]
    public void TheStepOneQuestionIsAChoiceWithAZeroEightFiveFloor()
    {
        RuleAsk? ask = Shipped(NitpickRule).Ask;

        Assert.NotNull(ask);
        Assert.Equal("choice", ask.Type);
        Assert.Equal(0.85, ask.ConfidenceAtLeast);
    }

    // ---- Step 0 (acceptance: no provider call for Critical or gate-corroborated) ----

    /// <summary>A Critical finding is substantive at step 0, so the provider is never asked about it.</summary>
    [Fact]
    public void ACriticalFindingIsSubstantiveAtStepZeroAndIsNotAsked()
    {
        ArbiterRule rule = Shipped(NitpickRule);

        string answer = RuleEngine.Decide(rule, FindingFacts("Critical", gateCorroborated: false));

        Assert.Equal(Substantive, answer);
        Assert.False(rule.ShouldAsk(answer));
    }

    /// <summary>A finding a failed gate already corroborates is substantive at step 0, whatever its severity.</summary>
    [Fact]
    public void AGateCorroboratedFindingIsSubstantiveAtStepZeroAndIsNotAsked()
    {
        ArbiterRule rule = Shipped(NitpickRule);

        string answer = RuleEngine.Decide(rule, FindingFacts("Minor", gateCorroborated: true));

        Assert.Equal(Substantive, answer);
        Assert.False(rule.ShouldAsk(answer));
    }

    /// <summary>A minor finding no gate corroborates is not decided at step 0, so it goes to the provider.</summary>
    [Fact]
    public void AMinorUncorroboratedFindingIsLeftForStepOne()
    {
        ArbiterRule rule = Shipped(NitpickRule);

        string answer = RuleEngine.Decide(rule, FindingFacts("Minor", gateCorroborated: false));

        Assert.Equal(RuleEngine.Undecidable, answer);
        Assert.True(rule.ShouldAsk(answer));
    }

    // ---- Step 1 through the evaluator (acceptance: nitpick drops, substantive allows, the rest is kept) ----

    /// <summary>An enforced nitpick at or above the floor drops the finding.</summary>
    [Fact]
    public async Task AnEnforcedNitpickAtTheFloorDropsTheFinding()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal) { [NitpickRule] = Nitpick },
            ScoreByRuleId = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [NitpickRule] = new ArbiterStep1Score(null, 0.9, 0.85),
            },
        };
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            LensReturn(MinorFinding), ConfigWith([Enforced(NitpickRule)]));

        Assert.Equal(RuleEffects.Drop, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>The shipped shadow rule leaves the outcome exactly as a run without it: nothing is dropped.</summary>
    [Fact]
    public async Task TheShadowNitpickRuleLeavesTheShippedOutcomeUnchanged()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal) { [NitpickRule] = Nitpick },
            ScoreByRuleId = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [NitpickRule] = new ArbiterStep1Score(null, 0.9, 0.85),
            },
        };
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            LensReturn(MinorFinding), ConfigWith([Shipped(NitpickRule)]));

        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>A substantive answer keeps the finding.</summary>
    [Fact]
    public async Task ASubstantiveAnswerAllowsTheFinding()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal) { [NitpickRule] = Substantive },
            ScoreByRuleId = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [NitpickRule] = new ArbiterStep1Score(null, 0.95, 0.85),
            },
        };
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            LensReturn(MinorFinding), ConfigWith([Enforced(NitpickRule)]));

        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>A nitpick answer below the floor is undecidable, so the finding is kept.</summary>
    [Fact]
    public async Task ANitpickBelowTheFloorKeepsTheFinding()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal) { [NitpickRule] = Nitpick },
            ScoreByRuleId = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [NitpickRule] = new ArbiterStep1Score(null, 0.6, 0.85),
            },
        };
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            LensReturn(MinorFinding), ConfigWith([Enforced(NitpickRule)]));

        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>An unsure answer keeps the finding; only a confident nitpick drops it.</summary>
    [Fact]
    public async Task AnUnsureAnswerKeepsTheFinding()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal) { [NitpickRule] = Unsure },
        };
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            LensReturn(MinorFinding), ConfigWith([Enforced(NitpickRule)]));

        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>With the provider set to <c>none</c> nothing is asked, so the finding is kept.</summary>
    [Fact]
    public async Task WithNoProviderTheFindingIsKept()
    {
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, new NoneProvider());

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            LensReturn(MinorFinding), ConfigWith([Enforced(NitpickRule)]));

        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>A Critical finding is never asked and never dropped, even when the provider would say nitpick.</summary>
    [Fact]
    public async Task ACriticalFindingIsNeverSentToTheProvider()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal) { [NitpickRule] = Nitpick },
        };
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            LensReturn(CriticalFinding), ConfigWith([Enforced(NitpickRule)]));

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    // ---- Rendering and annotate precedence (acceptance: one line per dropped finding) ----

    /// <summary>An enforced drop renders exactly one post-dispatch line naming the finding, the rule and the decision.</summary>
    [Fact]
    public void AnEnforcedDropRendersOneDroppedLinePerFinding()
    {
        string line = ArbiterFindingNotices.DroppedLine(FindingId, "decision-1");

        Assert.Equal(
            "ARBITER_DROPPED: nit-1 rule=KW-ARB-NITPICK-001 decision=decision-1",
            line);
    }

    /// <summary>
    /// An annotation on the same finding outranks the drop: the finding is annotated, not dropped, and the
    /// drop is logged as suppressed so the decision log still shows the filter's view.
    /// </summary>
    [Fact]
    public async Task AnnotateOnTheSameFindingWinsAndTheDropIsLoggedAsSuppressed()
    {
        // The shipped QUOTE-001 is decide-only, so a provider answer cannot reach it; this test asks it instead,
        // with the same answers and effects the shipped rule declares.
        ArbiterRule annotate = new(
            AnnotateRule,
            "lens.returned",
            "Is each finding's excerpt present at its file:line?",
            ["present", "elsewhere", "absent"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["present"] = RuleEffects.Allow,
                ["elsewhere"] = RuleEffects.Annotate,
                ["absent"] = RuleEffects.Annotate,
            },
            Ask: new RuleAsk("choice", new Dictionary<string, string>(StringComparer.Ordinal)),
            Shadow: false);
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NitpickRule] = Nitpick,
                [AnnotateRule] = "elsewhere",
            },
            ScoreByRuleId = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [NitpickRule] = new ArbiterStep1Score(null, 0.9, 0.85),
                [AnnotateRule] = new ArbiterStep1Score(null, 0.95, 0.85),
            },
        };
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            LensReturn(MinorFinding),
            ConfigWith([Enforced(NitpickRule), annotate]));

        Assert.Equal(RuleEffects.Annotate, Assert.Single(result.Dispatches).Outcome);
        ArbiterDecisionRecord record = Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll());
        // The rule also has a step-0 entry (its decide clause was undecidable); the step-1 entry is the drop.
        ArbiterDecisionRule nitpick = Assert.Single(record.Rules, rule => rule.Id == NitpickRule && rule.Step == 1);
        Assert.Equal(RuleEffects.Drop, nitpick.Effect);
        Assert.True(nitpick.Suppressed);
    }

    // ---- Review store (acceptance: each returned finding is appended with its drop status) ----

    /// <summary>Every returned finding is appended to the review store, with the drop status the filter gave it.</summary>
    [Fact]
    public async Task EachReturnedFindingIsAppendedToTheReviewStoreWithItsDropStatus()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal) { [NitpickRule] = Nitpick },
            ScoreByRuleId = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [NitpickRule] = new ArbiterStep1Score(null, 0.9, 0.85),
            },
        };
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        await evaluator.EvaluateAsync(LensReturn(MinorFinding), ConfigWith([Enforced(NitpickRule)]));

        IReadOnlyList<string> stored = new ArbiterRunStore(ArbiterDirectory, null, () => Now).ReadFindings(ReviewRun);
        string item = Assert.Single(stored);
        Assert.Contains(FindingId, item, StringComparison.Ordinal);
        Assert.Contains("dropped", item, StringComparison.Ordinal);
    }

    /// <summary>
    /// The filter reads every finding a lens returns, not only the first: in a return of a substantive finding
    /// followed by a nitpick, only the nitpick is dropped, and the store shows the drop on the nitpick alone.
    /// Failure this guards: evaluating just the first finding leaves the second stored as kept.
    /// </summary>
    [Fact]
    public async Task InAMultiFindingReturnOnlyTheNitpickIsDropped()
    {
        const string twoFindings =
            "id: nit-1\nseverity: Minor\nfile: src/Widget.cs\nline: 12\nclaim: null check missing\n" +
            "id: nit-2\nseverity: Minor\nfile: src/Widget.cs\nline: 40\nclaim: naming";
        PerFindingProvider provider = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nit-2"] = Nitpick,
        });
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        await evaluator.EvaluateAsync(LensReturn(twoFindings), ConfigWith([Enforced(NitpickRule)]));

        IReadOnlyList<string> stored = new ArbiterRunStore(ArbiterDirectory, null, () => Now).ReadFindings(ReviewRun);
        Assert.Equal(2, stored.Count);
        Assert.Contains("id: nit-1", stored[0], StringComparison.Ordinal);
        Assert.EndsWith("status: kept", stored[0], StringComparison.Ordinal);
        Assert.Contains("id: nit-2", stored[1], StringComparison.Ordinal);
        Assert.EndsWith("status: dropped", stored[1], StringComparison.Ordinal);
    }

    /// <summary>With no <c>REVIEW_RUN</c> header the review store is unread: the refuter fact is absent, so the rule cannot answer.</summary>
    [Fact]
    public void WithoutAReviewRunHeaderTheStoreIsUnreadAndTheRefuterRuleIsUndecidable()
    {
        const string refutePrompt = "KYBER-ARBITER: true\nREFUTE: correctness/nit-1\n\nRefute the finding.";
        KyberWeaveConfig config = ConfigWith([Shipped(RefuterRule)]);

        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "refute.spawn", "review-refuter", refutePrompt, "code-reviewer", ArbiterCallerSources.Harness, config);

        Assert.False(facts.IsPresent("finding.dropped"));
        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(Shipped(RefuterRule), facts));
    }

    // ---- Refuter gate (acceptance: NITPICK-002 denies a refuter for a dropped finding) ----

    /// <summary>The refuter gate reads a refutation dispatch, so it sits on <c>refute.spawn</c>.</summary>
    [Fact]
    public void TheRefuterGateRunsOnTheRefuteSpawnTrigger()
    {
        Assert.Equal("refute.spawn", Shipped(RefuterRule).Trigger);
    }

    /// <summary>A refutation of a dropped finding is denied: the refutation is not spawned.</summary>
    [Fact]
    public void ARefuterForADroppedFindingIsDenied()
    {
        ArbiterRule rule = Shipped(RefuterRule);
        ArbiterFactSet facts = new ArbiterFactSet()
            .With("finding.id", FindingId, ArbiterFactLabel.Asserted)
            .With("finding.dropped", true, ArbiterFactLabel.Derived);

        string answer = RuleEngine.Decide(rule, facts);

        Assert.Equal(RuleEffects.Verify, rule.Effects[answer]);
    }

    /// <summary>The refuter gate follows the filter's shadow state, so the two rules are shipped in the same mode.</summary>
    [Fact]
    public void TheRefuterGateFollowsTheFilterShadowState()
    {
        Assert.Equal(Shipped(NitpickRule).Shadow, Shipped(RefuterRule).Shadow);
    }

    /// <summary>
    /// A lens may write its severity in any case. A <c>critical</c>, <c>Critical</c> or <c>CRITICAL</c> finding is
    /// substantive at step 0, so it is never sent to the provider and never dropped. Failure this guards: a
    /// lowercase severity falls through to step 1, and an enforced nitpick answer drops a critical defect.
    /// </summary>
    [Theory]
    [InlineData("critical")]
    [InlineData("Critical")]
    [InlineData("CRITICAL")]
    public async Task ACriticalFindingOfAnyCaseIsNeverSentToTheProviderOrDropped(string severity)
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal) { [NitpickRule] = Nitpick },
            ScoreByRuleId = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [NitpickRule] = new ArbiterStep1Score(null, 0.9, 0.85),
            },
        };
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);
        string finding = "id: crit-1\nseverity: " + severity + "\nfile: src/Widget.cs\nline: 12\nclaim: null deref";

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            LensReturn(finding), ConfigWith([Enforced(NitpickRule)]));

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>
    /// The store keys a finding by its lens-qualified id (<c>correctness/nit-1</c>), and a refutation names it the
    /// same way. A finding the filter dropped must read as dropped when its refuter is judged, so the refuter is
    /// denied. Failure this guards: the refutation's bare slug never matches the stored id, so the drop is invisible
    /// and a refuter runs against a finding the filter already set aside.
    /// </summary>
    [Fact]
    public async Task ARefuterForAFindingDroppedUnderItsLensIdIsDenied()
    {
        ArbiterRunStore store = new(ArbiterDirectory, null, () => Now);
        await store.AppendFindingAsync(ReviewRun, ReturnedFinding.StoredText(
            "id: correctness/nit-1\nseverity: Minor\nfile: src/Widget.cs\nline: 12\nclaim: naming", dropped: true));
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, new ScriptedProvider());
        ArbiterEvent refute = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "review-lens",
            Prompt = "KYBER-ARBITER: true\nREVIEW_RUN: " + ReviewRun + "\nREFUTE: correctness/nit-1\n\n" +
                     "id: correctness/nit-1\nfile: src/Widget.cs\nline: 12\nclaim: naming",
            HarnessCaller = "code-reviewer",
            IsDispatch = true,
            Session = "session-1",
        };

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(refute, ConfigWith([Enforced(RefuterRule)]));

        Assert.Equal(RuleEffects.Verify, Assert.Single(result.Dispatches).Outcome);
    }
}
