using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.9: <c>KW-ARB-LENS-002</c> skips the lenses that have nothing to judge in a
/// change with no code in it, and <c>KW-ARB-PREEX-001</c> decides on the finding's own changed
/// lines rather than on the hunk set. The skip's review note is asserted in
/// <c>ArbiterRealEngineTests</c>, where the hook route is available.
/// </summary>
/// <remarks>
/// RED: <c>KW-ARB-LENS-002</c> is not in the shipped rules, so every assertion that looks it up
/// fails, and <c>KW-ARB-PREEX-001</c> still compares against <c>review.changed-hunks</c>, so the
/// fixtures below, which leave that fact absent, answer undecidable instead of their declared
/// answers.
/// </remarks>
public sealed class ArbiterLensNoCodeChangeRuleTests : IDisposable
{
    private const string NoCodeChangeRule = "KW-ARB-LENS-002";
    private const string PreExistingRule = "KW-ARB-PREEX-001";
    private const string NoCodeChangeAnswer = "no-code-change";

    private static readonly string[] SkippedLenses =
    [
        "correctness",
        "test-adequacy",
        "performance",
        "di-composition",
        "duplicate-implementation",
        "model-placement",
    ];

    private static readonly string[] NeverSkippedLenses =
    [
        "security",
        "documentation-accuracy",
        "blast-radius-revertibility",
    ];

    private static readonly string[] MarkdownOnlyPaths = ["docs/guide.md"];
    private static readonly string[] MixedPaths = ["docs/guide.md", "src/Code.cs"];
    private static readonly string[] UnknownLanguagePaths = ["assets/logo.xyz"];
    private static readonly string[] NoPaths = [];
    private static readonly int[] ChangedLines = [3, 7, 8];

    private readonly TempDirectory _temp = new();

    public ArbiterLensNoCodeChangeRuleTests()
    {
        Directory.CreateDirectory(ArbiterDirectory);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string ArbiterDirectory => Path.Combine(_temp.Path, "artifacts", "arbiter");

    public static IEnumerable<object[]> SkippedLensNames() =>
        SkippedLenses.Select(lens => new object[] { lens });

    public static IEnumerable<object[]> NeverSkippedLensNames() =>
        NeverSkippedLenses.Select(lens => new object[] { lens });

    /// <summary>
    /// The review facts a git read adds to a <c>lens.spawn</c>. An argument left null is not
    /// supplied at all, so an absent fact stays absent rather than becoming an empty one.
    /// </summary>
    private static List<(string Name, object Value)> ReviewPairs(
        string[]? changedPaths,
        string[]? noCodePaths,
        bool hunksPresent)
    {
        List<(string Name, object Value)> pairs = [];
        if (changedPaths is not null)
        {
            pairs.Add(("review.changed-paths", changedPaths));
        }

        if (noCodePaths is not null)
        {
            pairs.Add(("review.no-code-change-paths", noCodePaths));
        }

        if (hunksPresent)
        {
            pairs.Add(("review.changed-hunks", "hunks"));
        }

        return pairs;
    }

    private static ArbiterFactSet ReviewFacts(
        string lens,
        string[]? changedPaths,
        string[]? noCodePaths,
        bool hunksPresent)
    {
        ArbiterFactSet facts = new ArbiterFactSet().With("lens.name", lens, ArbiterFactLabel.Asserted);
        return ApplyPairs(facts, ReviewPairs(changedPaths, noCodePaths, hunksPresent));
    }

    private static ArbiterFactSet ApplyPairs(ArbiterFactSet facts, IEnumerable<(string Name, object Value)> pairs)
    {
        foreach ((string name, object value) in pairs)
        {
            facts = facts.With(name, value, ArbiterFactLabel.Asserted);
        }

        return facts;
    }

    private static ArbiterRuleAnswer StepZeroAnswer(ArbiterFactSet facts, string trigger, string ruleId)
    {
        ArbiterOutcome outcome = RuleEngine.EvaluateStep0(ArbiterConfig.ProductDefaults.Rules, facts, trigger);
        return Assert.Single(
            outcome.RuleAnswers,
            ruleAnswer => string.Equals(ruleAnswer.RuleId, ruleId, StringComparison.Ordinal));
    }

    /// <summary>
    /// On a change whose every path is a no-code-change path, each lens that judges code is
    /// skipped, and the rule answers <c>no-code-change</c>.
    /// </summary>
    /// <param name="lens">A lens that judges code.</param>
    [Theory]
    [MemberData(nameof(SkippedLensNames))]
    public void MarkdownOnlyChangeSkipsEveryLensThatJudgesCode(string lens)
    {
        ArbiterFactSet facts = ReviewFacts(lens, MarkdownOnlyPaths, MarkdownOnlyPaths, hunksPresent: true);

        ArbiterRuleAnswer answer = StepZeroAnswer(facts, "lens.spawn", NoCodeChangeRule);

        Assert.Equal(NoCodeChangeAnswer, answer.Answer);
        Assert.Equal(RuleEffects.Skip, answer.Effect);
    }

    /// <summary>
    /// Security, documentation-accuracy and blast-radius-revertibility keep running on the same
    /// change: the rule answers for them, and never with a skip.
    /// </summary>
    /// <param name="lens">A lens the rule must never skip.</param>
    [Theory]
    [MemberData(nameof(NeverSkippedLensNames))]
    public void MarkdownOnlyChangeNeverSkipsSecurityDocumentationOrBlastRadius(string lens)
    {
        ArbiterFactSet facts = ReviewFacts(lens, MarkdownOnlyPaths, MarkdownOnlyPaths, hunksPresent: true);

        ArbiterRuleAnswer answer = StepZeroAnswer(facts, "lens.spawn", NoCodeChangeRule);

        Assert.NotEqual(NoCodeChangeAnswer, answer.Answer);
        Assert.NotEqual(RuleEffects.Skip, answer.Effect);
    }

    /// <summary>
    /// A code line changed in the same review keeps the rule undecidable, so the lens is allowed.
    /// </summary>
    [Fact]
    public void ACodeLineChangeIsUndecidableAndAllowed()
    {
        ArbiterFactSet facts = ReviewFacts("correctness", MixedPaths, MarkdownOnlyPaths, hunksPresent: true);

        ArbiterRuleAnswer answer = StepZeroAnswer(facts, "lens.spawn", NoCodeChangeRule);

        Assert.Equal(RuleEngine.Undecidable, answer.Answer);
        Assert.Equal(RuleEffects.Allow, answer.Effect);
    }

    /// <summary>
    /// A path in a language the rule does not know cannot be shown to be no-code. The no-code set
    /// is present but empty here, so the case differs from the absent-hunks case: the hunks are
    /// there, and the rule still has no evidence that the path is no-code.
    /// </summary>
    [Fact]
    public void AnUnknownLanguageIsUndecidableAndAllowed()
    {
        ArbiterFactSet facts = ReviewFacts("correctness", UnknownLanguagePaths, NoPaths, hunksPresent: true);

        ArbiterRuleAnswer answer = StepZeroAnswer(facts, "lens.spawn", NoCodeChangeRule);

        Assert.Equal(RuleEngine.Undecidable, answer.Answer);
        Assert.Equal(RuleEffects.Allow, answer.Effect);
    }

    /// <summary>
    /// Without hunks the rule has no evidence either way, so it is undecidable and the lens is
    /// allowed.
    /// </summary>
    [Fact]
    public void AbsentHunksAreUndecidableAndAllowed()
    {
        ArbiterFactSet facts = ReviewFacts("correctness", MarkdownOnlyPaths, null, hunksPresent: false);

        ArbiterRuleAnswer answer = StepZeroAnswer(facts, "lens.spawn", NoCodeChangeRule);

        Assert.Equal(RuleEngine.Undecidable, answer.Answer);
        Assert.Equal(RuleEffects.Allow, answer.Effect);
    }

    /// <summary>
    /// The rule has no step-1 ask, so an undecidable answer on a code change never reaches the
    /// provider: no provider batch names it. The evaluation must also complete without an error,
    /// otherwise the absence of the rule from the batch proves nothing.
    /// </summary>
    [Fact]
    public async Task ACodeLineChangeNeverAsksTheProviderForTheRule()
    {
        Assert.Contains(
            ArbiterConfig.ProductDefaults.Rules,
            rule => string.Equals(rule.Id, NoCodeChangeRule, StringComparison.Ordinal));

        RecordingProvider provider = new();
        List<(string Name, object Value)> review = ReviewPairs(MixedPaths, MarkdownOnlyPaths, hunksPresent: true);
        ArbiterEvaluator evaluator = new(
            provider,
            _ => null,
            new InFlightLedger(ArbiterDirectory),
            new DecisionLog(ArbiterDirectory),
            new FixedReviewFacts(review),
            new PassThroughPlanReader(),
            TimeProvider.System);
        KyberWeaveConfig config = new()
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = ArbiterConfig.ProductDefaults.Rules },
        };

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(LensEvent("correctness"), config);

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.DoesNotContain(NoCodeChangeRule, provider.RuleIdsAsked);
    }

    /// <summary>
    /// A finding on one of its changed lines is <c>in-hunk</c> and allowed. The hunk fact is
    /// absent, so this fails while the rule still reads <c>review.changed-hunks</c>.
    /// </summary>
    [Fact]
    public void AFindingOnAChangedLineIsInHunkAndAllowed()
    {
        ArbiterFactSet facts = new ArbiterFactSet()
            .With("finding.line", 7, ArbiterFactLabel.Asserted)
            .With("finding.changed-lines", ChangedLines, ArbiterFactLabel.Derived);

        ArbiterRuleAnswer answer = StepZeroAnswer(facts, "lens.returned", PreExistingRule);

        Assert.Equal("in-hunk", answer.Answer);
        Assert.Equal(RuleEffects.Allow, answer.Effect);
    }

    /// <summary>
    /// A finding off its changed lines is <c>outside-hunk</c> and annotated. The hunk fact is
    /// absent, so this fails while the rule still reads <c>review.changed-hunks</c>.
    /// </summary>
    [Fact]
    public void AFindingOffTheChangedLinesIsOutsideHunkAndAnnotated()
    {
        ArbiterFactSet facts = new ArbiterFactSet()
            .With("finding.line", 5, ArbiterFactLabel.Asserted)
            .With("finding.changed-lines", ChangedLines, ArbiterFactLabel.Derived);

        ArbiterRuleAnswer answer = StepZeroAnswer(facts, "lens.returned", PreExistingRule);

        Assert.Equal("outside-hunk", answer.Answer);
        Assert.Equal(RuleEffects.Annotate, answer.Effect);
    }

    private static ArbiterEvent LensEvent(string lens) =>
        new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "review-lens",
            Prompt = $"KYBER-ARBITER: true\nLENS: {lens}\n\nApply the lens to the change.",
            HarnessCaller = "code-reviewer",
            IsDispatch = true,
        };

    /// <summary>Records the rule ids each step-1 batch names, and answers none of them.</summary>
    private sealed class RecordingProvider : IArbiterProvider
    {
        private readonly List<string> _ruleIds = [];

        public IReadOnlyList<string> RuleIdsAsked => _ruleIds;

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            _ruleIds.AddRange(rules.Select(rule => rule.Id));
            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, null, null, false));
        }
    }

    /// <summary>
    /// Adds the review facts the case declares to the facts the builder already produced, so a
    /// fault elsewhere in the evaluation is not hidden by a replacement fact set.
    /// </summary>
    private sealed class FixedReviewFacts : IArbiterGitFacts
    {
        private readonly List<(string Name, object Value)> _pairs;

        public FixedReviewFacts(List<(string Name, object Value)> pairs)
        {
            _pairs = pairs;
        }

        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) =>
            ApplyPairs(facts, _pairs);
    }

    private sealed class PassThroughPlanReader : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) => facts;
    }
}
