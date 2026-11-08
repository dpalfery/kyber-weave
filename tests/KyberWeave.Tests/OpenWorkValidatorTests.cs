using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Xunit;

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
        new OpenWorkValidator(KyberWeaveConfig.ProductDefaults).Validate(fixture.Load());

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
        using DocFixture fixture = new DocFixture().WithCatalog()
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
        using DocFixture fixture = new DocFixture().WithCatalog()
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
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(PlanIndex, Index("plans/index"))
            .Write(SpecIndex, Index("specs/index"));

        DiagnosticReport report = Validate(fixture);

        Assert.True(report.Items.Count == 0, Describe(report));
    }

    /// <summary>Archiving is how work closes, so archived plans and specifications pass.</summary>
    [Fact]
    public void ArchivedPlansAndSpecificationsAreClosed()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
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
        KyberWeaveConfig rootPlanIndex = KyberWeaveConfig.ProductDefaults.WithConfigReg(new ConfigRegConfig
        {
            Additions = [new ConfigRegEntry(ConfigRegConfig.PlanIndexProperty, "6-Docs/README.md")]
        });
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/README.md", Index("index"))
            .Write("6-Docs/guide.md", Work("guide", "reference"))
            .Write("6-Docs/2026-09-14-kilo.md", Work("2026-09-14-kilo", "plan"));

        DiagnosticReport report = new OpenWorkValidator(rootPlanIndex).Validate(fixture.Load());

        Diagnostic finding = Assert.Single(report.Items);
        Assert.Equal("6-Docs/2026-09-14-kilo.md", finding.FilePath);
    }

    /// <summary>
    /// Open work is correct while it is being built, so the rule runs only when the merge
    /// gate asks for it; the default <c>docs validate</c> stays silent.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void DocsValidateReportsOpenWorkOnlyWithMergeReady(bool mergeReady, int expectedExit)
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(PlanIndex, """
                ---
                id: plans/index
                title: Plans
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-09-24
                ---

                # Plans

                - [Kilo](2026-09-14-kilo.md)
                """)
            .Write("6-Docs/plans/2026-09-14-kilo.md", Work("plans/2026-09-14-kilo", "plan"));
        DocsValidateSettings settings = new DocsValidateSettings
        {
            Path = fixture.Root,
            Format = "json",
            MergeReady = mergeReady
        };

        CapturedConsoleExecution<int> execution =
            ProcessConsoleCapture.Run(() => new DocsValidateCommand().Execute(null!, settings));

        Assert.Equal(expectedExit, execution.Result);
        Assert.Equal(mergeReady, execution.Output.Contains(OpenWorkValidator.OpenWork, StringComparison.Ordinal));
    }

    /// <summary>
    /// The worker prompts are duplicated from the plan into a tracked task file, and the plan is
    /// the authority, so the file has a shelf life of exactly one plan: the commit that archives
    /// the plan deletes it. One left behind describes work nobody is doing any more, and the
    /// next architect reads it as live.
    /// </summary>
    /// <remarks>
    /// The gate placement is part of the rule. An open plan is correct while the build runs, so
    /// this is a merge gate: the same corpus passes <c>docs validate</c> and fails
    /// <c>docs validate --merge-ready</c>.
    /// </remarks>
    [Fact]
    public void ALeftoverTaskFileFailsMergeReadyAsKWDOCCLIFECYCLE005()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(PlanIndex, """
                ---
                id: plans/index
                title: Plans
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-09-24
                ---

                # Plans

                - [Kilo](2026-09-14-kilo.md)
                """)
            .Write("6-Docs/plans/2026-09-14-kilo.md", Work("plans/2026-09-14-kilo", "plan"))
            .Write(
                "6-Docs/plans/2026-09-14-kilo.tasks.json",
                """{ "schema-version": 1, "plan": "6-Docs/plans/2026-09-14-kilo.md", "tasks": [] }""");

        MergedRun withoutTheGate = ValidateThroughTheCommand(fixture, mergeReady: false);
        Assert.DoesNotContain(LeftoverTaskFile, withoutTheGate.Output, StringComparison.Ordinal);

        MergedRun withTheGate = ValidateThroughTheCommand(fixture, mergeReady: true);
        Assert.True(
            withTheGate.Output.Contains(LeftoverTaskFile, StringComparison.Ordinal),
            $"a merge carrying a leftover task file must report {LeftoverTaskFile}; it reported: {withTheGate.Output}");

        // The fixture is a plain directory, not a git repository, so the archive-closeout gate
        // also fails here. What this pins is that the leftover file is an error and not a
        // warning: it alone would already make the merge gate exit 1.
        Assert.Equal(1, withTheGate.Exit);
    }

    /// <summary>
    /// A specification's task file lives in the specification folder, so the closeout moves it
    /// with the folder. Looking only in the active folders would let a file ride into the
    /// archive beside its specification, where it stays readable and forever stale.
    /// </summary>
    [Fact]
    public void ATaskFileArchivedWithItsSpecificationIsStillCaught()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SpecIndex, Index("specs/index"))
            .Write(
                "6-Docs/archive/specs/tray/tasks.json",
                """{ "schema-version": 1, "plan": "6-Docs/archive/specs/tray/design.md", "tasks": [] }""");

        MergedRun run = ValidateThroughTheCommand(fixture, mergeReady: true);

        Assert.True(
            run.Output.Contains(LeftoverTaskFile, StringComparison.Ordinal),
            $"an archived specification carrying its task file must report {LeftoverTaskFile}; " +
            $"it reported: {run.Output}");
    }

    /// <summary>
    /// Leftover task files in secondary docs roots must also be caught by OpenWorkValidator.
    /// </summary>
    [Fact]
    public void ATaskFileInASecondaryDocsRootIsCaught()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(PlanIndex, Index("plans/index"))
            .Write(
                "secondary/plans/2026-09-14-kilo.tasks.json",
                """{ "schema-version": 1, "plan": "secondary/plans/2026-09-14-kilo.md", "tasks": [] }""");

        KyberWeaveConfig config = new()
        {
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoots(["6-Docs", "secondary"])
        };

        DiagnosticReport report = new OpenWorkValidator(config, fixture.Root).Validate(fixture.Load());

        Assert.True(
            report.Items.Any(i => i.Code == LeftoverTaskFile && i.FilePath == "secondary/plans/2026-09-14-kilo.tasks.json"),
            $"leftover task file in secondary docs root must be reported as {LeftoverTaskFile}; reported: {Describe(report)}");
    }

    /// <summary>A task file left behind when the change merges (<c>KW-DOC-LIFECYCLE-005</c>).</summary>
    private const string LeftoverTaskFile = "KW-DOC-LIFECYCLE-005";

    private static MergedRun ValidateThroughTheCommand(DocFixture fixture, bool mergeReady)
    {
        DocsValidateSettings settings = new()
        {
            Path = fixture.Root,
            Format = "json",
            MergeReady = mergeReady
        };

        CapturedConsoleExecution<int> execution =
            ProcessConsoleCapture.Run(() => new DocsValidateCommand().Execute(null!, settings));
        return new MergedRun(execution.Result, execution.Output);
    }

    /// <summary>What one <c>docs validate</c> child-in-process returned.</summary>
    private sealed record MergedRun(int Exit, string Output);
}
