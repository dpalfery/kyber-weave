using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review-round contract for secondary derivation on a multi-dispatch event: <c>delegate.model</c> is derived
/// for each dispatch by that dispatch's own target, not by the event's top-level target.
/// </summary>
public sealed class ArbiterSecondaryTargetTests : IDisposable
{
    private const string ScopeRule = "KW-ARB-SCOPE-002";
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";
    private const string ModelTrigger = "delegate.model";
    private const string NonImplementation = "code-reviewer";
    private const string Implementation = "csharp-dev";

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

    /// <summary>A multi-dispatch event. The event's own target is left unset: each dispatch names its own.</summary>
    private ArbiterEvent MultiDispatch(string first, string second) => new()
    {
        Harness = "claude",
        Phase = "pre",
        Prompt = Prompt,
        HarnessCaller = "conductor",
        Session = "s1",
        Cwd = _repo.Path,
        IsDispatch = true,
        Dispatches =
        [
            new ArbiterDispatch(first, Prompt),
            new ArbiterDispatch(second, Prompt),
        ],
    };

    [Fact]
    public async Task ImplementationSecond_DerivesModelOnlyForThatDispatch()
    {
        ArbiterEvaluationResult result = await Evaluator().EvaluateAsync(
            MultiDispatch(NonImplementation, Implementation), Config(), null, TestContext.Current.CancellationToken);

        Assert.Empty(result.Dispatches[0].Secondary);
        ArbiterSecondaryEvaluation derived = Assert.Single(result.Dispatches[1].Secondary);
        Assert.Equal(ModelTrigger, derived.Trigger);
    }

    [Fact]
    public async Task ImplementationFirst_DerivesModelOnlyForThatDispatch()
    {
        ArbiterEvaluationResult result = await Evaluator().EvaluateAsync(
            MultiDispatch(Implementation, NonImplementation), Config(), null, TestContext.Current.CancellationToken);

        ArbiterSecondaryEvaluation derived = Assert.Single(result.Dispatches[0].Secondary);
        Assert.Equal(ModelTrigger, derived.Trigger);
        Assert.Empty(result.Dispatches[1].Secondary);
    }
}
