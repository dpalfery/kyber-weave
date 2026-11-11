using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.5: secondary triggers evaluate behind the primary dispatch.
/// RED: <c>ArbiterEvaluator</c> has no <c>secondaryTriggers</c> parameter, and
/// <c>ArbiterDispatchEvaluation.Secondary</c>, <c>ArbiterDecisionRule.Shadow</c>,
/// <c>ArbiterDecisionRule.WouldEffect</c>, <c>ArbiterDecisionRecord.PrimaryLedgerId</c>
/// and the new catalog triggers do not exist yet.
/// </summary>
public sealed class ArbiterSecondaryTriggerTests : IDisposable
{
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
    private const string PrimaryTrigger = "delegate";
    private const string SecondaryTrigger = "delegate.model";
    private const string PrimaryRuleId = "KW-ARB-ASK-001";
    private const string SecondaryRuleId = "KW-ARB-MODEL-001";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-secondary-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    /// <summary>Answers per rule id, records every call's rule ids, and can fail or report over-budget.</summary>
    private sealed class ScriptedProvider : IArbiterProvider
    {
        private readonly List<IReadOnlyList<string>> _calls = [];

        public IReadOnlyList<IReadOnlyList<string>> Calls => _calls;

        public IReadOnlyDictionary<string, string> AnswerByRuleId { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>A batch larger than this reports over-budget with no answers (<c>RequestSent</c> false).</summary>
        public int? MaxRulesPerCall { get; init; }

        /// <summary>Raises for a batch when it returns an exception for those rules.</summary>
        public Func<IReadOnlyList<ArbiterRule>, Exception?>? Fault { get; init; }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            _calls.Add(rules.Select(rule => rule.Id).ToArray());
            Exception? fault = Fault?.Invoke(rules);
            if (fault is not null)
            {
                throw fault;
            }

            if (MaxRulesPerCall is { } max && rules.Count > max)
            {
                return Task.FromResult(new ArbiterStep1BatchResult(
                    new Dictionary<string, string>(StringComparer.Ordinal), null, null, RequestSent: false));
            }

            Dictionary<string, string> answers = rules
                .Where(rule => AnswerByRuleId.ContainsKey(rule.Id))
                .ToDictionary(rule => rule.Id, rule => AnswerByRuleId[rule.Id], StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.13.0", null, RequestSent: true));
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

    /// <summary>The test-only secondary-trigger derivation: <c>delegate</c> carries one secondary trigger.</summary>
    private static IReadOnlyList<string> SecondaryFor(string primaryTrigger, ArbiterEvent ev) =>
        string.Equals(primaryTrigger, PrimaryTrigger, StringComparison.Ordinal) ? [SecondaryTrigger] : [];

    /// <remarks>
    /// The docs root is <c>docs</c>, matching the planning paths the prompt names.
    /// </remarks>
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

    private static ArbiterRule Step0Rule(string id, string trigger, string answer, string effect) =>
        new(
            id,
            trigger,
            "Step-0 question.",
            [answer],
            new Dictionary<string, string>(StringComparer.Ordinal) { [answer] = effect },
            [new RuleDecideClause(RulePredicate.Exists("caller", true), answer)]);

    private static ArbiterRule AskRule(string id, string trigger, string answer, string effect, bool shadow = false) =>
        new ArbiterRule(
            id,
            trigger,
            "Step-1 question.",
            [answer, "other"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [answer] = effect,
                ["other"] = effect,
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

    private ArbiterEvaluator CreateEvaluator(IArbiterProvider provider)
    {
        DateTimeOffset fixedNow = new(2025, 10, 1, 12, 0, 0, TimeSpan.Zero);
        return new ArbiterEvaluator(
            provider,
            _ => null,
            new InFlightLedger(ArbiterDirectory),
            new DecisionLog(ArbiterDirectory),
            new PassThroughGitFacts(),
            new PassThroughPlanReader(),
            new FixedClock(fixedNow),
            secondaryTriggers: SecondaryFor);
    }

    private IReadOnlyList<ArbiterDecisionRecord> Records() => new DecisionLog(ArbiterDirectory).ReadAll();

    // Behaviour 1: a primary step-0 short-circuit stops before any provider call or secondary work.
    [Fact]
    public async Task PrimaryStep0ShortCircuit_MakesNoProviderCallAndSkipsSecondary()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal) { [SecondaryRuleId] = "retarget" },
        };
        KyberWeaveConfig config = ConfigWith([
            Step0Rule("KW-ARB-SCOPE-001", PrimaryTrigger, "not-in-plan", RuleEffects.Escalate),
            AskRule(SecondaryRuleId, SecondaryTrigger, "retarget", RuleEffects.Retarget),
        ]);

        ArbiterEvaluationResult result = await CreateEvaluator(provider).EvaluateAsync(DelegateEvent(), config);

        Assert.Empty(provider.Calls);
        Assert.Equal(RuleEffects.Escalate, result.Outcome);
        Assert.Empty(result.Dispatches[0].Secondary);
        Assert.DoesNotContain(Records(), record => record.Trigger == SecondaryTrigger);
    }

    // Behaviour 2: otherwise exactly one provider call holds the primary and secondary rules.
    [Fact]
    public async Task OtherwiseOneProviderCallHoldsPrimaryAndSecondaryRules()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrimaryRuleId] = "within-task",
                [SecondaryRuleId] = "retarget",
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(PrimaryRuleId, PrimaryTrigger, "within-task", RuleEffects.Allow),
            AskRule(SecondaryRuleId, SecondaryTrigger, "retarget", RuleEffects.Retarget),
        ]);

        await CreateEvaluator(provider).EvaluateAsync(DelegateEvent(), config);

        Assert.Single(provider.Calls);
        Assert.Equal([PrimaryRuleId, SecondaryRuleId], provider.Calls[0].OrderBy(id => id, StringComparer.Ordinal));
    }

    // Behaviour 3: over budget with both sent, the secondary answers undecidable over-budget and the primary goes alone.
    [Fact]
    public async Task OverBudgetWithBoth_SecondaryUndecidableOverBudget_PrimarySentAlone()
    {
        ScriptedProvider provider = new()
        {
            MaxRulesPerCall = 1,
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrimaryRuleId] = "within-task",
                [SecondaryRuleId] = "retarget",
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(PrimaryRuleId, PrimaryTrigger, "within-task", RuleEffects.Allow),
            AskRule(SecondaryRuleId, SecondaryTrigger, "retarget", RuleEffects.Retarget),
        ]);

        await CreateEvaluator(provider).EvaluateAsync(DelegateEvent(), config);

        Assert.Equal([PrimaryRuleId], provider.Calls[^1]);
        ArbiterDecisionRecord secondary = Records().Single(record => record.Trigger == SecondaryTrigger);
        Assert.Equal(ArbiterProviderStatuses.OverBudget, secondary.Provider.Status);
        ArbiterDecisionRule rule = secondary.Rules.Single(entry => entry.Id == SecondaryRuleId);
        Assert.Equal(RuleEngine.Undecidable, rule.Answer);
    }

    // Behaviour 4: a non-allow primary records each secondary rule with the family's neutral effect.
    [Fact]
    public async Task PrimaryNotAllow_SecondaryRecordedNeutralWithWouldEffectAndSuppressedEvidence()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrimaryRuleId] = "bad",
                [SecondaryRuleId] = "retarget",
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(PrimaryRuleId, PrimaryTrigger, "bad", RuleEffects.Escalate),
            AskRule(SecondaryRuleId, SecondaryTrigger, "retarget", RuleEffects.Retarget),
        ]);

        ArbiterEvaluationResult result = await CreateEvaluator(provider).EvaluateAsync(DelegateEvent(), config);

        Assert.Equal(RuleEffects.Escalate, result.Outcome);
        ArbiterDecisionRule rule = Records().Single(record => record.Trigger == SecondaryTrigger).Rules.Single();
        Assert.Equal(RuleEffects.Keep, rule.Effect);
        Assert.Equal(RuleEffects.Retarget, rule.WouldEffect);
        Assert.Equal("suppressed: primary " + RuleEffects.Escalate, rule.Evidence);
    }

    // Behaviour 5: primary allow writes one record per secondary trigger, linked to the primary record.
    [Fact]
    public async Task PrimaryAllow_OneRecordPerSecondaryTriggerLinkedToPrimary()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrimaryRuleId] = "within-task",
                [SecondaryRuleId] = "retarget",
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(PrimaryRuleId, PrimaryTrigger, "within-task", RuleEffects.Allow),
            AskRule(SecondaryRuleId, SecondaryTrigger, "retarget", RuleEffects.Retarget),
        ]);

        ArbiterEvaluationResult result = await CreateEvaluator(provider).EvaluateAsync(DelegateEvent(), config);

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterDecisionRecord primary = Records().Single(record => record.Trigger == PrimaryTrigger);
        ArbiterDecisionRecord secondary = Records().Single(record => record.Trigger == SecondaryTrigger);
        Assert.Equal(primary.LedgerId, secondary.LedgerId);
        Assert.Equal(primary.LedgerId, secondary.PrimaryLedgerId);
        Assert.Null(primary.PrimaryLedgerId);
        Assert.Equal(RuleEffects.Allow, result.Dispatches[0].Outcome);
        ArbiterSecondaryEvaluation entry = Assert.Single(result.Dispatches[0].Secondary);
        Assert.Equal(SecondaryTrigger, entry.Trigger);
        Assert.Equal(RuleEffects.Retarget, entry.Outcome);
        Assert.NotNull(entry.Payload);
    }

    // Behaviour 6: a shadow secondary rule contributes the neutral effect and records its would-be effect.
    [Fact]
    public async Task ShadowSecondaryRule_ContributesNeutralEffectAndRecordsWouldEffect()
    {
        ScriptedProvider provider = new()
        {
            AnswerByRuleId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrimaryRuleId] = "within-task",
                [SecondaryRuleId] = "retarget",
            },
        };
        KyberWeaveConfig config = ConfigWith([
            AskRule(PrimaryRuleId, PrimaryTrigger, "within-task", RuleEffects.Allow),
            AskRule(SecondaryRuleId, SecondaryTrigger, "retarget", RuleEffects.Retarget, shadow: true),
        ]);

        ArbiterEvaluationResult result = await CreateEvaluator(provider).EvaluateAsync(DelegateEvent(), config);

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterSecondaryEvaluation entry = Assert.Single(result.Dispatches[0].Secondary);
        Assert.Equal(RuleEffects.Keep, entry.Outcome);
        ArbiterDecisionRule rule = Records().Single(record => record.Trigger == SecondaryTrigger).Rules.Single();
        Assert.True(rule.Shadow);
        Assert.Equal(RuleEffects.Keep, rule.Effect);
        Assert.Equal(RuleEffects.Retarget, rule.WouldEffect);
    }

    // Behaviour 7: a failing secondary trigger is caught and logged as undecidable, never as the hook error.
    [Fact]
    public async Task SecondaryTriggerException_CaughtAndLoggedUndecidable_NeverHookError()
    {
        ScriptedProvider provider = new()
        {
            Fault = rules => rules.Any(rule => rule.Id == SecondaryRuleId)
                ? new InvalidOperationException("secondary bug")
                : null,
        };
        KyberWeaveConfig config = ConfigWith([
            Step0Rule("KW-ARB-CALLER-001", PrimaryTrigger, "allow", RuleEffects.Allow),
            AskRule(SecondaryRuleId, SecondaryTrigger, "retarget", RuleEffects.Retarget),
        ]);

        ArbiterEvaluationResult result = await CreateEvaluator(provider).EvaluateAsync(DelegateEvent(), config);

        Assert.False(result.IsError);
        Assert.NotEqual(ArbiterEvaluator.HookErrorCode, result.ErrorCode);
        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterDecisionRecord secondary = Records().Single(record => record.Trigger == SecondaryTrigger);
        Assert.Equal(RuleEngine.Undecidable, secondary.Rules.Single().Answer);
    }

    // The catalog declares the new triggers and facts (task 1.5 catalog rows).
    [Fact]
    public void Catalog_DelegateModelDeclaresModelTargetFactsInModelTargetFamily()
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("delegate.model", out IReadOnlySet<string> declared));
        foreach (string fact in new[]
        {
            "plan.task.text", "plan.task.files", "plan.task.file-kinds", "ledger.task.prior-attempt",
            "model-targeting.ladder.next", "delegation.model", "harness.model-override", "harness.observes-returns",
        })
        {
            Assert.Contains(fact, declared);
        }

        Assert.Equal(ArbiterTriggerFamily.ModelTarget, ArbiterTriggerFamilies.ForTrigger("delegate.model"));
    }

    [Theory]
    [InlineData("intake.shortcut", "intake.request", ArbiterTriggerFamily.Shortcut)]
    [InlineData("triage.shortcut", "defect.report", ArbiterTriggerFamily.Shortcut)]
    [InlineData("docs-impact.shortcut", "docs.governed-paths-changed", ArbiterTriggerFamily.Shortcut)]
    [InlineData("delegate.failure", "failure.previous-text", ArbiterTriggerFamily.FailureCluster)]
    [InlineData("sections.select", "task.text", ArbiterTriggerFamily.Advice)]
    [InlineData("comment.triage", "comment.body", ArbiterTriggerFamily.Advice)]
    public void Catalog_NewTriggersDeclareTheirFactsAndFamily(string trigger, string fact, ArbiterTriggerFamily family)
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts(trigger, out IReadOnlySet<string> declared));
        Assert.Contains(fact, declared);
        Assert.Equal(family, ArbiterTriggerFamilies.ForTrigger(trigger));
    }

    [Fact]
    public void Catalog_ShortcutDocsImpactDeclaresDiffFacts()
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("docs-impact.shortcut", out IReadOnlySet<string> declared));
        Assert.Contains("review.changed-paths", declared);
        Assert.Contains("review.diff-summary", declared);
    }

    [Fact]
    public void Catalog_SectionsSelectDeclaresCandidateFacts()
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("sections.select", out IReadOnlySet<string> declared));
        Assert.Contains("section.candidates", declared);
        Assert.Contains("section.candidate.*", declared);
    }

    [Fact]
    public void Catalog_CommentTriageDeclaresCommentFacts()
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("comment.triage", out IReadOnlySet<string> declared));
        foreach (string fact in new[]
        {
            "comment.body", "comment.path", "comment.line", "comment.author", "file.text-at-line", "comment.fixing-commits",
        })
        {
            Assert.Contains(fact, declared);
        }
    }

    [Fact]
    public void Catalog_DelegateFailureDeclaresClusterFacts()
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("delegate.failure", out IReadOnlySet<string> declared));
        Assert.Contains("failure.previous-text", declared);
        Assert.Contains("failure.current-text", declared);
        Assert.Contains("ledger.task.prior-attempt", declared);
    }

    [Fact]
    public void Catalog_LensSpawnGainsReviewPathFacts()
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("lens.spawn", out IReadOnlySet<string> declared));
        Assert.Contains("review.comment-only-paths", declared);
        Assert.Contains("review.rename-only-paths", declared);
        Assert.Contains("review.no-code-change-paths", declared);
    }

    [Fact]
    public void Catalog_LensReturnedGainsFindingFacts()
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("lens.returned", out IReadOnlySet<string> declared));
        Assert.Contains("finding.changed-lines", declared);
        Assert.Contains("finding.severity", declared);
        Assert.Contains("finding.gate-corroborated", declared);
    }

    [Fact]
    public void Catalog_RefuteSpawnGainsDroppedAndPriorFindingFacts()
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("refute.spawn", out IReadOnlySet<string> declared));
        Assert.Contains("finding.dropped", declared);
        Assert.Contains("review.prior-findings", declared);
    }
}
