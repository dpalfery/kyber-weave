using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.5: duplicate findings. <c>REVIEW_RUN</c> names the review a dispatch belongs to,
/// <c>review.prior-findings</c> lists that review's earlier findings on the same file, and
/// <c>KW-ARB-DUP-001</c> on <c>refute.spawn</c> denies a refuter whose finding repeats an earlier one.
/// RED: the rule is not shipped, the prior-findings fact is not built, and the for-each expansion of the
/// step-1 question is not wired to it yet.
/// </summary>
/// <remarks>
/// <para>
/// Batch contract (approved design): <c>KW-ARB-DUP-001</c> has <c>for-each: review.prior-findings</c>, so every
/// candidate becomes one step-1 question and all of them travel in ONE provider request (at most three).
/// The decision is: the FIRST candidate, in store order (oldest first), answered <c>same-defect</c> at or above
/// the 0.9 floor wins; its id becomes <c>duplicate-of</c>. Candidates answered otherwise do not decide.
/// </para>
/// <para>
/// Assumed names (GREEN must match): <c>ArbiterFindingNotices.DuplicateLine(string findingId, string earlierId,
/// string decisionId)</c> returns <c>ARBITER_DUPLICATE: &lt;finding id&gt; of &lt;earlier id&gt; decision=&lt;id&gt;</c>.
/// Each expanded question's rule id is <c>KW-ARB-DUP-001@&lt;list index&gt;</c>, and its <c>prior</c> state field names
/// the scoped candidate fact that holds the stored text, so the provider reads which candidate a question is about
/// from the batch facts it is handed. The prior findings are read from
/// <c>ArbiterRunStore</c> (task 3.1), one stored item per finding.
/// </para>
/// </remarks>
public sealed class ArbiterDuplicateFindingRuleTests : IDisposable
{
    private const string DuplicateRule = "KW-ARB-DUP-001";
    private const string SameDefect = "same-defect";
    private const string DifferentDefect = "different-defect";
    private const string ThisRun = "RUN-THIS";
    private const string OtherRun = "RUN-OTHER";
    private const string Refuted = "nit-2";

    /// <summary>The refute header names the lens and the finding slug: <c>&lt;lens&gt;/&lt;slug&gt;</c>.</summary>
    private const string RefuteValue = "correctness/" + Refuted;

    private const string RefutePrompt =
        "KYBER-ARBITER: true\nREVIEW_RUN: " + ThisRun + "\nREFUTE: " + RefuteValue + "\n\n" +
        "id: " + Refuted + "\nfile: src/Widget.cs\nline: 12\nclaim: naming";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-duplicate-" + Guid.NewGuid().ToString("N"));

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
    /// Answers each candidate's question by that candidate's stored text, and records every provider request
    /// as the list of candidates it carried. A candidate the function does not name must be answered by it.
    /// </summary>
    private sealed class BatchDuplicateProvider(Func<string, string> answerFor) : IArbiterProvider
    {
        private readonly List<IReadOnlyList<string>> _batches = [];

        /// <summary>One entry per provider request, holding the candidate texts that request carried, in order.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Batches => _batches;

        /// <summary>The number of provider requests made.</summary>
        public int CallCount => _batches.Count;

        /// <summary>Every candidate text the provider was asked about, across all requests, in ask order.</summary>
        public IReadOnlyList<string> Asked => _batches.SelectMany(batch => batch).ToList();

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            List<ArbiterRule> questions = rules.Where(rule => rule.Ask is not null).ToList();
            _batches.Add(questions.Select(rule => CandidateOf(rule, facts)).ToList());
            Dictionary<string, string> answers = questions
                .ToDictionary(rule => rule.Id, rule => answerFor(CandidateOf(rule, facts)), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.13.0", null, RequestSent: true));
        }

        /// <summary>
        /// The candidate a per-item question is about: the text of the scoped fact its <c>prior</c> state field
        /// names, read from the batch facts the provider is handed. The expansion keys the rule id by list index,
        /// so the id no longer carries the text.
        /// </summary>
        private static string CandidateOf(ArbiterRule rule, ArbiterFactSet facts)
        {
            if (rule.Ask is null || !rule.Ask.State.TryGetValue("prior", out string? factName))
            {
                return string.Empty;
            }

            return facts.TryGet(factName, out ArbiterFact? fact) && fact.Value is string text ? text : string.Empty;
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

    private static ArbiterRule Enforced(string id) => Shipped(id) with { Shadow = false };

    private static ArbiterEvent Refute(string runHeader) =>
        new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "review-lens",
            Prompt = runHeader + "REFUTE: " + RefuteValue + "\n\n" +
                     "id: " + Refuted + "\nfile: src/Widget.cs\nline: 12\nclaim: naming",
            HarnessCaller = "code-reviewer",
            IsDispatch = true,
            Session = "session-1",
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
            (_, _) => []);

    /// <summary>A stored earlier finding on <paramref name="file"/> at <paramref name="line"/>.</summary>
    private static string Prior(string id, string file, int line) =>
        $"id: {id}\nfile: {file}\nline: {line}\nclaim: naming";

    private async Task SeedAsync(string reviewRun, params string[] findings)
    {
        ArbiterRunStore store = new(ArbiterDirectory, null, () => Now);
        foreach (string finding in findings)
        {
            ArbiterStoreStatus status = await store.AppendFindingAsync(reviewRun, finding);
            Assert.Equal(ArbiterStoreStatus.Stored, status);
        }
    }

    private async Task<ArbiterEvaluationResult> RefuteAsync(IArbiterProvider provider, string runHeader = "KYBER-ARBITER: true\nREVIEW_RUN: " + ThisRun + "\n")
    {
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);
        return await evaluator.EvaluateAsync(Refute(runHeader), ConfigWith([Enforced(DuplicateRule)]));
    }

    // ---- REVIEW_RUN header (acceptance: recognised on lens and refute dispatches) ----

    /// <summary>The run id is a closed header, so a dispatch carrying it is recognised rather than rejected.</summary>
    [Fact]
    public void ReviewRunIsARecognisedHeaderName()
    {
        Assert.Contains("REVIEW_RUN", HeaderBlock.ClosedNames);
    }

    /// <summary>The run id parses out of a refute dispatch's header block.</summary>
    [Fact]
    public void ReviewRunParsesFromARefuteDispatch()
    {
        HeaderBlock block = HeaderBlock.Parse(RefutePrompt);

        Assert.Equal(ThisRun, block.Headers["REVIEW_RUN"]);
    }

    /// <summary>The run id parses out of a lens dispatch's header block too.</summary>
    [Fact]
    public void ReviewRunParsesFromALensDispatch()
    {
        HeaderBlock block = HeaderBlock.Parse("KYBER-ARBITER: true\nLENS: correctness\nREVIEW_RUN: " + ThisRun + "\n\nApply.");

        Assert.Equal(ThisRun, block.Headers["REVIEW_RUN"]);
    }

    /// <summary>
    /// With no <c>REVIEW_RUN</c> header there is no review to read, so the store is unread: the prior-findings
    /// fact is absent and the rule answers undecidable rather than comparing against an unrelated review.
    /// </summary>
    [Fact]
    public void WithoutAReviewRunHeaderTheStoreIsUnreadAndTheRuleIsUndecidable()
    {
        const string prompt = "KYBER-ARBITER: true\nREFUTE: " + RefuteValue + "\n\nid: " + Refuted + "\nfile: src/Widget.cs\nline: 12\nclaim: naming";
        KyberWeaveConfig config = ConfigWith([Shipped(DuplicateRule)]);

        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "refute.spawn", "review-refuter", prompt, "code-reviewer", ArbiterCallerSources.Harness, config);

        Assert.False(facts.IsPresent("review.prior-findings"));
        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(Shipped(DuplicateRule), facts));
    }

    // ---- review.prior-findings (acceptance: same review, same file, +-20 lines, at most 3, oldest first) ----

    /// <summary>
    /// A finding on the same file within 20 lines is a candidate. It goes into the one batch request, and the
    /// duplicate it names is the one the provider says repeats the refuted finding.
    /// </summary>
    [Fact]
    public async Task AnEarlierFindingOnTheSameFileWithinTwentyLinesIsCompared()
    {
        await SeedAsync(ThisRun, Prior("prior-1", "src/Widget.cs", 25));
        BatchDuplicateProvider provider = new(_ => SameDefect);

        ArbiterEvaluationResult result = await RefuteAsync(provider);

        Assert.Equal(1, provider.CallCount);
        Assert.Contains(Prior("prior-1", "src/Widget.cs", 25), provider.Asked);
        Assert.Equal(RuleEffects.Duplicate, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>A finding 21 lines away is outside the window and is never compared.</summary>
    [Fact]
    public async Task AnEarlierFindingTwentyOneLinesAwayIsNotCompared()
    {
        await SeedAsync(ThisRun, Prior("prior-far", "src/Widget.cs", 33));
        BatchDuplicateProvider provider = new(_ => SameDefect);

        ArbiterEvaluationResult result = await RefuteAsync(provider);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>A finding on another file is never compared, however close its line.</summary>
    [Fact]
    public async Task AnEarlierFindingOnAnotherFileIsNotCompared()
    {
        await SeedAsync(ThisRun, Prior("prior-other", "src/Other.cs", 12));
        BatchDuplicateProvider provider = new(_ => SameDefect);

        ArbiterEvaluationResult result = await RefuteAsync(provider);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>
    /// At most three earlier findings go into the batch, so a fourth same-file match is never asked and the
    /// batch is one request of exactly three questions.
    /// </summary>
    [Fact]
    public async Task AtMostThreeEarlierFindingsAreCompared()
    {
        await SeedAsync(
            ThisRun,
            Prior("prior-1", "src/Widget.cs", 10),
            Prior("prior-2", "src/Widget.cs", 11),
            Prior("prior-3", "src/Widget.cs", 13),
            Prior("prior-4", "src/Widget.cs", 14));
        BatchDuplicateProvider provider = new(text => text.Contains("prior-4", StringComparison.Ordinal) ? SameDefect : DifferentDefect);

        ArbiterEvaluationResult result = await RefuteAsync(provider);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(3, Assert.Single(provider.Batches).Count);
        Assert.DoesNotContain(provider.Asked, text => text.Contains("prior-4", StringComparison.Ordinal));
        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>
    /// Candidates go into the batch oldest first. When two both match, the first one stored is the one named.
    /// </summary>
    [Fact]
    public async Task TheOldestMatchingFindingIsTheOneNamedAsDuplicateOf()
    {
        await SeedAsync(
            ThisRun,
            Prior("prior-old", "src/Widget.cs", 10),
            Prior("prior-new", "src/Widget.cs", 11));
        BatchDuplicateProvider provider = new(_ => SameDefect);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            Refute("KYBER-ARBITER: true\nREVIEW_RUN: " + ThisRun + "\n"), ConfigWith([Enforced(DuplicateRule)]));

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(
            [Prior("prior-old", "src/Widget.cs", 10), Prior("prior-new", "src/Widget.cs", 11)],
            provider.Asked);
        ArbiterDecisionRecord record = Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll());
        Assert.Equal(RuleEffects.Duplicate, Assert.Single(result.Dispatches).Outcome);
        Assert.Equal($"ARBITER_DUPLICATE: {Refuted} of prior-old decision={record.Id}", DuplicateLineOf(record));
    }

    /// <summary>
    /// The first candidate in the batch answered <c>same-defect</c> decides; an earlier candidate that answered
    /// otherwise does not. Both candidates travel in the same single request.
    /// </summary>
    [Fact]
    public async Task TheFirstSameDefectAnswerDecidesTheDuplicate()
    {
        await SeedAsync(
            ThisRun,
            Prior("prior-a", "src/Widget.cs", 10),
            Prior("prior-b", "src/Widget.cs", 11));
        BatchDuplicateProvider provider = new(text => text.Contains("prior-b", StringComparison.Ordinal) ? SameDefect : DifferentDefect);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        await evaluator.EvaluateAsync(
            Refute("KYBER-ARBITER: true\nREVIEW_RUN: " + ThisRun + "\n"), ConfigWith([Enforced(DuplicateRule)]));

        Assert.Equal(1, provider.CallCount);
        ArbiterDecisionRecord record = Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll());
        Assert.Equal($"ARBITER_DUPLICATE: {Refuted} of prior-b decision={record.Id}", DuplicateLineOf(record));
    }

    /// <summary>A finding from another review run is never compared, even when it matches exactly.</summary>
    [Fact]
    public async Task AFindingFromAnotherReviewRunIsNeverCompared()
    {
        await SeedAsync(OtherRun, Prior("prior-elsewhere", "src/Widget.cs", 12));
        BatchDuplicateProvider provider = new(_ => SameDefect);

        ArbiterEvaluationResult result = await RefuteAsync(provider);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    // ---- Rule shape and decision (acceptance: floor 0.9, duplicate denies, anything else allows) ----

    /// <summary>The rule ships in shadow, so the shipped output is unchanged until the filter is enforced.</summary>
    [Fact]
    public void TheShippedDuplicateRuleIsEnforcedByTheVerdictAndNotAskedByTheHook()
    {
        ArbiterRule rule = Raw(DuplicateRule);

        Assert.Equal("refute.spawn", rule.Trigger);
        Assert.False(rule.Shadow);
        Assert.Contains(rule.Decide!, clause => clause.Answer == "asked-by-review-verdict");
    }

    /// <summary>The step-1 question is a choice with a 0.9 floor, and a same-defect answer is a duplicate.</summary>
    [Fact]
    public void TheStepOneQuestionIsAChoiceWithAZeroNinePointZeroFloor()
    {
        ArbiterRule rule = Shipped(DuplicateRule);

        Assert.NotNull(rule.Ask);
        Assert.Equal("choice", rule.Ask.Type);
        Assert.Equal(0.9, rule.Ask.ConfidenceAtLeast);
        Assert.Equal(RuleEffects.Duplicate, rule.Effects[SameDefect]);
        Assert.Equal(RuleEffects.Allow, rule.Effects[DifferentDefect]);
    }

    /// <summary>A same-defect answer below the floor is undecidable, so the refuter is not denied.</summary>
    [Fact]
    public async Task ASameDefectAnswerBelowTheFloorDoesNotDenyTheRefuter()
    {
        await SeedAsync(ThisRun, Prior("prior-1", "src/Widget.cs", 10));
        BatchDuplicateProvider provider = new(_ => SameDefect);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, new ScoredProvider(provider, 0.5));

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            Refute("KYBER-ARBITER: true\nREVIEW_RUN: " + ThisRun + "\n"), ConfigWith([Enforced(DuplicateRule)]));

        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    /// <summary>
    /// The shipped shadow rule leaves the refuter's outcome as it would be without the rule: a duplicate is
    /// recorded but the refuter is still spawned.
    /// </summary>
    [Fact]
    public async Task TheShadowDuplicateRuleDoesNotDenyTheRefuter()
    {
        await SeedAsync(ThisRun, Prior("prior-1", "src/Widget.cs", 10));
        BatchDuplicateProvider provider = new(_ => SameDefect);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            Refute("KYBER-ARBITER: true\nREVIEW_RUN: " + ThisRun + "\n"), ConfigWith([Shipped(DuplicateRule)]));

        Assert.Equal(RuleEffects.Allow, Assert.Single(result.Dispatches).Outcome);
    }

    // ---- Refuter outcome and denial (acceptance: denial reason, outcome recorded against the finding id) ----

    /// <summary>An enforced duplicate denies the refuter with the named reason.</summary>
    [Fact]
    public async Task AnEnforcedDuplicateDeniesTheRefuterWithItsReason()
    {
        await SeedAsync(ThisRun, Prior("prior-1", "src/Widget.cs", 10));
        BatchDuplicateProvider provider = new(_ => SameDefect);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, new ScoredProvider(provider, 0.95));

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            Refute("KYBER-ARBITER: true\nREVIEW_RUN: " + ThisRun + "\n"), ConfigWith([Enforced(DuplicateRule)]));

        Assert.Equal(RuleEffects.Duplicate, Assert.Single(result.Dispatches).Outcome);
        ArbiterDecisionRecord record = Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll());
        Assert.Equal($"ARBITER_DUPLICATE: {Refuted} of prior-1 decision={record.Id}", DuplicateLineOf(record));
    }

    /// <summary>
    /// Each refuter return records its outcome against its finding id in the review store.
    /// RED: production has no path that does this. A REFUTE return keeps the <c>refute.spawn</c> trigger
    /// (<c>TriggerClassifier.ToReturned</c> maps only <c>lens.spawn</c>), and the store is written for
    /// <c>lens.returned</c> only, as new finding items. GREEN needs csharp-dev to add a refutation-outcome
    /// write to <c>ArbiterRunStore</c> plus an evaluator call on the refute return, keyed by the finding slug.
    /// </summary>
    [Fact]
    public async Task EachRefuterReturnRecordsItsOutcomeAgainstItsFindingIdInTheStore()
    {
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, new BatchDuplicateProvider(_ => DifferentDefect));
        ArbiterEvent returned = new()
        {
            Harness = "claude",
            Phase = "return",
            Target = "review-lens",
            Prompt = "KYBER-ARBITER: true\nREVIEW_RUN: " + ThisRun + "\nREFUTE: " + RefuteValue + "\n\nRefute.",
            HarnessCaller = "code-reviewer",
            IsDispatch = true,
            Session = "session-1",
            ToolOutput = "VERIFIED: " + Refuted,
        };

        await evaluator.EvaluateAsync(returned, ConfigWith([Enforced(DuplicateRule)]));

        IReadOnlyList<string> stored = new ArbiterRunStore(ArbiterDirectory, null, () => Now).ReadFindings(ThisRun);
        string item = Assert.Single(stored);
        Assert.Contains(Refuted, item, StringComparison.Ordinal);
    }

    /// <summary>The duplicate decision's denial line as it appears on the one decision record.</summary>
    private static string DuplicateLineOf(ArbiterDecisionRecord record)
    {
        ArbiterDecisionRule rule = Assert.Single(record.Rules, r => r.Id == DuplicateRule);
        return rule.Evidence ?? string.Empty;
    }

    /// <summary>Wraps a batch provider and adds a fixed confidence score to every answer it gives.</summary>
    private sealed class ScoredProvider(BatchDuplicateProvider inner, double confidence) : IArbiterProvider
    {
        public async Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            ArbiterStep1BatchResult inside = await inner.AskStep1Async(rules, facts, step0Answers, cancellationToken);
            Dictionary<string, ArbiterStep1Score> scores = rules
                .Where(rule => rule.Ask is not null)
                .ToDictionary(rule => rule.Id, _ => new ArbiterStep1Score(null, confidence, rules[0].Ask!.ConfidenceAtLeast), StringComparer.Ordinal);
            return new ArbiterStep1BatchResult(inside.AnswersByRule, inside.Model, inside.Usage, inside.RequestSent, scores);
        }
    }

    /// <summary>
    /// A refuted finding is stored under its lens-qualified id (<c>correctness/nit-2</c>), and its own stored item sits
    /// on the same file and line as the refutation. The prior list holds the earlier findings only, so the duplicate
    /// rule is asked about the earlier finding and never about the refuted finding itself. Failure this guards: the
    /// refuted finding is listed as its own earlier finding, and the rule is asked to match it against itself.
    /// </summary>
    [Fact]
    public async Task ARefutedFindingIsNeverAskedAboutAsItsOwnEarlierFinding()
    {
        await SeedAsync(
            ThisRun,
            "id: correctness/nit-1\nfile: src/Widget.cs\nline: 8\nclaim: naming\nstatus: kept",
            "id: correctness/nit-2\nfile: src/Widget.cs\nline: 12\nclaim: naming\nstatus: kept");
        BatchDuplicateProvider provider = new(_ => DifferentDefect);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);
        ArbiterEvent refute = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "review-lens",
            Prompt = "KYBER-ARBITER: true\nREVIEW_RUN: " + ThisRun + "\nREFUTE: correctness/" + Refuted + "\n\n" +
                     "id: correctness/" + Refuted + "\nfile: src/Widget.cs\nline: 12\nclaim: naming",
            HarnessCaller = "code-reviewer",
            IsDispatch = true,
            Session = "session-1",
        };

        await evaluator.EvaluateAsync(refute, ConfigWith([Enforced(DuplicateRule)]));

        string asked = Assert.Single(provider.Asked);
        Assert.Contains("id: correctness/nit-1", asked, StringComparison.Ordinal);
    }
}
