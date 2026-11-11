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
/// Review-round contract for the pre event's model fields: a blank caller model names nothing, so the pre event
/// names the rendered agent's model, not the caller's.
/// </summary>
public sealed class ArbiterPreEventBlankModelTests : IDisposable
{
    private const string ScopeRule = "KW-ARB-SCOPE-002";
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";
    private const string Harness = "claude";
    private const string Rendered = "claude-sonnet-5-5";

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private string ArbiterDirectory => Path.Combine(_repo.Path, "artifacts", "arbiter");

    private sealed class ScopeProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> answers = new(StringComparer.Ordinal) { [ScopeRule] = "within-task" };
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "stub", null, true, new Dictionary<string, ArbiterStep1Score>(StringComparer.Ordinal)));
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
        Arbiter = ArbiterConfig.ProductDefaults with { Enabled = true },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private ArbiterEvaluator Evaluator() => new(
        new ScopeProvider(),
        _ => null,
        new InFlightLedger(ArbiterDirectory),
        new DecisionLog(ArbiterDirectory),
        new NoGitFacts(),
        new PlanFacts(),
        TimeProvider.System);

    private ArbiterEvent Dispatch(string? model) => new()
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
        RenderedModelToken = Rendered,
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

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task BlankCallerModel_PreEventNamesTheRenderedModel(string model)
    {
        WriteTable();
        _ = await Evaluator().EvaluateAsync(Dispatch(model), Config(), null, TestContext.Current.CancellationToken);

        ArbiterLedgerEvent pre = new InFlightLedger(ArbiterDirectory).ReadAll().Single(
            ev => ev.Phase == ArbiterLedgerPhases.Pre && ev.Trigger == "delegate");
        Assert.Equal("rendered", pre.ModelSource);
        Assert.Equal("sonnet", pre.ModelToken);
    }
}
