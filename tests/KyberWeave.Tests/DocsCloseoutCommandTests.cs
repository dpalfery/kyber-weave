using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Processes;
using Xunit;

using KyberWeave.Tests.Fakes;
namespace KyberWeave.Tests;

/// <summary>
/// <c>docs closeout</c>: the mechanical half of a plan closeout. A plan with complete closeout evidence is archived
/// with its task file removed and both indexes updated; one without it is refused and nothing moves. Each test
/// builds the repository on disk, because the evidence check reads the same files the archive gate does.
/// </summary>
public sealed class DocsCloseoutCommandTests
{
    private const string Plan = "6-Docs/plans/2026-10-20-widget.md";
    private const string TaskFile = "6-Docs/plans/2026-10-20-widget.tasks.yml";
    private const string Archived = "6-Docs/archive/plans/2026-10-20-widget.md";
    private const string PlanIndex = "6-Docs/plans/README.md";
    private const string Inventory = "6-Docs/archive/plans/README.md";
    private const string Date = "2026-11-13";

    private static DocFixture Repository(string? planText = null, string? planIndex = null)
    {
        DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(Plan, planText ?? CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready"))
            .Write(TaskFile, "{}\n")
            .Write(PlanIndex, planIndex ?? PlanIndexWithRow)
            .Write(Inventory, InventoryWithOneRow);
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        return fixture;
    }

    private static string PlanIndexWithRow => """
        ---
        id: plans/index
        title: Plan Inventory
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-11-01
        ---

        # Plan Inventory

        ## Active Plans

        | Plan | Status |
        |---|---|
        | [2026-10-20-widget.md](2026-10-20-widget.md) | Ready |

        ## Archived Plans

        See the archive.
        """;

    private static string InventoryWithOneRow => """
        ---
        id: archive/plans/index
        title: Archived Plan Inventory
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-11-01
        ---

        # Archived Plan Inventory

        | Plan | Status | Date | Archive Date | Canonical Docs / Harvested ADRs | Goal |
        |---|---|---|---|---|---|
        | [older.md](older.md) | Archived — complete 2026-10-01 | 2026-10-01 | 2026-10-01 | none | Older plan |
        """;

    private static (int Exit, string Output) Run(DocFixture fixture, bool archive, string path = Plan, string? date = Date)
    {
        DocsCloseoutSettings settings = new()
        {
            Path = Path.Combine(fixture.Root, path.Replace('/', Path.DirectorySeparatorChar)),
            Format = "json",
            Archive = archive,
            Date = date
        };

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => DocsTestCommands.Closeout().Execute(null!, settings, CancellationToken.None));
        return (run.Result, run.Output);
    }

    /// <summary>Runs the check with the plan spelled as <paramref name="typed"/> from <paramref name="workingDirectory"/>.</summary>
    private static (int Exit, string Output) RunTyped(DocFixture fixture, string typed, string workingDirectory)
    {
        DocsCloseoutSettings settings = new()
        {
            Path = typed,
            Format = "json",
            Date = Date,
            WorkingDirectory = Path.Combine(fixture.Root, workingDirectory.Replace('/', Path.DirectorySeparatorChar))
        };

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => DocsTestCommands.Closeout().Execute(null!, settings, CancellationToken.None));
        return (run.Result, run.Output);
    }

    private static string Read(DocFixture fixture, string relative) =>
        File.ReadAllText(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Exists(DocFixture fixture, string relative) =>
        File.Exists(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData(PlanIndex)]
    [InlineData(Inventory)]
    public void ALinkAtAnIndexPathIsRefusedAndNothingMoves(string linkedPath)
    {
        using DocFixture fixture = Repository();
        string linked = Path.Combine(fixture.Root, linkedPath.Replace('/', Path.DirectorySeparatorChar));
        string target = Path.Combine(fixture.Root, "owner-copy.md");
        string before = File.ReadAllText(linked);
        File.Move(linked, target);
        try
        {
            File.CreateSymbolicLink(linked, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("Symbolic-link creation is not available here; the link refusal was not run.");
        }

        (int exit, string output) = Run(fixture, archive: true);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-CLOSEOUT-003", output, StringComparison.Ordinal);
        Assert.Contains("is a symbolic link", output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(target));
        Assert.True(Exists(fixture, Plan));
        Assert.False(Exists(fixture, Archived));
    }

    [Fact]
    public void ArchivingMovesThePlanSetsItsStatusRemovesTheTaskFileAndUpdatesBothIndexes()
    {
        using DocFixture fixture = Repository();

        (int exit, string output) = Run(fixture, archive: true);

        Assert.True(exit == 0, output);
        Assert.False(Exists(fixture, Plan));
        Assert.False(Exists(fixture, TaskFile));
        string archived = Read(fixture, Archived);
        Assert.Contains("status: archived", archived, StringComparison.Ordinal);
        Assert.Contains($"**Status:** Archived, {Date}", archived, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-20-widget.md", Read(fixture, PlanIndex), StringComparison.Ordinal);
        Assert.Contains("No plans are open.", Read(fixture, PlanIndex), StringComparison.Ordinal);

        string[] rows = Read(fixture, Inventory).Split('\n').Where(line => line.StartsWith("| [", StringComparison.Ordinal)).ToArray();
        Assert.StartsWith("| [2026-10-20-widget.md](2026-10-20-widget.md) | Archived — complete 2026-11-13 |", rows[0], StringComparison.Ordinal);
        Assert.Contains("[Widget architecture](../../reference/widget.md) (D1, H1)", rows[0], StringComparison.Ordinal);
        Assert.StartsWith("| [older.md]", rows[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-10-20-widget", "6-Docs/plans")]
    [InlineData("2026-10-20-widget.md", "6-Docs/plans")]
    [InlineData("widget", ".")]
    [InlineData("widget.md", "6-Docs/plans")]
    [InlineData("2026-10-20-widget", ".")]
    [InlineData("6-Docs/plans/2026-10-20-widget.md", "6-Docs/plans")]
    [InlineData("6-Docs/plans/2026-10-20-widget.md", ".")]
    [InlineData("6-Docs/plans/2026-10-20-widget", "6-Docs")]
    public void EveryNaturalSpellingOfThePlanIsResolvedAndTheResolvedPathIsReported(string typed, string workingDirectory)
    {
        using DocFixture fixture = Repository();

        (int exit, string output) = RunTyped(fixture, typed, workingDirectory);

        Assert.True(exit == 0, output);
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.Equal(Plan, report.RootElement.GetProperty("metrics").GetProperty("plan").GetString());
        Assert.DoesNotContain("KW-DOC-CLOSEOUT-001", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASlugMatchingTwoActivePlansIsAmbiguousAndNothingMoves()
    {
        using DocFixture fixture = Repository();
        fixture.Write("6-Docs/plans/2026-10-25-widget.md", CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready"));

        (int exit, string output) = RunTyped(fixture, "widget", ".");

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-PLANREF-001", output, StringComparison.Ordinal);
        Assert.Contains("6-Docs/plans/2026-10-20-widget.md", output, StringComparison.Ordinal);
        Assert.Contains("6-Docs/plans/2026-10-25-widget.md", output, StringComparison.Ordinal);
        Assert.True(Exists(fixture, Plan));
        Assert.False(Exists(fixture, Archived));
    }

    [Fact]
    public void AfterArchivingTheDocsValidatorReportsNoLifecycleFinding()
    {
        using DocFixture fixture = Repository();
        Run(fixture, archive: true);

        Assert.DoesNotContain("KW-DOC-LIFECYCLE", new ReportedFindings(fixture).Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUncheckedTaskListRefusesTheArchiveAndMovesNothing()
    {
        string plan = CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready") + """

            ## Tasks

            - [ ] 1.1 Widget
              - **Files:** `src/Widget.cs`
            - [ ] 1.2 Gadget
              - **Files:** `src/Gadget.cs`
            """;
        using DocFixture fixture = Repository(plan);

        (int exit, string output) = Run(fixture, archive: true);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-CLOSEOUT-006", output, StringComparison.Ordinal);
        Assert.Contains("1.1", output, StringComparison.Ordinal);
        Assert.True(Exists(fixture, Plan));
        Assert.False(Exists(fixture, Archived));
    }

    [Fact]
    public void ATickedTaskListArchivesNormally()
    {
        string plan = CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready") + """

            ## Tasks

            - [x] 1.1 Widget
              - **Files:** `src/Widget.cs`
            """;
        using DocFixture fixture = Repository(plan);

        (int exit, string output) = Run(fixture, archive: true);

        Assert.True(exit == 0, output);
        Assert.True(Exists(fixture, Archived));
    }

    [Fact]
    public void AProseChecklistOutsideTheTasksSectionDoesNotRefuseTheArchive()
    {
        string plan = CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready") + """

            ## Tasks

            - [x] 1.1 Widget
              - **Files:** `src/Widget.cs`

            ## Open Questions

            - [ ] confirm rollout with owner
            """;
        using DocFixture fixture = Repository(plan);

        (int exit, string output) = Run(fixture, archive: true);

        Assert.True(exit == 0, output);
        Assert.True(Exists(fixture, Archived));
    }

    [Fact]
    public void AFencedCheckboxExampleInsideTheTasksSectionDoesNotRefuseTheArchive()
    {
        string plan = CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready") + """

            ## Tasks

            - [x] 1.1 Widget
              - **Files:** `src/Widget.cs`

            The grammar is:

            ```text
            - [ ] 9.9 An example, not a task
            ```
            """;
        using DocFixture fixture = Repository(plan);

        (int exit, string output) = Run(fixture, archive: true);

        Assert.True(exit == 0, output);
        Assert.True(Exists(fixture, Archived));
    }

    [Fact]
    public void AnUncheckedTaskStillReportsTheOtherCloseoutGapsInTheSameRun()
    {
        string plan = CloseoutCorpus.PlanDocument(
            status: "ready",
            bodyStatus: "Ready",
            sections: CloseoutCorpus.DecisionsSection("D1") + CloseoutCorpus.DesignHarvestSection("H1")) + """

            ## Tasks

            - [ ] 1.1 Widget
              - **Files:** `src/Widget.cs`
            """;
        using DocFixture fixture = Repository(plan);

        (int exit, string output) = Run(fixture, archive: true);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-CLOSEOUT-006", output, StringComparison.Ordinal);
        Assert.Contains("KW-DOC-LIFECYCLE-004", output, StringComparison.Ordinal);
        Assert.True(Exists(fixture, Plan));
        Assert.False(Exists(fixture, Archived));
    }

    [Fact]
    public void WithoutArchiveNothingChangesAndTheCommandSaysWhatItWouldDo()
    {
        using DocFixture fixture = Repository();

        (int exit, string output) = Run(fixture, archive: false);

        Assert.True(exit == 0, output);
        Assert.True(Exists(fixture, Plan));
        Assert.True(Exists(fixture, TaskFile));
        Assert.False(Exists(fixture, Archived));
        Assert.Contains("Closeout evidence is complete", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-20-widget.md", Read(fixture, PlanIndex), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmappedDecisionRefusesTheArchiveNamingWhatIsMissingAndMovesNothing()
    {
        string sections = CloseoutCorpus.DecisionsSection("D1", "D2")
                          + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Widget architecture](../../reference/widget.md)"))
                          + CloseoutCorpus.DesignHarvestSection("H1");
        using DocFixture fixture = Repository(
            CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", sections: sections));

        (int exit, string output) = Run(fixture, archive: true);

        Assert.Equal(1, exit);
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.Contains("KW-DOC-LIFECYCLE-004", output, StringComparison.Ordinal);
        Assert.Contains("D2", output, StringComparison.Ordinal);
        Assert.True(Exists(fixture, Plan));
        Assert.True(Exists(fixture, TaskFile));
        Assert.False(Exists(fixture, Archived));
        Assert.Contains("2026-10-20-widget.md", Read(fixture, PlanIndex), StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-20-widget", Read(fixture, Inventory), StringComparison.Ordinal);
    }

    [Fact]
    public void OneRunNamesEveryGapAsItsOwnFindingWithAnExampleAndMovesNothing()
    {
        // No mapping table, no impact components, the plan index gone: three problems that took three runs to see.
        string sections = CloseoutCorpus.DecisionsSection("D1", "D2", "ADMIN-DASH") + CloseoutCorpus.DesignHarvestSection("H1");
        using DocFixture fixture = Repository(
            CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", componentsLine: string.Empty, sections: sections));
        File.Delete(Path.Combine(fixture.Root, PlanIndex.Replace('/', Path.DirectorySeparatorChar)));

        (int exit, string output) = Run(fixture, archive: true);

        Assert.Equal(1, exit);
        using JsonDocument report = JsonDocument.Parse(output);
        List<(string Code, string Message, string Hint)> findings = report.RootElement.GetProperty("findings").EnumerateArray()
            .Select(item => (item.GetProperty("code").GetString()!, item.GetProperty("message").GetString()!,
                item.TryGetProperty("hint", out JsonElement hint) ? hint.GetString() ?? string.Empty : string.Empty))
            .ToList();
        Assert.Contains(findings, f => f.Code == "KW-DOC-CLOSEOUT-003" && f.Message.Contains(PlanIndex, StringComparison.Ordinal));
        (string _, string c4, string c4Hint) = Assert.Single(findings, f => f.Message.StartsWith("C4:", StringComparison.Ordinal));
        Assert.Contains("D1, D2, ADMIN-DASH", c4Hint, StringComparison.Ordinal);
        Assert.Contains("Documentation waiver", c4Hint, StringComparison.Ordinal);
        (string _, string c5, string c5Hint) = Assert.Single(findings, f => f.Message.StartsWith("C5:", StringComparison.Ordinal));
        Assert.Contains("impact-components", c5Hint, StringComparison.Ordinal);
        Assert.NotEqual(c4, c5);
        Assert.True(Exists(fixture, Plan));
        Assert.False(Exists(fixture, Archived));
    }

    [Fact]
    public void AMappingRowWhoseLinkResolvesNowhereIsNamedAlongsideEveryUnmappedId()
    {
        string sections = CloseoutCorpus.DecisionsSection("D1", "D2")
                          + CloseoutCorpus.CloseoutMappingSection(("D1, D2, H1", "[Widget](../../reference/missing.md)"))
                          + CloseoutCorpus.DesignHarvestSection("H1");
        using DocFixture fixture = Repository(
            CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", sections: sections));

        (int exit, string output) = Run(fixture, archive: true);

        Assert.Equal(1, exit);
        Assert.Contains("D1, D2, H1", output, StringComparison.Ordinal);
        Assert.Contains("reference/missing.md", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "|  | 2026-11-13 |")]
    [InlineData(true, "| 2026-10-20 | 2026-11-13 |")]
    public void TheInventoryRowShowsTheCreatedDateOnlyWhenThePlanRecordedOne(bool recorded, string cells)
    {
        string plan = CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready");
        if (recorded)
            plan = plan.Replace("status: ready", "status: ready\ncreated: 2026-10-20", StringComparison.Ordinal);
        using DocFixture fixture = Repository(plan);

        (int exit, string output) = Run(fixture, archive: true);

        Assert.True(exit == 0, output);
        string row = Read(fixture, Inventory).Split('\n').First(line => line.StartsWith("| [2026-10-20-widget.md]", StringComparison.Ordinal));
        Assert.Contains(cells, row, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingArchiveInventoryIsCreatedWithTheTableHeaderAndTheRow()
    {
        using DocFixture fixture = Repository();
        File.Delete(Path.Combine(fixture.Root, Inventory.Replace('/', Path.DirectorySeparatorChar)));

        (int exit, string output) = Run(fixture, archive: true);

        Assert.True(exit == 0, output);
        string text = Read(fixture, Inventory);
        Assert.Contains("| Plan | Status | Date | Archive Date | Canonical Docs / Harvested ADRs | Goal |", text, StringComparison.Ordinal);
        Assert.Contains("| [2026-10-20-widget.md](2026-10-20-widget.md) | Archived — complete 2026-11-13 |", text, StringComparison.Ordinal);
        Assert.Contains("doc-type: index", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInventoryWithoutTheTableGetsTheTableAppended()
    {
        using DocFixture fixture = Repository();
        fixture.Write(Inventory, "---\nid: archive/plans/index\ntitle: Archived Plan Inventory\ndoc-type: index\nstatus: current\nowner: Maintainers\nlast-reviewed: 2026-11-01\n---\n\n# Archived Plan Inventory\n\nNothing yet.\n");

        (int exit, string output) = Run(fixture, archive: true);

        Assert.True(exit == 0, output);
        string text = Read(fixture, Inventory);
        Assert.Contains("Nothing yet.", text, StringComparison.Ordinal);
        Assert.Contains("| Plan | Status | Date | Archive Date | Canonical Docs / Harvested ADRs | Goal |", text, StringComparison.Ordinal);
        Assert.Contains("[2026-10-20-widget.md]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunWithArchiveReportsEveryChangeIncludingTheInventoryItWouldCreateAndChangesNothing()
    {
        using DocFixture fixture = Repository();
        File.Delete(Path.Combine(fixture.Root, Inventory.Replace('/', Path.DirectorySeparatorChar)));
        DocsCloseoutSettings settings = new()
        {
            Path = Path.Combine(fixture.Root, Plan.Replace('/', Path.DirectorySeparatorChar)),
            Format = "json",
            Archive = true,
            DryRun = true,
            Date = Date
        };

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => DocsTestCommands.Closeout().Execute(null!, settings, CancellationToken.None));

        Assert.True(run.Result == 0, run.Output);
        Assert.Contains("Would create", run.Output, StringComparison.Ordinal);
        Assert.Contains("Would remove", run.Output, StringComparison.Ordinal);
        Assert.True(Exists(fixture, Plan));
        Assert.True(Exists(fixture, TaskFile));
        Assert.False(Exists(fixture, Archived));
        Assert.False(Exists(fixture, Inventory));
    }

    [Fact]
    public void ARunningPlanWithOtherRowsKeepsThemInThePlanIndex()
    {
        string index = PlanIndexWithRow.Replace(
            "| [2026-10-20-widget.md](2026-10-20-widget.md) | Ready |",
            "| [2026-10-20-widget.md](2026-10-20-widget.md) | Ready |\n        | [other.md](other.md) | Ready |",
            StringComparison.Ordinal);
        using DocFixture fixture = Repository(planIndex: index);

        (int exit, _) = Run(fixture, archive: true);

        Assert.Equal(0, exit);
        string text = Read(fixture, PlanIndex);
        Assert.Contains("[other.md](other.md)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("No plans are open.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArchivedPlanOrAnIndexIsNotAnActivePlan()
    {
        using DocFixture fixture = Repository();
        fixture.Write(Archived, "# archived\n");

        (int indexExit, string indexOutput) = Run(fixture, archive: true, path: PlanIndex);
        (int taken, string takenOutput) = Run(fixture, archive: true);

        Assert.Equal(1, indexExit);
        Assert.Contains("KW-DOC-CLOSEOUT-001", indexOutput, StringComparison.Ordinal);
        Assert.Equal(1, taken);
        Assert.Contains("KW-DOC-CLOSEOUT-002", takenOutput, StringComparison.Ordinal);
        Assert.True(Exists(fixture, Plan));
    }

    private const string EarlierArchive = "# an earlier archive entry that is not ours\n";

    private static string Absolute(DocFixture fixture, string relative) =>
        Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static PlanCloseout Closer(DocFixture fixture, Action<string>? afterActivePlanCheck = null, Action<string>? beforeWriteTarget = null) =>
        new(fixture.Root, KyberWeaveConfig.ProductDefaults,
            () => new DocumentLoader(fixture.Root, KyberWeaveConfig.ProductDefaults.Ontology),
            DocsPorts.Files, DocsPorts.Process, DocsPorts.Exclusive)
        {
            AfterActivePlanCheck = afterActivePlanCheck,
            BeforeWriteTarget = beforeWriteTarget
        };

    private static DateOnly ArchiveDate => DateOnly.Parse(Date, CultureInfo.InvariantCulture);

    private static Diagnostic SingleError(PlanCloseoutOutcome outcome) =>
        Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnArchiveEntryThatIsAlreadyThereIsRefusedAndKeptByteForByte(bool archive)
    {
        using DocFixture fixture = Repository();
        fixture.Write(Archived, EarlierArchive);

        (int exit, string output) = Run(fixture, archive);

        Assert.Equal(1, exit);
        Assert.Contains(PlanCloseout.ArchiveTargetExists, output, StringComparison.Ordinal);
        Assert.Equal(EarlierArchive, Read(fixture, Archived));
        Assert.True(Exists(fixture, Plan));
        Assert.True(Exists(fixture, TaskFile));
        Assert.Contains("2026-10-20-widget.md", Read(fixture, PlanIndex), StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-20-widget", Read(fixture, Inventory), StringComparison.Ordinal);
    }

    [Fact]
    public void AnArchiveEntryThatAppearsJustBeforeTheDryRunWriteIsRefusedNotOverwrittenAndNotDeleted()
    {
        using DocFixture fixture = Repository();

        PlanCloseoutOutcome outcome = Closer(fixture, beforeWriteTarget: target => File.WriteAllText(target, EarlierArchive))
            .Run(Plan, ArchiveDate, archive: false);

        Assert.False(outcome.Archived);
        Assert.Equal(PlanCloseout.ArchiveTargetExists, SingleError(outcome).Code);
        Assert.Equal(EarlierArchive, Read(fixture, Archived));
        Assert.True(Exists(fixture, Plan));
        Assert.True(Exists(fixture, TaskFile));
    }

    [Fact]
    public void AnArchiveEntryThatAppearsJustBeforeTheRealMoveIsRefusedAndNothingMoves()
    {
        using DocFixture fixture = Repository();
        int creates = 0;

        // The first create is the evidence check's copy, which the run removes again; the second is the real move.
        PlanCloseoutOutcome outcome = Closer(fixture, beforeWriteTarget: target =>
            {
                creates++;
                if (creates == 2)
                    File.WriteAllText(target, EarlierArchive);
            })
            .Run(Plan, ArchiveDate, archive: true);

        Assert.Equal(2, creates);
        Assert.False(outcome.Archived);
        Diagnostic refusal = SingleError(outcome);
        Assert.Equal(PlanCloseout.ArchiveTargetExists, refusal.Code);
        Assert.Contains("Nothing was moved", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(EarlierArchive, Read(fixture, Archived));
        Assert.True(Exists(fixture, Plan));
        Assert.True(Exists(fixture, TaskFile));
        Assert.Contains("2026-10-20-widget.md", Read(fixture, PlanIndex), StringComparison.Ordinal);
    }

    [Fact]
    public void TheExclusiveCreateRefusesAnExistingFileAndLeavesItsBytesAlone()
    {
        using DocFixture fixture = Repository();
        string path = Absolute(fixture, Archived);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, EarlierArchive);

        bool written = DocsPorts.Exclusive.TryWriteNew(path, "replacement");

        Assert.False(written);
        Assert.Equal(EarlierArchive, File.ReadAllText(path));
    }

    [Fact]
    public void TheExclusiveCreateWritesANewFileWithoutAByteOrderMark()
    {
        using DocFixture fixture = Repository();
        string path = Absolute(fixture, "6-Docs/archive/plans/new.md");

        bool written = DocsPorts.Exclusive.TryWriteNew(path, "# new\n");

        Assert.True(written);
        Assert.Equal(Encoding.UTF8.GetBytes("# new\n"), File.ReadAllBytes(path));
    }

    /// <summary>
    /// A failure while writing is not "the archive already exists": it propagates, and the partial file this call
    /// created is removed so the next closeout is not refused on it. A lone surrogate cannot be encoded, which fails
    /// the write after the file was created, as a full disk would.
    /// </summary>
    [Fact]
    public void AWriteThatFailsAfterTheCreatePropagatesAndRemovesThePartialFile()
    {
        using DocFixture fixture = Repository();
        string path = Absolute(fixture, "6-Docs/archive/plans/partial.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Assert.ThrowsAny<EncoderFallbackException>(() => DocsPorts.Exclusive.TryWriteNew(path, "text then a lone surrogate \uD800 here"));

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// When removing the partial file fails too, the write error is still what propagates: a cleanup failure must not
    /// replace the error the caller needs to see.
    /// </summary>
    [Fact]
    public void AFailingCleanupDoesNotMaskTheWriteError()
    {
        using DocFixture fixture = Repository();
        string path = Absolute(fixture, "6-Docs/archive/plans/stuck.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        List<string> deleteAttempts = [];

        Assert.ThrowsAny<EncoderFallbackException>(() => DocsPorts.Exclusive.TryWriteNew(
            path,
            "text then a lone surrogate \uD800 here",
            attempted =>
            {
                deleteAttempts.Add(attempted);
                throw new IOException("the file is in use");
            }));

        Assert.Equal([path], deleteAttempts);
    }

    /// <summary>
    /// A folder where the archive file belongs fails the exclusive create without a file being there to find: Windows
    /// answers with an access error, Unix with "exists". Either is a taken path, refused with nothing overwritten and no
    /// second look at the path to decide it.
    /// </summary>
    [Fact]
    public void ThePathHoldingAFolderIsRefusedAsTakenAndTheFolderIsLeftAlone()
    {
        using DocFixture fixture = Repository();
        string path = Absolute(fixture, "6-Docs/archive/plans/folder.md");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "inside.txt"), "kept");

        bool written = DocsPorts.Exclusive.TryWriteNew(path, "text");

        Assert.False(written);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(path, "inside.txt")));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AFolderThatCannotBeWrittenToIsRefusedNotThrownAndNothingIsCreated()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess,
            "Unix directory modes are not enforced on Windows or for a privileged user, so a denied create cannot be simulated.");
        using DocFixture fixture = Repository();
        string directory = Absolute(fixture, "6-Docs/archive/plans/locked");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            bool written = DocsPorts.Exclusive.TryWriteNew(Path.Combine(directory, "new.md"), "text");

            Assert.False(written);
            Assert.False(File.Exists(Path.Combine(directory, "new.md")));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void APlanThatVanishesAfterTheActiveCheckIsRefusedAsMissingAndNothingIsCreated()
    {
        using DocFixture fixture = Repository();

        PlanCloseoutOutcome outcome = Closer(fixture, afterActivePlanCheck: File.Delete).Run(Plan, ArchiveDate, archive: true);

        Assert.False(outcome.Archived);
        Diagnostic refusal = Assert.Single(outcome.Report.Items, item => item.Code == PlanCloseout.NotAnActivePlan);
        Assert.Contains("does not exist", refusal.Message, StringComparison.Ordinal);
        Assert.False(Exists(fixture, Archived));
    }

    [Fact]
    public void AnUnreadablePlanIndexIsNamedInTheReportNotThrown()
    {
        using DocFixture fixture = Repository();
        string index = Absolute(fixture, PlanIndex);
        File.Delete(index);
        Directory.CreateDirectory(index);

        (int exit, string output) = Run(fixture, archive: true);

        Assert.Equal(1, exit);
        Assert.Contains(PlanCloseout.IndexNotEditable, output, StringComparison.Ordinal);
        Assert.Contains("could not be read", output, StringComparison.Ordinal);
        Assert.True(Exists(fixture, Plan));
        Assert.False(Exists(fixture, Archived));
    }

    [Fact]
    public void AnUnreadableArchiveInventoryIsNamedInTheReportAndTheCheckCopyIsRemoved()
    {
        using DocFixture fixture = Repository();
        string inventory = Absolute(fixture, Inventory);
        File.Delete(inventory);
        Directory.CreateDirectory(inventory);

        (int exit, string output) = Run(fixture, archive: true);

        Assert.Equal(1, exit);
        Assert.Contains(PlanCloseout.IndexNotEditable, output, StringComparison.Ordinal);
        Assert.Contains("could not be read", output, StringComparison.Ordinal);
        Assert.True(Exists(fixture, Plan));
        Assert.False(Exists(fixture, Archived));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AnUnreadablePlanIsNamedInTheReportNotThrown()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess,
            "Unix file modes are not enforced on Windows or for a privileged user, so an unreadable plan cannot be simulated.");

        using DocFixture fixture = Repository();
        string plan = Absolute(fixture, Plan);
        File.SetUnixFileMode(plan, UnixFileMode.None);
        try
        {
            (int exit, string output) = Run(fixture, archive: true);

            Assert.Equal(1, exit);
            Assert.Contains(PlanCloseout.NotAnActivePlan, output, StringComparison.Ordinal);
            Assert.Contains("could not be read", output, StringComparison.Ordinal);
            Assert.False(Exists(fixture, Archived));
        }
        finally
        {
            File.SetUnixFileMode(plan, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void ADateThatIsNotADateIsRefused()
    {
        using DocFixture fixture = Repository();

        (int exit, string output) = Run(fixture, archive: true, date: "yesterday");

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-CLOSEOUT-005", output, StringComparison.Ordinal);
        Assert.True(Exists(fixture, Plan));
    }

    [Fact]
    public void InAGitRepositoryTheMovedAndEditedFilesAreStaged()
    {
        using DocFixture fixture = Repository();
        Directory.Delete(Path.Combine(fixture.Root, ".git"));
        Git(fixture, "init", "--quiet");
        Git(fixture, "add", "-A");
        Git(fixture, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "start");

        (int exit, string output) = Run(fixture, archive: true);

        Assert.True(exit == 0, output);
        string status = Git(fixture, "status", "--porcelain");
        Assert.DoesNotContain("??", status, StringComparison.Ordinal);
        Assert.Contains("6-Docs/archive/plans/2026-10-20-widget.md", status, StringComparison.Ordinal);
    }

    private static string Git(DocFixture fixture, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)}: {result.StandardError}");
        return result.StandardOutput;
    }
}
