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
/// Review-round contract for the model target resolver: the mode is an allowlist (only <c>enforce</c>
/// delivers, anything else is shadow with its own reason), and a blank caller model or a blank table token
/// counts as absent. Observed end to end through <see cref="ArbiterEvaluator"/>, as the resolver's own tests are.
/// </summary>
public sealed class ModelTargetResolverInputTests : IDisposable
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

    private static KyberWeaveConfig Config(string mode = "enforce") => new()
    {
        Arbiter = ArbiterConfig.ProductDefaults with
        {
            Enabled = true,
            ModelTargeting = new ArbiterModelTargetingConfig(
                mode,
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

    private ArbiterEvent Dispatch(string? model = null) => new()
    {
        Harness = Harness,
        Phase = "pre",
        Target = "csharp-dev",
        Prompt = Prompt,
        HarnessCaller = "conductor",
        Session = "s1",
        Cwd = _repo.Path,
        ModelOverrideField = "model",
        Model = model,
        RenderedModelToken = Model,
        IsDispatch = true,
    };

    private async Task<ArbiterDecisionRecord> RunAsync(ArbiterEvent ev, KyberWeaveConfig config)
    {
        _ = await Evaluator(new HardProvider()).EvaluateAsync(ev, config, null, TestContext.Current.CancellationToken);
        return Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll(), decision => decision.Trigger == "delegate.model");
    }

    private static ArbiterModelBlock BlockOf(ArbiterDecisionRecord record) =>
        Assert.IsType<ArbiterModelBlock>(record.Model);

    private void WriteTable(string? deepToken = "opus")
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
                ["deep-planning"] = deepToken,
            },
            new Dictionary<string, ArbiterModelTargetsAgent>(StringComparer.Ordinal)
            {
                ["csharp-dev"] = new("general", "sonnet"),
            });
        File.WriteAllText(
            Path.Combine(ArbiterDirectory, $"model-targets.{Harness}.json"),
            JsonSerializer.Serialize(table));
    }

    // Mode is an allowlist: only exact "enforce" delivers. Anything else behaves as shadow and says why.

    [Theory]
    [InlineData("SHADOW")]
    [InlineData("Enforce")]
    [InlineData("enforce ")]
    [InlineData("")]
    [InlineData("typo")]
    public async Task ModeNotExactlyEnforceOrShadow_ChangesNothingAndSaysUnknownMode(string mode)
    {
        WriteTable();

        ArbiterDecisionRecord record = await RunAsync(Dispatch(), Config(mode));

        Assert.Equal(RuleEffects.Keep, record.Outcome);
        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("unknown-mode", block.Reason);
    }

    // A blank caller model names nothing, so the Arbiter may still choose the model.

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task BlankCallerModel_CountsAsNoCallerModel_AndDelivers(string model)
    {
        WriteTable();

        ArbiterDecisionRecord record = await RunAsync(Dispatch(model: model), Config("enforce"));

        ArbiterModelBlock block = BlockOf(record);
        Assert.True(block.Delivered);
        Assert.Null(block.Reason);
        Assert.Equal("opus", block.Token);
    }

    // A blank table token is no token at all.

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task BlankTableToken_SaysNoToken(string token)
    {
        WriteTable(deepToken: token);

        ArbiterDecisionRecord record = await RunAsync(Dispatch(), Config("enforce"));

        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("no-token", block.Reason);
    }

    [Fact]
    public async Task ModeShadow_StillSaysShadowNotUnknownMode()
    {
        WriteTable();

        ArbiterDecisionRecord record = await RunAsync(Dispatch(), Config("shadow"));

        ArbiterModelBlock block = BlockOf(record);
        Assert.False(block.Delivered);
        Assert.Equal("shadow", block.Reason);
    }
}
