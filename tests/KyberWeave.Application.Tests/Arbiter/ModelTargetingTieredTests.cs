using KyberWeave.Tests.Fakes;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The model-targeting rules follow the three developer tiers. The difficulty comes from the rating the task file
/// records, so the Arbiter never asks a second time; the ladder climbs the tiers; and a dispatch the conductor
/// already put on the next tier is left as it is. There is one roster, so these are the rules for every run.
/// </summary>
public sealed class ModelTargetingTieredTests
{
    private const string DifficultyRule = "KW-ARB-DIFFICULTY-001";
    private const string LadderRule = "KW-ARB-LADDER-001";
    private const string ScopeRule = "KW-ARB-SCOPE-002";
    private const string Plan = "docs/plans/x.md";
    private const string Trigger = "delegate.model";
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";

    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryFileSystem _files = new();
    private readonly InMemoryArbiterState _state;
    private readonly string _repoPath = ArbiterTestRoots.Under("repo");

    public ModelTargetingTieredTests()
    {
        _state = new InMemoryArbiterState(_files);
        WritePlan(difficulty: "medium");
    }

    private string ArbiterDirectory => Path.Combine(_repoPath, "artifacts", "arbiter");

    /// <summary>A plan whose T2 is the task the dispatch names, with an optional recorded difficulty.</summary>
    private void WritePlan(string? difficulty)
    {
        _files.CreateDirectory(Path.Combine(_repoPath, "docs", "plans"));
        string rating = difficulty is null ? string.Empty : $"\n- **Difficulty:** {difficulty}";
        _files.WriteAllText(
            Path.Combine(_repoPath, Plan),
            $$"""
            ---
            id: plans/x
            title: X
            doc-type: plan
            status: current
            owner: Maintainers
            last-reviewed: 2026-11-17
            ---

            # X

            **Status: Ready.**

            ## Tasks

            ### T2: Add the reader

            - **Files:** `src/A.cs`
            - **Depends on:** none{{rating}}

            Implement T2.
            """);
    }

    /// <summary>Answers the rules it is told about; a rule it is not told about gets no answer.</summary>
    private sealed class ScriptedProvider : IArbiterProvider
    {
        public IReadOnlyDictionary<string, string> Answers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, ArbiterStep1Score>? Scores { get; init; }

        public List<string> AskedRuleIds { get; } = [];

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            AskedRuleIds.AddRange(rules.Select(rule => rule.Id));
            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                if (Answers.TryGetValue(rule.Id, out string? answer))
                {
                    answers[rule.Id] = answer;
                }
            }

            if (rules.Any(rule => rule.Id == ScopeRule))
            {
                answers[ScopeRule] = "within-task";
            }

            return Task.FromResult(new ArbiterStep1BatchResult(
                answers,
                "stub",
                null,
                RequestSent: true,
                Scores is null
                    ? null
                    : Scores.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
        }
    }

    private sealed class NoGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    /// <summary>The real plan reader, over a plan file written under the temporary repository.</summary>
    private sealed class RealPlanReader(IPlanFacts facts, string root) : IArbiterPlanReader
    {
        private readonly IPlanFacts _facts = facts;

        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            _facts.Enrich(facts, classification, root);
    }

    private static KyberWeaveConfig Config(string roster, string mode = "enforce") => new()
    {
        Arbiter = ArbiterConfigDefaults.Product with
        {
            Enabled = true,
            ModelTargeting = ArbiterConfigDefaults.Product.ModelTargeting with { Mode = mode },
        },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        WorkerRoster = roster,
    };

    private ArbiterEvaluator Evaluator(IArbiterProvider provider) => new(
        provider,
        (_, _) => Task.FromResult<string?>(null),
        _state.Ledger(ArbiterDirectory),
        _state.Decisions(ArbiterDirectory),
        new NoGitFacts(),
        new RealPlanReader(_state.PlanFacts(), _repoPath),
        new FileArbiterModelTargetsReader(_files),
        _state.GateReports(),
        _state.Factory(),
        new FakeTimeProvider(At), new FakeIdGenerator(),
        secondaryTriggers: (primary, ev) => string.Equals(primary, "delegate", StringComparison.Ordinal)
            ? [Trigger]
            : []);

    private ArbiterEvent Dispatch(string target) => new()
    {
        Harness = "claude",
        Phase = "pre",
        Target = target,
        Prompt = Prompt,
        HarnessCaller = "conductor",
        Session = "s1",
        Cwd = _repoPath,
        ObservesReturns = true,
        IsDispatch = true,
    };

    private async Task<ArbiterDecisionRecord> EvaluateAndReadSecondaryAsync(
        IArbiterProvider provider,
        string roster = WorkerRosters.Tiered,
        string mode = "enforce",
        string target = "dev-hard")
    {
        await Evaluator(provider).EvaluateAsync(
            Dispatch(target), Config(roster, mode), null, TestContext.Current.CancellationToken);

        IReadOnlyList<ArbiterDecisionRecord> records = _state.Decisions(ArbiterDirectory).ReadAll();
        ArbiterDecisionRecord primary = Assert.Single(records, record => record.Trigger == "delegate");
        string blockers = string.Join(", ", primary.Rules.Where(rule => rule.Effect != "allow").Select(rule => $"{rule.Id}={rule.Answer}"));
        Assert.True(primary.Outcome == "allow", $"fixture precondition: primary outcome {primary.Outcome}: {blockers}");
        return Assert.Single(records, record => record.Trigger == Trigger);
    }

    private static ArbiterDecisionRule RuleOf(ArbiterDecisionRecord record, string id) =>
        Assert.Single(record.Rules, rule => rule.Id == id);

    private async Task SeedAttemptAsync(string profile, string result, string target)
    {
        InFlightLedger ledger = _state.Ledger(ArbiterDirectory);
        await ledger.AppendAsync(new ArbiterLedgerEvent("pre-1", At, "hook", "claude", null, ArbiterLedgerPhases.Pre)
        {
            Trigger = "delegate",
            Target = target,
            Headers = new ArbiterLedgerHeaders(PlanFile: Plan, Task: "T2"),
            ModelProfile = profile,
            ModelSource = "model-targets",
        });
        await ledger.AppendAsync(new ArbiterLedgerEvent("post-1", At, "hook", "claude", null, ArbiterLedgerPhases.Post)
        {
            PreId = "pre-1",
            Returns = new ArbiterReturnMarkers(TaskReview: result),
        });
    }

    /// <summary>The three tiers' model profiles, as the render writes them for this harness.</summary>
    private void WriteModelTargetsTable()
    {
        _files.CreateDirectory(ArbiterDirectory);
        ArbiterModelTargets table = new(
            ArbiterModelTargets.SchemaName,
            "claude",
            new ArbiterModelTargetsPreset("work", "d"),
            "b",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["fast"] = "t-fast",
                ["general"] = "t-general",
                ["dev-hard"] = "t-hard",
                ["deep-planning"] = "t-deep",
            },
            new Dictionary<string, ArbiterModelTargetsAgent>(StringComparer.Ordinal)
            {
                ["dev-easy"] = new("fast", "t-fast"),
                ["dev-medium"] = new("general", "t-general"),
                ["dev-hard"] = new("dev-hard", "t-hard"),
            });
        _files.WriteAllText(
            Path.Combine(ArbiterDirectory, "model-targets.claude.json"),
            System.Text.Json.JsonSerializer.Serialize(table));
    }

    // 1. The rating comes from the task file; no second call.

    [Theory]
    [InlineData("easy", "easy", "fast")]
    [InlineData("medium", "standard", "general")]
    [InlineData("hard", "hard", "dev-hard")]
    public async Task TheRecordedRatingAnswersTheDifficultyQuestionWithNoCall(
        string recorded,
        string answer,
        string profile)
    {
        WritePlan(recorded);
        WriteModelTargetsTable();
        ScriptedProvider provider = new();

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider);

        Assert.DoesNotContain(DifficultyRule, provider.AskedRuleIds);
        Assert.DoesNotContain(LadderRule, provider.AskedRuleIds);
        Assert.Equal(answer, RuleOf(record, DifficultyRule).Answer);
        Assert.Equal(RuleEffects.Retarget, RuleOf(record, DifficultyRule).Effect);
        Assert.NotNull(record.Model);
        Assert.Equal(profile, record.Model!.Profile);
        Assert.Equal("difficulty", record.Model.Source);
    }

    [Fact]
    public async Task ATaskWithNoRecordedRatingAsksNothingAndKeeps()
    {
        WritePlan(difficulty: null);
        ScriptedProvider provider = new();

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(provider);

        Assert.DoesNotContain(DifficultyRule, provider.AskedRuleIds);
        Assert.Equal(RuleEngine.Undecidable, RuleOf(record, DifficultyRule).Answer);
        Assert.Equal(RuleEffects.Keep, RuleOf(record, DifficultyRule).Effect);
    }

    // 2. The ladder climbs the tiers, and a dispatch already on the next tier is left as it is.

    [Fact]
    public async Task ADispatchAlreadyOnTheNextTierIsLeftAsItIs()
    {
        WriteModelTargetsTable();
        await SeedAttemptAsync("fast", "FAIL", "dev-easy");

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(new ScriptedProvider(), target: "dev-medium");

        Assert.Equal("step-up", RuleOf(record, LadderRule).Answer);
        Assert.NotNull(record.Model);
        Assert.False(record.Model!.Delivered);
        Assert.Equal(ModelTargetResolver.SameAsRendered, record.Model.Reason);
    }

    [Fact]
    public async Task TheLadderStepsUpFromOneTierToTheNext()
    {
        WriteModelTargetsTable();
        await SeedAttemptAsync("fast", "FAIL", "dev-easy");

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(new ScriptedProvider(), target: "dev-easy");

        Assert.NotNull(record.Model);
        Assert.Equal("general", record.Model!.Profile);
        Assert.Equal("ladder", record.Model.Source);
    }

    [Fact]
    public async Task TheTieredLadderClimbsFromTheMiddleTierToHard()
    {
        WriteModelTargetsTable();
        await SeedAttemptAsync("general", "FAIL", "dev-medium");

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(new ScriptedProvider(), target: "dev-medium");

        Assert.NotNull(record.Model);
        Assert.Equal("dev-hard", record.Model!.Profile);
        Assert.Equal("ladder", record.Model.Source);
    }

    [Fact]
    public async Task TheTopTierIsTheTopOfTheLadder()
    {
        WriteModelTargetsTable();
        await SeedAttemptAsync("dev-hard", "FAIL", "dev-hard");

        ArbiterDecisionRecord record = await EvaluateAndReadSecondaryAsync(new ScriptedProvider(), target: "dev-hard");

        Assert.Equal("top-of-ladder", RuleOf(record, LadderRule).Answer);
        Assert.Equal(RuleEffects.Keep, RuleOf(record, LadderRule).Effect);
    }

    /// <summary>The plan reader publishes the rating the task file records, which is what the rules read.</summary>
    [Fact]
    public void ThePlanReaderPublishesTheRecordedDifficulty()
    {
        WritePlan("hard");
        PlanFacts facts = _state.PlanFacts();

        ArbiterFactSet enriched = facts.Enrich(
            new ArbiterFactSet(),
            new TriggerClassification(
                "delegate",
                "conductor",
                ArbiterCallerSources.Harness,
                "dev-medium",
                "implementation",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["KYBER-ARBITER"] = "true",
                    ["PLAN_FILE"] = Plan,
                    ["TASK"] = "T2",
                },
                null,
                true),
            _repoPath);

        Assert.True(enriched.TryGet("plan.task.difficulty", out ArbiterFact? difficulty));
        Assert.Equal("hard", difficulty!.Value);
    }
}
