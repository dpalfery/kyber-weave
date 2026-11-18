using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Xunit;

using KyberWeave.Core.Arbiter;
namespace KyberWeave.Tests;

/// <summary>
/// A plan or specification is open only while it is being built. The change that finishes
/// it archives it, so a merge that still carries one either merges unfinished work or leaves
/// finished work looking live.
/// </summary>
public sealed class OpenWorkValidatorTests
{
    private const string PlanIndex = "6-Docs/plans/README.md";
    private const string SpecIndex = "6-Docs/specs/README.md";

    /// <summary>An inventory index document with no entries.</summary>
    private static string Index(string id) =>
        $"""
        ---
        id: {id}
        title: {id}
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-09-24
        ---

        # {id}

        None.
        """;

    /// <summary>A draft work document of <paramref name="docType"/>.</summary>
    private static string Work(string id, string docType) =>
        $"""
        ---
        id: {id}
        title: {id}
        doc-type: {docType}
        status: draft
        component: MotorcycleRAG API
        owner: API maintainers
        last-reviewed: 2026-09-24
        ---

        # {id}
        """;

    /// <summary>Runs the validator over the fixture with the product-default registry.</summary>
    private static DiagnosticReport Validate(DocFixture fixture) =>
        new OpenWorkValidator(KyberWeaveConfigDefaults.Product, fixture.Store).Validate(fixture.Load());

    /// <summary>Lists each finding's code and file, for assertion messages.</summary>
    private static string Describe(DiagnosticReport report) =>
        string.Join("; ", report.Items.Select(i => $"{i.Code} {i.FilePath}"));

    /// <summary>
    /// The regression: three delivered plans stayed in the active folder marked Ready, and
    /// every merge after them passed.
    /// </summary>
    [Fact]
    public void APlanStillInThePlansFolderIsKWDOCLIFECYCLE003()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write(PlanIndex, Index("plans/index"))
            .Write("6-Docs/plans/2026-09-14-kilo.md", Work("plans/2026-09-14-kilo", "plan"));

        DiagnosticReport report = Validate(fixture);

        Diagnostic finding = Assert.Single(report.Items);
        Assert.Equal(OpenWorkValidator.OpenWork, finding.Code);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("6-Docs/plans/2026-09-14-kilo.md", finding.FilePath);
        Assert.True(report.HasErrors);
    }

    /// <summary>
    /// The other regression: a specification merged in stages with its closeout never done.
    /// It is one piece of work, so it is one finding however many documents it holds.
    /// </summary>
    [Fact]
    public void ASpecificationFolderIsOneFindingPointingAtItsReadme()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write(SpecIndex, Index("specs/index"))
            .Write("6-Docs/specs/tray/README.md", Index("specs/tray/index"))
            .Write("6-Docs/specs/tray/requirements.md", Work("specs/tray/requirements", "requirements"))
            .Write("6-Docs/specs/tray/design.md", Work("specs/tray/design", "spec"));

        DiagnosticReport report = Validate(fixture);

        Diagnostic finding = Assert.Single(report.Items);
        Assert.Equal(OpenWorkValidator.OpenWork, finding.Code);
        Assert.Equal("tray", finding.Subject);
        Assert.Equal("6-Docs/specs/tray/README.md", finding.FilePath);
    }

    /// <summary>An empty inventory is the closed state, not open work.</summary>
    [Fact]
    public void TheIndexesAloneAreNotOpenWork()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write(PlanIndex, Index("plans/index"))
            .Write(SpecIndex, Index("specs/index"));

        DiagnosticReport report = Validate(fixture);

        Assert.True(report.Items.Count == 0, Describe(report));
    }

    /// <summary>Archiving is how work closes, so archived plans and specifications pass.</summary>
    [Fact]
    public void ArchivedPlansAndSpecificationsAreClosed()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write(PlanIndex, Index("plans/index"))
            .Write(SpecIndex, Index("specs/index"))
            .Write("6-Docs/archive/plans/2026-09-14-kilo.md", Work("plans/2026-09-14-kilo", "plan"))
            .Write("6-Docs/archive/specs/tray/design.md", Work("specs/tray/design", "spec"));

        DiagnosticReport report = Validate(fixture);

        Assert.True(report.Items.Count == 0, Describe(report));
    }

    /// <summary>
    /// A host may point <c>plan-index</c> at a shared folder. The catalog and reference pages
    /// beside the index are not plans, so only the plan document is open work.
    /// </summary>
    [Fact]
    public void AnIndexInASharedFolderReportsOnlyPlanDocuments()
    {
        KyberWeaveConfig rootPlanIndex = KyberWeaveConfigDefaults.Product.WithConfigReg(new ConfigRegConfig
        {
            Additions = [new ConfigRegEntry(ConfigRegConfig.PlanIndexProperty, "6-Docs/README.md")]
        });
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/README.md", Index("index"))
            .Write("6-Docs/guide.md", Work("guide", "reference"))
            .Write("6-Docs/2026-09-14-kilo.md", Work("2026-09-14-kilo", "plan"));

        DiagnosticReport report = new OpenWorkValidator(rootPlanIndex, fixture.Store).Validate(fixture.Load());

        Diagnostic finding = Assert.Single(report.Items);
        Assert.Equal("6-Docs/2026-09-14-kilo.md", finding.FilePath);
    }

    /// <summary>
    /// Leftover task files in secondary docs roots must also be caught by OpenWorkValidator.
    /// </summary>
    [Fact]
    public void ATaskFileInASecondaryDocsRootIsCaught()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write(PlanIndex, Index("plans/index"))
            .Write(
                "secondary/plans/2026-09-14-kilo.tasks.yml",
                """{ "schema-version": 1, "plan": "secondary/plans/2026-09-14-kilo.md", "tasks": [] }""");

        KyberWeaveConfig config = new()
        {
            Arbiter = ArbiterConfigDefaults.Product,
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoots(["6-Docs", "secondary"])
        };

        DiagnosticReport report = new OpenWorkValidator(config, fixture.Store, fixture.Root).Validate(fixture.Load());

        Assert.True(
            report.Items.Any(i => i.Code == LeftoverTaskFile && i.FilePath == "secondary/plans/2026-09-14-kilo.tasks.yml"),
            $"leftover task file in secondary docs root must be reported as {LeftoverTaskFile}; reported: {Describe(report)}");
    }

    /// <summary>A task file left behind when the change merges (<c>KW-DOC-LIFECYCLE-005</c>).</summary>
    private const string LeftoverTaskFile = "KW-DOC-LIFECYCLE-005";
}
