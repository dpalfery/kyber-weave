using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The plan facts every host reads, decided from the in-memory file system and the plan path seam: the plan's own
/// text is the only resource, so the test needs no checkout.
/// </summary>
public sealed class PlanFactsTests
{
    private const string Root = "/repo";

    private const string Plan =
        """
        # Plan: job status cards

        **Status: Ready, 2026-11-14.**

        ## Tasks

        ### T1 — Job status summary aggregation in the data layer
        - **Files:** `src/Data/JobRepository.cs`

        ### T2 — Job status summary service and API endpoint
        - **Depends on:** T1
        - **Files:** `src/Api/JobController.cs`
        """;

    private readonly InMemoryFileSystem _files = new();

    [Fact]
    public void ThePlanAHeaderNames_PublishesItsTasksAndTheTaskTheHeaderNames()
    {
        WritePlan("docs/plans/cards.md", Plan);

        ArbiterFactSet facts = Facts(Headers(("PLAN_FILE", "docs/plans/cards.md"), ("TASK", "T2")));

        Assert.Equal(true, Value(facts, "plan.exists"));
        Assert.Equal(2, Value(facts, "plan.tasks.count"));
        Assert.Equal("T2", Value(facts, "plan.task"));
        Assert.Equal(
            "T1: Job status summary aggregation in the data layer",
            Assert.IsAssignableFrom<IEnumerable<string>>(Value(facts, PlanFacts.TaskListFact)).First());
    }

    [Fact]
    public void AMissingPlan_IsAnAbsentFactRatherThanAFailure()
    {
        ArbiterFactSet facts = Facts(Headers(("PLAN_FILE", "docs/plans/missing.md")));

        Assert.Equal(false, Value(facts, "plan.exists"));
        Assert.Null(Value(facts, "plan.task"));
    }

    [Fact]
    public void AHeaderlessDelegation_FindsTheOneActivePlanThroughTheFileSeam()
    {
        WritePlan("docs/plans/cards.md", Plan);

        ArbiterFactSet facts = Facts(
            new TriggerClassification("delegate", "conductor", "harness", "dev-medium", "implementation", Headers(), null, true),
            new ArbiterFactSet()
                .With("config.planning-dirs", new List<string> { "docs/plans/**" }, ArbiterFactLabel.Asserted)
                .With("delegation.prompt", "TASK: T1\n\nWrite the tests first.", ArbiterFactLabel.Asserted));

        Assert.Equal(PlanFacts.MachineIdentity, Value(facts, PlanFacts.IdentityFact));
        Assert.Equal(true, Value(facts, "plan.exists"));
        Assert.Equal("T1", Value(facts, "plan.task"));
    }

    private ArbiterFactSet Facts(IReadOnlyDictionary<string, string> headers, ArbiterFactSet? facts = null) =>
        Facts(
            new TriggerClassification("delegate", "conductor", "harness", "dev-medium", "implementation", headers, null, true),
            facts ?? new ArbiterFactSet());

    private ArbiterFactSet Facts(TriggerClassification classification, ArbiterFactSet facts) =>
        new PlanFacts(new PlanFileResolver(), _files).Enrich(facts, classification, Root);

    private void WritePlan(string relative, string text)
    {
        _files.CreateDirectory($"{Root}/docs/plans");
        _files.WriteAllText($"{Root}/{relative}", text);
    }

    private static object? Value(ArbiterFactSet facts, string name) =>
        facts.TryGet(name, out ArbiterFact? fact) ? fact.Value : null;

    private static IReadOnlyDictionary<string, string> Headers(params (string Name, string Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

    /// <summary>Resolves a repository-relative plan inside the root it is given, and refuses a path that escapes.</summary>
    private sealed class PlanFileResolver : Contracts.Repositories.IPlanFileResolver
    {
        public string? Resolve(string? planFile, string repositoryRoot) =>
            planFile is null || planFile.StartsWith("..", StringComparison.Ordinal)
                ? null
                : Path.Combine(repositoryRoot, planFile);
    }
}
