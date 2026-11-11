using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 2.3: the two shipped model-targeting rules on <c>delegate.model</c>.
/// RED: <c>KW-ARB-DIFFICULTY-001</c>, <c>KW-ARB-LADDER-001</c> and the mode mapping that turns
/// <c>arbiter.model-targeting.mode</c> into their enabled and shadow state do not exist yet, so
/// no decision record carries them.
/// </summary>
/// <remarks>
/// The secondary is reached through the evaluator's <c>secondaryTriggers</c> seam, so these tests
/// assert on the <c>delegate.model</c> decision record the evaluator writes. The ladder profile
/// precedence (item 3) is observed on the record's <c>model</c> block, which task 2.5 writes.
/// </remarks>
public sealed class ArbiterModelTargetRulesTests : IDisposable
{
    private const string DifficultyRule = "KW-ARB-DIFFICULTY-001";
    private const string LadderRule = "KW-ARB-LADDER-001";
    private const string ScopeRule = "KW-ARB-SCOPE-002";
    private const string Plan = "docs/plans/x.md";
    private const string Trigger = "delegate.model";
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";

    private static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private string ArbiterDirectory => Path.Combine(_repo.Path, "artifacts", "arbiter");

    /// <summary>Answers the rules it is told about, with optional scores; a rule it is not told about gets no answer.</summary>
    private sealed class ScriptedProvider : IArbiterProvider
    {
        public IReadOnlyDictionary<string, string> Answers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, ArbiterStep1Score>? Scores { get; init; }

        public Exception? Fault { get; init; }

        public int CallCount { get; private set; }

        /// <summary>Every rule id the provider was asked a question about, across all calls.</summary>
        public List<string> AskedRuleIds { get; } = [];

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            AskedRuleIds.AddRange(rules.Select(rule => rule.Id));
            // The fault is the difficulty provider's, so it is raised only for the secondary's question. The
            // primary's scope question is answered, so the primary still allows and the secondary is reached.
            if (Fault is not null && rules.Any(rule => rule.Id == DifficultyRule))
            {
                throw Fault;
            }

            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                if (Answers.TryGetValue(rule.Id, out string? answer))
                {
                    answers[rule.Id] = answer;
                }
            }

            // The primary's scope question: the delegation asks only for the plan task's work, so the primary allows.
            if (rules.Any(rule => rule.Id == ScopeRule))
            {
                answers[ScopeRule] = "within-task";
            }

            return Task.FromResult(new ArbiterStep1BatchResult(
                answers,
                "stub",
                null,
                RequestSent: true,
                Scores is null ? null : Scores.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
        }
    }

    /// <summary>The plan stands in for a free T2 owned by csharp-dev with one implementation file.</summary>
    private sealed class PlanFacts : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            facts
                .With("plan.exists", true, ArbiterFactLabel.Derived)
                .With("plan.tasks.count", 3, ArbiterFactLabel.Derived)
                .With("plan.development-mode", "standard", ArbiterFactLabel.Derived)
                .With("plan.task", "T2", ArbiterFactLabel.Derived)
                .With("plan.task.skills", new List<string> { "csharp-dev" }, ArbiterFactLabel.Derived)
                .With("plan.task.files", new List<string> { "src/A.cs" }, ArbiterFactLabel.Derived)
                .With("plan.task.depends-on", new List<string>(), ArbiterFactLabel.Derived)
                .With("plan.test-contract.row", "A failing test", ArbiterFactLabel.Derived);
    }

    private sealed class NoGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    private static ArbiterConfig Shipped(string mode) => ArbiterConfig.ProductDefaults with
    {
        ModelTargeting = new ArbiterModelTargetingConfig(
            mode,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["easy"] = "fast",
                ["standard"] = "general",
                ["hard"] = "deep-planning",
            },
            ["fast", "general", "deep-planning"]),
    };

    private static KyberWeaveConfig Config(string mode = "shadow") => new()
    {
        Arbiter = Shipped(mode) with { Enabled = true },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private ArbiterEvaluator Evaluator(IArbiterProvider provider) => new(
        provider,
        _ => null,
        new InFlightLedger(ArbiterDirectory),
        new DecisionLog(ArbiterDirectory),
        new NoGitFacts(),
        new PlanFacts(),
        TimeProvider.System,
        secondaryTriggers: (primary, ev) => string.Equals(primary, "delegate", StringComparison.Ordinal)
            ? [Trigger]
            : []);

    private ArbiterEvent Dispatch(bool observesReturns = true) => new()
    {
        Harness = "claude",
        Phase = "pre",
        Target = "csharp-dev",
        Prompt = Prompt,
        HarnessCaller = "conductor",
        Session = "s1",
        Cwd = _repo.Path,
        ObservesReturns = observesReturns,
        IsDispatch = true,
    };

    private async Task<ArbiterDecisionRecord> EvaluateAndReadSecondaryAsync(
        IArbiterProvider provider,
        string mode = "shadow",
        bool observesReturns = true)
    {
        await Evaluator(provider).EvaluateAsync(
            Dispatch(observesReturns), Config(mode), null, TestContext.Current.CancellationToken);

        IReadOnlyList<ArbiterDecisionRecord> records = new DecisionLog(ArbiterDirectory).ReadAll();
        ArbiterDecisionRecord primary = Assert.Single(records, record => record.Trigger == "delegate");
        string blockers = string.Join(", ", primary.Rules.Where(rule => rule.Effect != "allow").Select(rule => $"{rule.Id}={rule.Answer}"));
        Assert.True(primary.Outcome == "allow", $"fixture precondition: primary outcome {primary.Outcome}: {blockers}");
        return Assert.Single(records, record => record.Trigger == Trigger);
    }

    private static ArbiterDecisionRule RuleOf(ArbiterDecisionRecord record, string id) =>
        Assert.Single(record.Rules, rule => rule.Id == id);

    private async Task SeedAttemptAsync(string profile, string result, bool recordProfile = true)
    {
        InFlightLedger ledger = new(ArbiterDirectory);
        await ledger.AppendAsync(new ArbiterLedgerEvent("pre-1", At, "hook", "claude", null, ArbiterLedgerPhases.Pre)
        {
            Trigger = "delegate",
            Target = "csharp-dev",
            Headers = new ArbiterLedgerHeaders(PlanFile: Plan, Task: "T2"),
            ModelProfile = recordProfile ? profile : null,
            ModelSource = recordProfile ? "model-targets" : null,
        });
        await ledger.AppendAsync(new ArbiterLedgerEvent("post-1", At, "hook", "claude", null, ArbiterLedgerPhases.Post)
        {
            PreId = "pre-1",
            Returns = new ArbiterReturnMarkers(TaskReview: result),
        });
    }

    private void WriteModelTargetsTable()
    {
        Directory.CreateDirectory(ArbiterDirectory);
        ArbiterModelTargets table = new(
            ArbiterModelTargets.SchemaName,
            "claude",
            new ArbiterModelTargetsPreset("work", "d"),
            "b",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["fast"] = "t-fast",
                ["general"] = "t-general",
                ["deep-planning"] = "t-deep",
            },
            new Dictionary<string, ArbiterModelTargetsAgent>(StringComparer.Ordinal)
            {
                ["csharp-dev"] = new("general", "t-general"),
            });
        File.WriteAllText(
            Path.Combine(ArbiterDirectory, "model-targets.claude.json"),
            System.Text.Json.JsonSerializer.Serialize(table));
    }

    // 1. KW-ARB-DIFFICULTY-001

    [Theory]
    [InlineData("easy")]
    [InlineData("standard")]
    [InlineData("hard")]
    public async Task Difficulty_AnsweredWithAnyDifficultyAboveTheFloor_Retargets(string answer)
    {
        ScriptedProvider provider = new()
        {
            Answers = new Dictionary<string, string>(StringComparer.Ordinal) { [DifficultyRule] = answer },
            Scores = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [DifficultyRule] = new([0.9, 0.05, 0.05], 0.9, 0.8),
            },
        };

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider, mode: "enforce");

        Assert.Equal(RuleEffects.Retarget, RuleOf(record, DifficultyRule).Effect);
    }

    [Fact]
    public async Task Difficulty_BelowTheFloor_Keeps()
    {
        ScriptedProvider provider = new()
        {
            Answers = new Dictionary<string, string>(StringComparer.Ordinal) { [DifficultyRule] = "hard" },
            Scores = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [DifficultyRule] = new([0.5, 0.3, 0.2], 0.5, 0.8),
            },
        };

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider, mode: "enforce");

        Assert.Equal(RuleEffects.Keep, RuleOf(record, DifficultyRule).Effect);
    }

    [Theory]
    [InlineData("undecidable")]
    [InlineData("none")]
    public async Task Difficulty_UndecidableOrNoProvider_Keeps(string answer)
    {
        ScriptedProvider provider = new()
        {
            Answers = new Dictionary<string, string>(StringComparer.Ordinal) { [DifficultyRule] = answer },
        };

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider, mode: "enforce");

        Assert.Equal(RuleEffects.Keep, RuleOf(record, DifficultyRule).Effect);
    }

    [Fact]
    public async Task Difficulty_ProviderError_Keeps()
    {
        ScriptedProvider provider = new() { Fault = new HttpRequestException("provider down") };

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider, mode: "enforce");

        Assert.Equal(RuleEffects.Keep, RuleOf(record, DifficultyRule).Effect);
    }

    // 2. KW-ARB-LADDER-001 (step 0, no provider call, in order)

    [Fact]
    public async Task Ladder_ReturnsUnobservable_KeepsWithoutAProviderCall()
    {
        ScriptedProvider provider = new();

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider, mode: "enforce", observesReturns: false);

        ArbiterDecisionRule rule = RuleOf(record, LadderRule);
        Assert.Equal("returns-unobservable", rule.Answer);
        Assert.Equal(RuleEffects.Keep, rule.Effect);
        Assert.DoesNotContain(LadderRule, provider.AskedRuleIds);
    }

    [Fact]
    public async Task Ladder_NoPriorAttempt_IsFirstAttempt()
    {
        ScriptedProvider provider = new();

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider, mode: "enforce");

        Assert.Equal("first-attempt", RuleOf(record, LadderRule).Answer);
        Assert.DoesNotContain(LadderRule, provider.AskedRuleIds);
    }

    [Fact]
    public async Task Ladder_PriorAttemptPassed_IsFirstAttempt()
    {
        await SeedAttemptAsync("fast", "PASS");

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(new ScriptedProvider(), mode: "enforce");

        Assert.Equal("first-attempt", RuleOf(record, LadderRule).Answer);
    }

    [Fact]
    public async Task Ladder_PriorProfileUnrecorded_IsProfileUnrecorded()
    {
        await SeedAttemptAsync(profile: string.Empty, "FAIL", recordProfile: false);

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(new ScriptedProvider(), mode: "enforce");

        Assert.Equal("profile-unrecorded", RuleOf(record, LadderRule).Answer);
    }

    [Fact]
    public async Task Ladder_PriorProfileIsTheTop_IsTopOfLadder()
    {
        await SeedAttemptAsync("deep-planning", "FAIL");

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(new ScriptedProvider(), mode: "enforce");

        Assert.Equal("top-of-ladder", RuleOf(record, LadderRule).Answer);
    }

    [Fact]
    public async Task Ladder_PriorFailedBelowTheTop_StepsUpAndRetargets()
    {
        await SeedAttemptAsync("fast", "FAIL");
        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(new ScriptedProvider(), mode: "enforce");

        ArbiterDecisionRule rule = RuleOf(record, LadderRule);
        Assert.Equal("step-up", rule.Answer);
        Assert.Equal(RuleEffects.Retarget, rule.Effect);
    }

    // 3. When both retarget, the ladder's profile wins.

    [Fact]
    public async Task BothRetarget_LadderProfileWins()
    {
        await SeedAttemptAsync("fast", "FAIL");
        WriteModelTargetsTable();
        ScriptedProvider provider = new()
        {
            Answers = new Dictionary<string, string>(StringComparer.Ordinal) { [DifficultyRule] = "hard" },
            Scores = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [DifficultyRule] = new([0.05, 0.05, 0.9], 0.9, 0.8),
            },
        };

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider, mode: "enforce");

        Assert.Equal(RuleEffects.Retarget, RuleOf(record, DifficultyRule).Effect);
        Assert.Equal(RuleEffects.Retarget, RuleOf(record, LadderRule).Effect);
        Assert.Equal(RuleEffects.Retarget, record.Outcome);
        Assert.NotNull(record.Model);
        Assert.Equal("general", record.Model!.Profile);
        Assert.Equal("ladder", record.Model.Source);
    }

    // 4. model-targeting.mode mapping

    [Fact]
    public async Task ModeOff_DisablesBothRules()
    {
        await SeedAttemptAsync("fast", "FAIL");
        ScriptedProvider provider = new()
        {
            Answers = new Dictionary<string, string>(StringComparer.Ordinal) { [DifficultyRule] = "hard" },
        };

        await Evaluator(provider).EvaluateAsync(Dispatch(), Config("off"), null, TestContext.Current.CancellationToken);

        IReadOnlyList<ArbiterDecisionRecord> records = new DecisionLog(ArbiterDirectory).ReadAll();
        string[] ruleIds = [DifficultyRule, LadderRule];
        Assert.DoesNotContain(records.SelectMany(record => record.Rules), rule => ruleIds.Contains(rule.Id));
    }

    [Fact]
    public async Task ModeShadow_EvaluatesAndLogsWithoutEffecting()
    {
        await SeedAttemptAsync("fast", "FAIL");
        ScriptedProvider provider = new()
        {
            Answers = new Dictionary<string, string>(StringComparer.Ordinal) { [DifficultyRule] = "hard" },
            Scores = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [DifficultyRule] = new([0.05, 0.05, 0.9], 0.9, 0.8),
            },
        };

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider, mode: "shadow");

        ArbiterDecisionRule difficulty = RuleOf(record, DifficultyRule);
        Assert.True(difficulty.Shadow);
        Assert.Equal(RuleEffects.Retarget, difficulty.WouldEffect);
        Assert.Equal(RuleEffects.Keep, difficulty.Effect);

        ArbiterDecisionRule ladder = RuleOf(record, LadderRule);
        Assert.True(ladder.Shadow);
        Assert.Equal(RuleEffects.Retarget, ladder.WouldEffect);
        Assert.Equal(RuleEffects.Keep, record.Outcome);
    }

    [Fact]
    public async Task ModeEnforce_CombinedOutcomeRetargets()
    {
        await SeedAttemptAsync("fast", "FAIL");
        ScriptedProvider provider = new()
        {
            Answers = new Dictionary<string, string>(StringComparer.Ordinal) { [DifficultyRule] = "hard" },
            Scores = new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)
            {
                [DifficultyRule] = new([0.05, 0.05, 0.9], 0.9, 0.8),
            },
        };

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider, mode: "enforce");

        Assert.Equal(RuleEffects.Retarget, record.Outcome);
    }

    // 5. The shipped id set gains both ids: covered in ArbiterDefaultRulesTests.ExpectedRuleIds.
}
