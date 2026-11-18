using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using System.Text.Json;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Tests.Arbiter;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// Review-round contract for the evaluator's model write: a confirm callback that throws means the model
/// was not written, so the decision records <c>model-write-failed</c> and the dispatch stays allowed. A
/// cancellation still propagates. Observed through <see cref="ArbiterEvaluator"/> with the shipped secondaries.
/// </summary>
public sealed class ArbiterEvaluatorModelWriteFailureTests
{
    private const string DifficultyRule = "KW-ARB-DIFFICULTY-001";
    private const string ScopeRule = "KW-ARB-SCOPE-002";
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";
    private const string Harness = "claude";
    private const string Model = "claude-sonnet-5-5";

    private readonly InMemoryArbiterState _arbiter = new();

    /// <summary>
    /// The plan facts the evaluator reads. Under the one remaining roster the difficulty rule takes the recorded
    /// rating as its answer and makes no model call, so the fake states the hard rating these tests retarget on.
    /// </summary>
    private sealed class PlanFacts : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            facts
                .With("plan.exists", true, ArbiterFactLabel.Derived)
                .With("plan.task.difficulty", "hard", ArbiterFactLabel.Derived)
                .With("plan.tasks.count", 3, ArbiterFactLabel.Derived)
                .With("plan.development-mode", "standard", ArbiterFactLabel.Derived)
                .With("plan.task", "T2", ArbiterFactLabel.Derived)
                .With("plan.task.skills", new List<string> { "dev-medium" }, ArbiterFactLabel.Derived)
                .With("plan.task.files", new List<string> { "src/A.cs" }, ArbiterFactLabel.Derived)
                .With("plan.task.depends-on", new List<string>(), ArbiterFactLabel.Derived)
                .With("plan.test-contract.row", "A failing test", ArbiterFactLabel.Derived);
    }

    private sealed class NoGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    private static KyberWeaveConfig Config() => new()
    {
        Arbiter = ArbiterConfigDefaults.Product with
        {
            Enabled = true,
            ModelTargeting = new ArbiterModelTargetingConfig(
                "enforce",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["easy"] = "fast",
                    ["standard"] = "general",
                    ["hard"] = "dev-hard",
                },
                ["fast", "general", "dev-hard"]),
        },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private ArbiterEvaluator Evaluator() => new(
        new FakeArbiterProvider
        {
            Answers = { [ScopeRule] = "within-task", [DifficultyRule] = "hard" },
            Scores = { [DifficultyRule] = new([0.05, 0.05, 0.9], 0.9, 0.8) },
        },
        _ => null,
        _arbiter.LedgerStore,
        _arbiter.DecisionLog,
        new NoGitFacts(),
        new PlanFacts(),
        new FileArbiterModelTargetsReader(_arbiter.Files),
            _arbiter.GateReports(),
            _arbiter.State,
        TimeProvider.System, new FakeIdGenerator());

    private ArbiterEvent Dispatch() => new()
    {
        Harness = Harness,
        Phase = "pre",
        Target = "dev-medium",
        Prompt = Prompt,
        HarnessCaller = "conductor",
        Session = "s1",
        Cwd = InMemoryArbiterState.Directory,
        ModelOverrideField = "model",
        RenderedModelToken = Model,
        IsDispatch = true,
    };

    private void WriteTable()
    {
        _arbiter.Files.CreateDirectory(InMemoryArbiterState.Directory);
        ArbiterModelTargets table = new(
            ArbiterModelTargets.SchemaName,
            Harness,
            new ArbiterModelTargetsPreset("work", "d"),
            "b",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["fast"] = "haiku",
                ["general"] = "sonnet",
                ["dev-hard"] = "opus",
            },
            new Dictionary<string, ArbiterModelTargetsAgent>(StringComparer.Ordinal)
            {
                ["dev-medium"] = new("general", "sonnet"),
            });
        _arbiter.Files.WriteAllText(
            Path.Combine(InMemoryArbiterState.Directory, $"model-targets.{Harness}.json"),
            JsonSerializer.Serialize(table));
    }

    private static ArbiterModelBlock BlockOf(ArbiterDecisionRecord record) =>
        Assert.IsType<ArbiterModelBlock>(record.Model);

    [Fact]
    public async Task ConfirmCallbackThrows_RecordsModelWriteFailedAndStaysAllowed()
    {
        WriteTable();
        ArbiterEvaluatorOptions options = new(ConfirmModelWrite: _ => throw new IOException("write refused"));

        ArbiterEvaluationResult result = await Evaluator().EvaluateAsync(
            Dispatch(), Config(), options, TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.NotEqual(RuleEffects.Escalate, result.Outcome);
        ArbiterDecisionRecord record = Assert.Single(
            _arbiter.Records, decision => decision.Trigger == "delegate.model");
        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("model-write-failed", block.Reason);
    }

    [Fact]
    public async Task ConfirmCallbackCancels_CancellationPropagates()
    {
        WriteTable();
        ArbiterEvaluatorOptions options = new(ConfirmModelWrite: _ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Evaluator().EvaluateAsync(
            Dispatch(), Config(), options, TestContext.Current.CancellationToken));
    }
}
