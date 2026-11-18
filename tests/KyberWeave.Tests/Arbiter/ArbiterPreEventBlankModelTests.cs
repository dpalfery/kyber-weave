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

    private sealed class PlanFacts : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            facts
                .With("plan.exists", true, ArbiterFactLabel.Derived)
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
        Arbiter = ArbiterConfigDefaults.Product with { Enabled = true },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private ArbiterEvaluator Evaluator() => new(
        FakeArbiterProvider.AnsweringByRule((ScopeRule, "within-task")),
        _ => null,
        RealArbiterState.Ledger(ArbiterDirectory),
        RealArbiterState.Decisions(ArbiterDirectory),
        new NoGitFacts(),
        new PlanFacts(),
        new FileArbiterModelTargetsReader(new PhysicalFileSystem()),
            RealGateReportFacts.Create(),
            RealArbiterState.Factory(),
        TimeProvider.System);

    private ArbiterEvent Dispatch(string? model) => new()
    {
        Harness = Harness,
        Phase = "pre",
        Target = "dev-medium",
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
                ["dev-medium"] = new("general", "sonnet"),
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

        ArbiterLedgerEvent pre = RealArbiterState.Ledger(ArbiterDirectory).ReadAll().Single(
            ev => ev.Phase == ArbiterLedgerPhases.Pre && ev.Trigger == "delegate");
        Assert.Equal("rendered", pre.ModelSource);
        Assert.Equal("sonnet", pre.ModelToken);
    }
}
