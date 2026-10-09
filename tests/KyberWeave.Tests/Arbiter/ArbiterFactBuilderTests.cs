using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.6 (fact builder): every fact
/// <see cref="ArbiterTriggerCatalog"/> declares for a trigger is produced with
/// its label; <c>delegation.prompt</c> is stripped; <c>delegation.paths</c>
/// applies the plan path rule; <c>config.planning-dirs</c> resolves through the
/// config registry; plan and task identity come from headers only.
/// RED: <see cref="TriggerFactBuilder"/> does not exist yet.
/// </summary>
public sealed class ArbiterFactBuilderTests
{
    private static KyberWeaveConfig DocsConfig() =>
        new() { Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs") };

    private static TriggerClassification DelegatePlannerClassification(string prompt) =>
        TriggerClassifier.ClassifySingle(
            new ArbiterEvent
            {
                Harness = "claude",
                Phase = "pre",
                Target = "architect",
                Prompt = prompt,
                HarnessCaller = "conductor",
                IsDispatch = true,
            },
            "architect",
            prompt);

    [Fact]
    public void FactBuilder_DelegateProducesEveryCatalogFactWithLabels()
    {
        const string Prompt =
            "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement `src/a.cs`.";

        TriggerClassification classification = TriggerClassifier.ClassifySingle(
            new ArbiterEvent
            {
                Harness = "claude",
                Phase = "pre",
                Target = "csharp-dev",
                Prompt = Prompt,
                HarnessCaller = "conductor",
                IsDispatch = true,
            },
            "csharp-dev",
            Prompt);

        Assert.Equal("delegate", classification.Trigger);

        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, Prompt, DocsConfig());

        Assert.True(ArbiterTriggerCatalog.TryGetFacts("delegate", out IReadOnlySet<string>? declared));
        foreach (string name in declared.Where(name => !ArbiterTriggerCatalog.IsAnswerRef(name)))
            Assert.True(facts.Contains(name), $"missing catalog fact '{name}'");
        Assert.DoesNotContain("rules.<id>.answer", facts.Facts.Keys);

        Assert.Equal("conductor", facts.Facts["caller"].Value);
        Assert.Equal(ArbiterFactLabel.Derived, facts.Facts["caller"].Label);
        Assert.Equal("csharp-dev", facts.Facts["delegation.target"].Value);
        Assert.Equal(ArbiterFactLabel.Asserted, facts.Facts["delegation.target"].Label);
        Assert.Equal("implementation", facts.Facts["delegation.target-class"].Value);
        Assert.Equal(ArbiterFactLabel.Derived, facts.Facts["delegation.target-class"].Label);
        Assert.Equal("Implement `src/a.cs`.", facts.Facts["delegation.prompt"].Value);
        Assert.Equal("docs/plans/plan.md", facts.Facts["delegation.plan-file"].Value);
        Assert.Equal("T3", facts.Facts["delegation.task"].Value);
        Assert.Equal(ArbiterFactLabel.Asserted, facts.Facts["delegation.prompt"].Label);
    }

    [Fact]
    public void FactBuilder_DelegationPromptIsStripped()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nBody `src/a.cs` here.";
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "delegate.planner",
            "architect",
            Prompt,
            "conductor",
            "harness",
            DocsConfig());

        Assert.Equal("Body `src/a.cs` here.", facts.Facts["delegation.prompt"].Value);
    }

    [Fact]
    public void FactBuilder_DelegationPathsApplyPathRule()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nTouch `src/a.cs` and `docs/plans/plan.md`.";
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "delegate",
            "csharp-dev",
            Prompt,
            "conductor",
            "harness",
            DocsConfig());

        var paths = Assert.IsAssignableFrom<IReadOnlyList<string>>(facts.Facts["delegation.paths"].Value);
        Assert.Contains("src/a.cs", paths);
        Assert.Contains("docs/plans/plan.md", paths);
    }

    [Fact]
    public void FactBuilder_PlanningDirsAreDirectoriesOfIndexes()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nINTAKE: docs/todo/item.md";
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "delegate.planner",
            "architect",
            Prompt,
            "conductor",
            "harness",
            DocsConfig());

        var dirs = Assert.IsAssignableFrom<IReadOnlyList<string>>(facts.Facts["config.planning-dirs"].Value);
        Assert.Equal(["docs/plans", "docs/specs", "docs/todo"], dirs);
        Assert.Equal(ArbiterFactLabel.Derived, facts.Facts["config.planning-dirs"].Label);
    }

    [Fact]
    public void FactBuilder_PlannerProducesMarkers()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nINTAKE: docs/todo/item.md";
        TriggerClassification classification = DelegatePlannerClassification(Prompt);

        Assert.Equal("delegate.planner", classification.Trigger);

        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, Prompt, DocsConfig());

        Assert.True(facts.Contains("delegation.markers"));
        Assert.Equal(ArbiterFactLabel.Asserted, facts.Facts["delegation.markers"].Label);
        Assert.Contains("INTAKE", facts.Facts["delegation.markers"].Value?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FactBuilder_InvestigateProducesCatalogFacts()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nInvestigate.";
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "investigate",
            "task-reviewer",
            Prompt,
            "conductor",
            "harness",
            DocsConfig());

        Assert.True(ArbiterTriggerCatalog.TryGetFacts("investigate", out IReadOnlySet<string>? declared));
        foreach (string name in declared)
            Assert.True(facts.Contains(name), $"missing catalog fact '{name}'");

        Assert.Equal("read-only", facts.Facts["delegation.target-class"].Value);
        Assert.Equal(ArbiterFactLabel.Derived, facts.Facts["delegation.target-class"].Label);
    }

    [Fact]
    public void FactBuilder_LensSpawnProducesLensFacts()
    {
        const string Prompt = "KYBER-ARBITER: true\nLENS: security\n\nApply the lens.";
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "lens.spawn",
            "review-lens",
            Prompt,
            "code-reviewer",
            "harness",
            DocsConfig());

        Assert.Equal("security", facts.Facts["lens.name"].Value);
        Assert.Equal(ArbiterFactLabel.Asserted, facts.Facts["lens.name"].Label);
        Assert.True(facts.Contains("lens.applicability"));
        Assert.Equal(ArbiterFactLabel.Derived, facts.Facts["lens.applicability"].Label);
        Assert.False(string.IsNullOrWhiteSpace(facts.Facts["lens.applicability"].Value?.ToString()));
    }

    [Fact]
    public void FactBuilder_PlanTaskIdentityFromHeadersOnly()
    {
        // The body names another plan and task-like text, but identity comes from headers.
        const string Prompt =
            "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nSee docs/plans/other.md about T9.";

        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "delegate",
            "csharp-dev",
            Prompt,
            "conductor",
            "harness",
            DocsConfig());

        Assert.Equal("docs/plans/plan.md", facts.Facts["delegation.plan-file"].Value);
        Assert.Equal("T3", facts.Facts["delegation.task"].Value);
    }

    [Fact]
    public void FactBuilder_CallerLabelAssertedWhenInferredFromHeaders()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nInvestigate.";
        TriggerClassification classification = TriggerClassifier.ClassifySingle(
            new ArbiterEvent
            {
                Harness = "antigravity",
                Phase = "pre",
                Target = "task-reviewer",
                Prompt = Prompt,
                IsDispatch = true,
            },
            "task-reviewer",
            Prompt);

        Assert.Equal("investigate", classification.Trigger);
        Assert.Equal("conductor", classification.Caller);

        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, Prompt, DocsConfig());

        Assert.Equal("conductor", facts.Facts["caller"].Value);
        Assert.Equal(ArbiterFactLabel.Asserted, facts.Facts["caller"].Label);
    }

    [Fact]
    public void FactBuilder_UnknownTriggerThrowsWithHint()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            TriggerFactBuilder.Build("not-a-trigger", "csharp-dev", "body", null, null, DocsConfig()));

        Assert.Contains("delegate", ex.Message, StringComparison.Ordinal);
    }
}
