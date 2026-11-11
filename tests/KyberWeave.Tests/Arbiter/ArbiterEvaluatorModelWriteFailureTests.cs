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
/// Review-round contract for the evaluator's model write: a confirm callback that throws means the model
/// was not written, so the decision records <c>model-write-failed</c> and the dispatch stays allowed. A
/// cancellation still propagates. Observed through <see cref="ArbiterEvaluator"/> with the shipped secondaries.
/// </summary>
public sealed class ArbiterEvaluatorModelWriteFailureTests : IDisposable
{
    private const string DifficultyRule = "KW-ARB-DIFFICULTY-001";
    private const string ScopeRule = "KW-ARB-SCOPE-002";
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";
    private const string Harness = "claude";
    private const string Model = "claude-sonnet-5-5";

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private string ArbiterDirectory => Path.Combine(_repo.Path, "artifacts", "arbiter");

    /// <summary>Answers DIFFICULTY as hard with a confident score, so the difficulty rule wants a retarget.</summary>
    private sealed class HardProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> answers = new(StringComparer.Ordinal)
            {
                [ScopeRule] = "within-task",
                [DifficultyRule] = "hard",
            };
            Dictionary<string, ArbiterStep1Score> scores = new(StringComparer.Ordinal)
            {
                [DifficultyRule] = new([0.05, 0.05, 0.9], 0.9, 0.8),
            };
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

    private static KyberWeaveConfig Config() => new()
    {
        Arbiter = ArbiterConfig.ProductDefaults with
        {
            Enabled = true,
            ModelTargeting = new ArbiterModelTargetingConfig(
                "enforce",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["easy"] = "fast",
                    ["standard"] = "general",
                    ["hard"] = "deep-planning",
                },
                ["fast", "general", "deep-planning"]),
        },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private ArbiterEvaluator Evaluator() => new(
        new HardProvider(),
        _ => null,
        new InFlightLedger(ArbiterDirectory),
        new DecisionLog(ArbiterDirectory),
        new NoGitFacts(),
        new PlanFacts(),
        TimeProvider.System);

    private ArbiterEvent Dispatch() => new()
    {
        Harness = Harness,
        Phase = "pre",
        Target = "csharp-dev",
        Prompt = Prompt,
        HarnessCaller = "conductor",
        Session = "s1",
        Cwd = _repo.Path,
        ModelOverrideField = "model",
        RenderedModelToken = Model,
        IsDispatch = true,
    };

    private void WriteTable()
    {
        Directory.CreateDirectory(ArbiterDirectory);
        ArbiterModelTargets table = new(
            ArbiterModelTargets.SchemaName,
            Harness,
            new ArbiterModelTargetsPreset("work", "d"),
            "b",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["fast"] = "haiku",
                ["general"] = "sonnet",
                ["deep-planning"] = "opus",
            },
            new Dictionary<string, ArbiterModelTargetsAgent>(StringComparer.Ordinal)
            {
                ["csharp-dev"] = new("general", "sonnet"),
            });
        File.WriteAllText(
            Path.Combine(ArbiterDirectory, $"model-targets.{Harness}.json"),
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
            new DecisionLog(ArbiterDirectory).ReadAll(), decision => decision.Trigger == "delegate.model");
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
