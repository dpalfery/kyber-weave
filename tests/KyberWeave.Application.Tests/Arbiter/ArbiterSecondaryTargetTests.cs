using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Tests.Arbiter;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// Review-round contract for secondary derivation on a multi-dispatch event: <c>delegate.model</c> is derived
/// for each dispatch by that dispatch's own target, not by the event's top-level target.
/// </summary>
public sealed class ArbiterSecondaryTargetTests
{
    private const string ScopeRule = "KW-ARB-SCOPE-002";
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";
    private const string ModelTrigger = "delegate.model";
    private const string NonImplementation = "code-reviewer";
    private const string Implementation = "dev-medium";

    private readonly InMemoryArbiterState _arbiter = new();

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

    private ArbiterEvaluator Evaluator() =>
        _arbiter.Evaluator(
            FakeArbiterProvider.AnsweringByRule((ScopeRule, "within-task")),
            planReader: new PlanFacts(),
            gitFacts: new NoGitFacts());

    /// <summary>A multi-dispatch event. The event's own target is left unset: each dispatch names its own.</summary>
    private ArbiterEvent MultiDispatch(string first, string second) => new()
    {
        Harness = "claude",
        Phase = "pre",
        Prompt = Prompt,
        HarnessCaller = "conductor",
        Session = "s1",
        Cwd = "/repo",
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
