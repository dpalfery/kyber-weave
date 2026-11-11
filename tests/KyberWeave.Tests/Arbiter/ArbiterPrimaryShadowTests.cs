using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for the shadow state of a primary rule: a shadow rule is log-only, whichever trigger it
/// sits on. A primary shadow rule contributes the family's neutral effect and records what it would
/// have decided, so an enforced answer that would deny or skip never denies or skips while the rule is
/// still in shadow.
/// </summary>
/// <remarks>
/// Secondary rules already behave this way (see <c>ArbiterSecondaryTriggerTests</c>). The primary
/// path does not read <c>Shadow</c> at all, so both tests below fail on their outcome assertions.
/// </remarks>
public sealed class ArbiterPrimaryShadowTests : IDisposable
{
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
    private const string LensPrompt = "KYBER-ARBITER: true\nLENS: correctness\n\nApply the lens to the change.";
    private const string DelegateTrigger = "delegate";
    private const string ChoiceRuleId = "KW-ARB-ASK-001";
    private const string LensRuleId = "KW-ARB-LENS-002";

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string ArbiterDirectory => Path.Combine(_temp.Path, "artifacts", "arbiter");

    /// <summary>Answers per rule id from a fixed script; the step-1 questions are the only provider calls.</summary>
    private sealed class ScriptedProvider : IArbiterProvider
    {
        public IReadOnlyDictionary<string, string> AnswerByRuleId { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> answers = rules
                .Where(rule => AnswerByRuleId.ContainsKey(rule.Id))
                .ToDictionary(rule => rule.Id, rule => AnswerByRuleId[rule.Id], StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.13.0", null, RequestSent: true));
        }
    }

    /// <summary>Adds the review facts a git read would add to a lens spawn: a Markdown-only change.</summary>
    private sealed class MarkdownOnlyGitFacts : IArbiterGitFacts
    {
        private static readonly string[] MarkdownPaths = ["docs/guide.md"];

        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) =>
            facts.With("review.changed-paths", MarkdownPaths, ArbiterFactLabel.Asserted)
                .With("review.no-code-change-paths", MarkdownPaths, ArbiterFactLabel.Asserted);
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

    private ArbiterEvaluator CreateEvaluator(IArbiterProvider provider, IArbiterGitFacts gitFacts)
    {
        DateTimeOffset fixedNow = new(2025, 10, 1, 12, 0, 0, TimeSpan.Zero);
        return new ArbiterEvaluator(
            provider,
            _ => null,
            new InFlightLedger(ArbiterDirectory),
            new DecisionLog(ArbiterDirectory),
            gitFacts,
            new PassThroughPlanReader(),
            new FixedClock(fixedNow));
    }

    private IReadOnlyList<ArbiterDecisionRecord> Records() => new DecisionLog(ArbiterDirectory).ReadAll();

    private static KyberWeaveConfig ConfigWith(IReadOnlyList<ArbiterRule> rules) =>
        new()
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = rules },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };

    private static ArbiterEvent DelegateEvent() =>
        new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "csharp-dev",
            Prompt = Prompt,
            HarnessCaller = "conductor",
            IsDispatch = true,
        };

    private static ArbiterEvent LensEvent() =>
        new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "review-lens",
            Prompt = LensPrompt,
            HarnessCaller = "code-reviewer",
            IsDispatch = true,
        };

    /// <summary>A step-1 choice rule on <c>delegate</c> whose <c>bad</c> answer maps to <paramref name="effect"/>.</summary>
    private static ArbiterRule ChoiceRule(string id, string answer, string effect, bool shadow) =>
        new ArbiterRule(
            id,
            DelegateTrigger,
            "Step-1 question.",
            [answer, "other"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [answer] = effect,
                ["other"] = RuleEffects.Allow,
            },
            null,
            new RuleAsk(
                "choice",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = "delegation.task" },
                Criteria: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [answer] = "criterion",
                    ["other"] = "other criterion",
                }))
        with
        { Shadow = shadow };

    /// <summary>
    /// A shadow choice rule on a primary trigger is log-only: its <c>escalate</c> answer does not
    /// escalate the dispatch, the dispatch takes the family's neutral <c>allow</c>, and the record keeps
    /// the <c>escalate</c> that the rule would have applied.
    /// </summary>
    [Fact]
    public async Task ShadowPrimaryChoiceRule_DoesNotEscalateAndRecordsWouldEffect()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal) { [ChoiceRuleId] = "bad" },
        };
        KyberWeaveConfig config = ConfigWith([
            ChoiceRule(ChoiceRuleId, "bad", RuleEffects.Escalate, shadow: true),
        ]);

        ArbiterEvaluationResult result = await CreateEvaluator(provider, new PassThroughGitFacts())
            .EvaluateAsync(DelegateEvent(), config);

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterDecisionRule rule = Records()
            .Single(record => record.Trigger == DelegateTrigger)
            .Rules.Single(entry => entry.Id == ChoiceRuleId);
        Assert.True(rule.Shadow, "the rule is declared shadow, so its record must say so.");
        Assert.Equal(RuleEffects.Allow, rule.Effect);
        Assert.Equal(RuleEffects.Escalate, rule.WouldEffect);
    }

    /// <summary>
    /// A host override that sets <c>shadow: true</c> on the shipped step-0 rule <c>KW-ARB-LENS-002</c>
    /// makes the skip log-only: the lens runs, and the record keeps the <c>skip</c> it would have applied.
    /// The control test below shows the same change skips the lens when the override is absent.
    /// </summary>
    [Fact]
    public async Task ShadowOverrideOnNoCodeLensRule_DoesNotSkipTheLensAndRecordsWouldEffect()
    {
        KyberWeaveConfig config = LensOnlyConfig(shadow: true);

        ArbiterEvaluationResult result = await CreateEvaluator(new ScriptedProvider(), new MarkdownOnlyGitFacts())
            .EvaluateAsync(LensEvent(), config);

        Assert.NotEqual(RuleEffects.Skip, result.Outcome);
        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterDecisionRule rule = Records()
            .Single(record => record.Trigger == "lens.spawn")
            .Rules.Single(entry => entry.Id == LensRuleId);
        Assert.True(rule.Shadow, "the overridden rule is shadow, so its record must say so.");
        Assert.Equal(RuleEffects.Skip, rule.WouldEffect);
    }

    /// <summary>
    /// Control for the test above: without the override the same Markdown-only lens spawn is skipped,
    /// so the failure above is about the shadow state and not about the fixture.
    /// </summary>
    [Fact]
    public async Task WithoutTheShadowOverride_NoCodeLensRuleSkipsTheLens()
    {
        KyberWeaveConfig config = LensOnlyConfig(shadow: false);

        ArbiterEvaluationResult result = await CreateEvaluator(new ScriptedProvider(), new MarkdownOnlyGitFacts())
            .EvaluateAsync(LensEvent(), config);

        Assert.Equal(RuleEffects.Skip, result.Outcome);
    }

    /// <summary>
    /// The shipped rules are loaded from YAML with the override, then narrowed to the one rule under test,
    /// so no other shipped rule can answer and change the outcome.
    /// </summary>
    private static KyberWeaveConfig LensOnlyConfig(bool shadow)
    {
        string yaml = "arbiter:\n  enabled: true\n" + (shadow
            ? "  rules:\n    - id: KW-ARB-LENS-002\n      shadow: true\n"
            : string.Empty);
        KyberWeaveConfig loaded = KyberWeaveConfigLoader.LoadFromYaml(yaml);
        List<ArbiterRule> lensOnly = loaded.Arbiter.Rules
            .Where(rule => string.Equals(rule.Id, LensRuleId, StringComparison.Ordinal))
            .ToList();
        return new KyberWeaveConfig
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = lensOnly },
            Ontology = loaded.Ontology,
        };
    }
}
