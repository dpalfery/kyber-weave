using System.Text.Json;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 2.5: the model target resolver, run by the evaluator after the
/// <c>delegate.model</c> outcome is combined.
/// RED: <c>ModelTargetResolver</c>, the <c>model</c> block (<c>ArbiterModelBlock</c>), the
/// pre-event <c>model-*</c> fields and the secondary payload do not exist yet.
/// </summary>
/// <remarks>
/// Each step is observed end to end through <see cref="ArbiterEvaluator"/>, so the test pins the
/// decision record and the pre ledger event rather than a resolver signature. Assumed inputs on
/// <see cref="ArbiterEvent"/>: <c>Model</c> (the caller's own model field, the <c>delegation.model</c>
/// source), and <c>RenderedModelToken</c> (the token the target agent is rendered with).
/// The secondary payload is asserted to carry the resolved token, not a payload format.
/// </remarks>
public sealed class ModelTargetResolverTests : IDisposable
{
    private const string DifficultyRule = "KW-ARB-DIFFICULTY-001";
    private const string ScopeRule = "KW-ARB-SCOPE-002";
    private const string Plan = "docs/plans/x.md";
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";
    private const string Harness = "claude";
    private const string Model = "claude-sonnet-5-5";

    private static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private string ArbiterDirectory => Path.Combine(_repo.Path, "artifacts", "arbiter");

    /// <summary>Answers DIFFICULTY with a confident score for <see cref="Difficulty"/>, or with nothing when <see cref="Hard"/> is false.</summary>
    private sealed class HardProvider : IArbiterProvider
    {
        public bool Hard { get; init; } = true;

        public string Difficulty { get; init; } = "hard";

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> answers = new(StringComparer.Ordinal)
            {
                // The primary's scope question: the delegation asks only for the plan task's work, so it allows.
                [ScopeRule] = "within-task",
            };
            Dictionary<string, ArbiterStep1Score> scores = new(StringComparer.Ordinal);
            if (Hard)
            {
                answers[DifficultyRule] = Difficulty;
                scores[DifficultyRule] = new([0.05, 0.05, 0.9], 0.9, 0.8);
            }

            return Task.FromResult(new ArbiterStep1BatchResult(answers, "stub", null, true, scores));
        }
    }

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

    private static KyberWeaveConfig Config(string mode = "enforce", ArbiterModelTargetingConfig? targeting = null)
    {
        ArbiterModelTargetingConfig modelTargeting = targeting ?? new ArbiterModelTargetingConfig(
            mode,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["easy"] = "fast",
                ["standard"] = "general",
                ["hard"] = "deep-planning",
            },
            ["fast", "general", "deep-planning"]);
        return new()
        {
            Arbiter = ArbiterConfig.ProductDefaults with
            {
                Enabled = true,
                ModelTargeting = modelTargeting,
            },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };
    }

    private ArbiterEvaluator Evaluator(IArbiterProvider provider) => new(
        provider,
        _ => null,
        new InFlightLedger(ArbiterDirectory),
        new DecisionLog(ArbiterDirectory),
        new NoGitFacts(),
        new PlanFacts(),
        TimeProvider.System,
        secondaryTriggers: (primary, ev) => string.Equals(primary, "delegate", StringComparison.Ordinal)
            ? ["delegate.model"]
            : []);

    private ArbiterEvent Dispatch(
        string? model = null,
        string? renderedToken = Model,
        string? modelOverrideField = "model") => new()
        {
            Harness = Harness,
            Phase = "pre",
            Target = "csharp-dev",
            Prompt = Prompt,
            HarnessCaller = "conductor",
            Session = "s1",
            Cwd = _repo.Path,
            ModelOverrideField = modelOverrideField,
            Model = model,
            RenderedModelToken = renderedToken,
            IsDispatch = true,
        };

    private async Task<(ArbiterDecisionRecord Record, ArbiterDispatchEvaluation Result)> RunAsync(
        ArbiterEvent ev,
        IArbiterProvider? provider = null,
        KyberWeaveConfig? config = null)
    {
        ArbiterDispatchEvaluation result = Assert.Single(
            (await Evaluator(provider ?? new HardProvider()).EvaluateAsync(
                ev, config ?? Config(), null, TestContext.Current.CancellationToken)).Dispatches);
        ArbiterDecisionRecord primary = Assert.Single(
            new DecisionLog(ArbiterDirectory).ReadAll(), decision => decision.Trigger == "delegate");
        string blockers = string.Join(", ", primary.Rules.Where(rule => rule.Effect != "allow").Select(rule => $"{rule.Id}={rule.Answer}"));
        Assert.True(primary.Outcome == "allow", $"fixture precondition: primary outcome {primary.Outcome}: {blockers}");
        ArbiterDecisionRecord record = Assert.Single(
            new DecisionLog(ArbiterDirectory).ReadAll(), decision => decision.Trigger == "delegate.model");
        return (record, result);
    }

    private static ArbiterModelBlock BlockOf(ArbiterDecisionRecord record) =>
        Assert.IsType<ArbiterModelBlock>(record.Model);

    private void WriteTable(
        string? generalToken = "sonnet",
        bool includeAgent = true,
        string harness = Harness,
        string? deepToken = "opus")
    {
        Directory.CreateDirectory(ArbiterDirectory);
        ArbiterModelTargets table = new(
            ArbiterModelTargets.SchemaName,
            harness,
            new ArbiterModelTargetsPreset("work", "d"),
            "b",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["fast"] = "haiku",
                ["general"] = generalToken,
                ["deep-planning"] = deepToken,
            },
            includeAgent
                ? new Dictionary<string, ArbiterModelTargetsAgent>(StringComparer.Ordinal)
                {
                    ["csharp-dev"] = new("general", generalToken),
                }
                : new Dictionary<string, ArbiterModelTargetsAgent>(StringComparer.Ordinal));
        File.WriteAllText(
            Path.Combine(ArbiterDirectory, $"model-targets.{harness}.json"),
            JsonSerializer.Serialize(table));
    }

    private Task SeedPriorFailAsync(string profile) => SeedPriorAttemptAsync(profile, "FAIL");

    /// <summary>Seeds one earlier attempt at T2: the profile it ran on (null when unrecorded) and its task review.</summary>
    private async Task SeedPriorAttemptAsync(string? profile, string taskReview)
    {
        InFlightLedger ledger = new(ArbiterDirectory);
        await ledger.AppendAsync(new ArbiterLedgerEvent("pre-1", At, "hook", Harness, null, ArbiterLedgerPhases.Pre)
        {
            Trigger = "delegate",
            Target = "csharp-dev",
            Headers = new ArbiterLedgerHeaders(PlanFile: Plan, Task: "T2"),
            ModelProfile = profile,
            ModelSource = profile is null ? null : "model-targets",
        });
        await ledger.AppendAsync(new ArbiterLedgerEvent("post-1", At, "hook", Harness, null, ArbiterLedgerPhases.Post)
        {
            PreId = "pre-1",
            Returns = new ArbiterReturnMarkers(TaskReview: taskReview),
        });
    }

    private static ArbiterLedgerEvent PreEventOf(InFlightLedger ledger) =>
        ledger.ReadAll().Single(ev => ev.Phase == ArbiterLedgerPhases.Pre && ev.Trigger == "delegate");

    // 1. Outcome keep -> no change.

    [Fact]
    public async Task OutcomeKeep_LeavesTheModelUnchanged()
    {
        WriteTable();

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(), new HardProvider { Hard = false });

        Assert.Equal(RuleEffects.Keep, record.Outcome);
        Assert.Null(record.Model);
    }

    // 2. Retarget from the ladder wins; else from difficulty-profiles[answer].

    [Fact]
    public async Task RetargetFromDifficulty_ResolvesTheDifficultyProfile()
    {
        WriteTable();

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch());

        ArbiterModelBlock block = BlockOf(record);
        Assert.Equal("deep-planning", block.Profile);
        Assert.Equal("difficulty", block.Source);
    }

    [Fact]
    public async Task RetargetFromLadder_ResolvesTheLadderNext()
    {
        WriteTable();
        await SeedPriorFailAsync("fast");

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch());

        ArbiterModelBlock block = BlockOf(record);
        Assert.Equal("general", block.Profile);
        Assert.Equal("ladder", block.Source);
    }

    // 2b. A failed retry is never sent to a cheaper profile: the ladder's keep holds over the difficulty answer.
    // Regression guard: a fail on the top rung with an easy task used to climb down to fast through difficulty.

    [Fact]
    public async Task PriorFailOnTheTopRung_EasyDifficultyDoesNotCutTheModel()
    {
        WriteTable();
        await SeedPriorFailAsync("deep-planning");

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(), new HardProvider { Difficulty = "easy" });

        Assert.Null(record.Model?.Profile);
    }

    [Fact]
    public async Task PriorFailWithProfileUnrecorded_EasyDifficultyDoesNotCutTheModel()
    {
        WriteTable();
        await SeedPriorAttemptAsync(profile: null, taskReview: "FAIL");

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(), new HardProvider { Difficulty = "easy" });

        Assert.Null(record.Model?.Profile);
    }

    [Fact]
    public async Task FirstAttempt_EasyDifficultyStillRetargetsPerDifficultyProfile()
    {
        WriteTable();

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(), new HardProvider { Difficulty = "easy" });

        ArbiterModelBlock block = BlockOf(record);
        Assert.Equal("fast", block.Profile);
        Assert.Equal("difficulty", block.Source);
    }

    [Fact]
    public async Task PriorPass_EasyDifficultyStillRetargetsPerDifficultyProfile()
    {
        WriteTable();
        await SeedPriorAttemptAsync("deep-planning", "PASS");

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(), new HardProvider { Difficulty = "easy" });

        ArbiterModelBlock block = BlockOf(record);
        Assert.Equal("fast", block.Profile);
        Assert.Equal("difficulty", block.Source);
    }

    // 3. delegation.model present -> no change, reason caller-named-model.

    [Fact]
    public async Task CallerNamedModel_ChangesNothingAndSaysSo()
    {
        WriteTable();
        await SeedPriorFailAsync("fast");

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(model: "claude-opus-5-5"));

        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("caller-named-model", block.Reason);
    }

    // 4. Table null -> model-targets-unreadable.

    [Fact]
    public async Task NoTable_SaysModelTargetsUnreadable()
    {
        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch());

        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("model-targets-unreadable", block.Reason);
    }

    // 5. Profile token null -> no-token.

    [Fact]
    public async Task ProfileWithNoToken_SaysNoToken()
    {
        // The hard difficulty resolves to deep-planning, so that profile is the one left without a token.
        WriteTable(deepToken: null);

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(renderedToken: null));

        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("no-token", block.Reason);
    }

    // 6. Token equals the target agent's rendered token -> same-as-rendered.

    [Fact]
    public async Task TokenAlreadyRendered_SaysSameAsRendered()
    {
        WriteTable(generalToken: "opus");

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(renderedToken: "opus"));

        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("same-as-rendered", block.Reason);
    }

    [Fact]
    public async Task RenderedToken_ComesFromTheTargetAgentsTableEntry_AndEqualTokenSaysSameAsRendered()
    {
        // The table gives csharp-dev the opus token, and the hard retarget also lands on opus. Nothing
        // in the caller supplies the rendered token, so the pre event and the resolver must read it from the table.
        WriteTable(generalToken: "opus");

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(renderedToken: null));

        Assert.Equal("rendered", PreEventOf(new InFlightLedger(ArbiterDirectory)).ModelSource);
        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("same-as-rendered", block.Reason);
    }

    // 7. harness.model-override unsupported -> unsupported-harness, profile and token still recorded.

    [Fact]
    public async Task HarnessWithoutModelOverride_SaysUnsupportedHarnessAndKeepsProfileAndToken()
    {
        WriteTable();

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(modelOverrideField: null));

        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("unsupported-harness", block.Reason);
        Assert.Equal("deep-planning", block.Profile);
        Assert.Equal("opus", block.Token);
    }

    // 8. Mode shadow -> shadow; otherwise the result carries the token to deliver.

    [Fact]
    public async Task ModeShadow_SaysShadowAndDeliversNothing()
    {
        WriteTable();

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch(), config: Config(mode: "shadow"));

        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("shadow", block.Reason);
    }

    [Fact]
    public async Task ModeEnforce_CarriesTheTokenToDeliver()
    {
        WriteTable();

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch());

        ArbiterModelBlock block = BlockOf(record);
        Assert.True(block.Delivered);
        Assert.Null(block.Reason);
        Assert.Equal("opus", block.Token);
        Assert.Equal(Model, block.RenderedToken);
    }

    // 9. The model block, and the pre event's model-* fields naming the rendered, caller or unknown model.

    [Fact]
    public async Task ModelBlock_HoldsProfileTokenRenderedTokenSourceDeliveredAndReason()
    {
        WriteTable();

        (ArbiterDecisionRecord record, _) = await RunAsync(Dispatch());

        ArbiterModelBlock block = BlockOf(record);
        Assert.Equal("deep-planning", block.Profile);
        Assert.Equal("opus", block.Token);
        Assert.Equal(Model, block.RenderedToken);
        Assert.Equal("difficulty", block.Source);
        Assert.True(block.Delivered);
        Assert.Null(block.Reason);
    }

    [Fact]
    public async Task PreEvent_NamesTheRenderedAgentWhenNoCallerModelIsGiven()
    {
        WriteTable();

        await RunAsync(Dispatch(model: null, renderedToken: Model));

        ArbiterLedgerEvent pre = PreEventOf(new InFlightLedger(ArbiterDirectory));
        Assert.Equal("rendered", pre.ModelSource);
    }

    [Fact]
    public async Task PreEvent_NamesTheCallerWhenTheCallerNamedTheModel()
    {
        WriteTable();

        await RunAsync(Dispatch(model: "claude-opus-5-5"));

        ArbiterLedgerEvent pre = PreEventOf(new InFlightLedger(ArbiterDirectory));
        Assert.Equal("caller", pre.ModelSource);
    }

    [Fact]
    public async Task PreEvent_NamesUnknownWhenNeitherModelIsKnown()
    {
        // No table agents entry for the target, so the evaluator cannot derive a rendered token either.
        WriteTable(includeAgent: false);

        await RunAsync(Dispatch(model: null, renderedToken: null));

        ArbiterLedgerEvent pre = PreEventOf(new InFlightLedger(ArbiterDirectory));
        Assert.Equal("unknown", pre.ModelSource);
    }

    // 10. The evaluation's secondary payload for delegate.model carries the resolver result.

    [Fact]
    public async Task SecondaryPayload_CarriesTheResolvedToken()
    {
        WriteTable();

        (_, ArbiterDispatchEvaluation result) = await RunAsync(Dispatch());

        ArbiterSecondaryEvaluation secondary = Assert.Single(result.Secondary, s => s.Trigger == "delegate.model");
        Assert.NotNull(secondary.Payload);
        Assert.Contains("opus", secondary.Payload, StringComparison.Ordinal);
    }
}
