using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The plan inventory is how a reader learns which plans are open, so a plan document left in
/// the active folder without being listed is live to retrieval and invisible to the reader.
/// </summary>
public sealed class PlanInventoryValidatorTests
{
    private const string PlanIndex = "6-Docs/plans/README.md";

    /// <summary>
    /// A host that wants archive content retrievable drops the archive segment from the
    /// ontology exclusions, so the corpus loads documents under 6-Docs/archive/.
    /// </summary>
    private static readonly OntologyConfig ArchiveInCorpus =
        OntologyConfig.ProductDefaults.Clone(excludedPathSegments: ["node_modules", "obj", "bin"]);

    /// <summary>
    /// The same host overrides plan-index to the repository-root index, so the folder the
    /// validator derives spans the whole docs root, archive subtree included.
    /// </summary>
    private static readonly KyberWeaveConfig RootPlanIndexOverrides = KyberWeaveConfig.ProductDefaults
        .WithConfigReg(new ConfigRegConfig
        {
            Additions = [new ConfigRegEntry(ConfigRegConfig.PlanIndexProperty, "6-Docs/README.md")]
        })
        .WithOntology(ArchiveInCorpus);

    private static string Index(string body, string id = "plans/index") =>
        $"""
        ---
        id: {id}
        title: Plan inventory
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-09-13
        ---

        # Plan inventory

        {body}
        """;

    private static string Plan(string id, string body = "") =>
        $"""
        ---
        id: {id}
        title: {id}
        doc-type: plan
        status: needs-review
        component: MotorcycleRAG API
        owner: API maintainers
        last-reviewed: 2026-09-13
        ---

        # {id}

        {body}
        """;

    private static DiagnosticReport Validate(DocFixture fixture) =>
        new PlanInventoryValidator(KyberWeaveConfig.ProductDefaults).Validate(fixture.Load());

    private static string Describe(DiagnosticReport report) =>
        string.Join("; ", report.Items.Select(i => $"{i.Code} {i.FilePath}"));

    /// <summary>
    /// The regression: a plan was archived and the inventory said "None", but the dispatch
    /// pack beneath it stayed in the active folder. Nothing reported it.
    /// </summary>
    [Fact]
    public void APackLeftInThePlansFolderWhenItsPlanWasArchivedIsKWDOCLIFECYCLE001()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(PlanIndex, Index("""
                ## Active Plans

                None.

                ## Archived Plans

                - [Spine](../archive/plans/2026-09-06-spine.md)
                """))
            .Write("6-Docs/plans/tasks/README.md", Plan("plans/tasks/index", "- [T1](T1.md)"))
            .Write("6-Docs/plans/tasks/T1.md", Plan("plans/tasks/T1"));

        DiagnosticReport report = Validate(fixture);

        Assert.Equal(
            ["6-Docs/plans/tasks/README.md", "6-Docs/plans/tasks/T1.md"],
            report.Items.Where(i => i.Code == PlanInventoryValidator.UnlistedPlan).Select(i => i.FilePath).Order(StringComparer.Ordinal));
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void APlanReachedThroughALinkedPackIsListed()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(PlanIndex, Index("- [Spine dispatch pack](tasks/)"))
            .Write("6-Docs/plans/tasks/README.md", Plan("plans/tasks/index", "- [T1](T1.md)"))
            .Write("6-Docs/plans/tasks/T1.md", Plan("plans/tasks/T1"));

        DiagnosticReport report = Validate(fixture);

        Assert.True(report.Items.Count == 0, Describe(report));
    }

    [Fact]
    public void APageOutsideThePlansFolderDoesNotListAPlanByLinkingIt()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(PlanIndex, Index("- [Architecture](../architecture.md)"))
            .Write("6-Docs/architecture.md", Index("- [Rollout](plans/rollout.md)", id: "architecture"))
            .Write("6-Docs/plans/rollout.md", Plan("plans/rollout"));

        DiagnosticReport report = Validate(fixture);

        Assert.Contains(report.Items, i => i.Code == PlanInventoryValidator.UnlistedPlan && i.FilePath == "6-Docs/plans/rollout.md");
    }

    [Fact]
    public void ACorpusWithoutAPlanIndexIsSilent()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/plans/rollout.md", Plan("plans/rollout"));

        DiagnosticReport report = Validate(fixture);

        Assert.True(report.Items.Count == 0, Describe(report));
    }

    /// <summary>
    /// A host can lift the loader's archive exclusion and override plan-index to an index
    /// above the archive subtree; the folder the validator derives then spans the archive.
    /// Closed work must still not fail the inventory as unlisted open work.
    /// </summary>
    [Fact]
    public void APlanUnderTheArchiveSegmentIsNotUnlistedWhenTheIndexSitsAboveTheArchiveSubtree()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/README.md", Index("None.", "docs/index"))
            .Write("6-Docs/archive/plans/closed-spine.md", Plan("plans/closed-spine"));

        DocumentSet set = new DocumentLoader(fixture.Root, ArchiveInCorpus).Load();
        DiagnosticReport report = new PlanInventoryValidator(RootPlanIndexOverrides).Validate(set);

        Assert.True(report.Items.Count == 0, Describe(report));
    }
}
