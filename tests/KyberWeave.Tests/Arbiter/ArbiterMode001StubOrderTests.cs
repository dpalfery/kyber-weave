using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-MODE-001</c> must not judge a stub task that a test-first plan orders before its tests as
/// skipping them. The model reads the task list in plan order and the tasks that depend on this one, both
/// parsed from the plan file, and may answer <c>in-order</c>, which allows.
/// </summary>
public sealed class ArbiterMode001StubOrderTests : IDisposable
{
    private const string Plan =
        """
        # Plan

        ## Tasks

        ### T1 — Contract stubs for the exporter
        Files: src/IExporter.cs

        ### T2 — Failing tests for the exporter
        Files: tests/ExporterTests.cs
        Depends on: T1

        ### T3 — Implement the exporter
        Files: src/Exporter.cs
        Depends on: T1, T2
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kw-mode-stub-" + Guid.NewGuid().ToString("N"));

    public ArbiterMode001StubOrderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "plans"));
        File.WriteAllText(Path.Combine(_root, "docs", "plans", "plan.md"), Plan);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void PlanFacts_ListTheTasksThatDependOnTheDispatchedTask_InPlanOrder()
    {
        ArbiterFactSet facts = Enrich("T1");

        Assert.True(facts.TryGet("plan.task.dependents", out ArbiterFact? dependents));
        Assert.Equal(["T2: Failing tests for the exporter", "T3: Implement the exporter"], (IEnumerable<string>)dependents.Value!);
        Assert.True(facts.TryGet(PlanFacts.TaskListFact, out ArbiterFact? list));
        Assert.Equal("T1: Contract stubs for the exporter", ((IEnumerable<string>)list.Value!).First());
    }

    [Fact]
    public void PlanFacts_ListNoDependents_ForTheLastTask()
    {
        ArbiterFactSet facts = Enrich("T3");

        Assert.True(facts.TryGet("plan.task.dependents", out ArbiterFact? dependents));
        Assert.Empty((IEnumerable<string>)dependents.Value!);
    }

    [Fact]
    public void Mode001_AsksWithThePlanOrderAndDependents()
    {
        ArbiterRule mode = Assert.Single(ArbiterConfig.ProductDefaults.Rules, rule => rule.Id == "KW-ARB-MODE-001");

        Assert.Contains("plan.task-list", mode.Ask!.State.Values);
        Assert.Contains("plan.task.dependents", mode.Ask.State.Values);
        Assert.Contains("plan.task.dependents", ArbiterTriggerCatalog.FactsByTrigger["delegate"]);
    }

    private ArbiterFactSet Enrich(string task)
    {
        ArbiterEvent ev = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "csharp-dev",
            Prompt = $"KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: {task}\n\nDo the task.",
            HarnessCaller = "conductor",
            Session = "s1",
            Cwd = _root,
            IsDispatch = true,
        };
        TriggerClassification classification = Assert.Single(TriggerClassifier.Classify(ev));
        return PlanFacts.Enrich(new ArbiterFactSet(), classification, _root);
    }
}
