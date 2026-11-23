using System.Globalization;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs closeout</c>: the mechanical half of a plan closeout. A plan with complete closeout evidence is archived
/// with its task file removed and both indexes updated; one without it is refused and nothing moves. Each test
/// builds the repository in memory, because the evidence check reads the same files the archive gate does. What
/// needs a real disk (links, file modes, the exclusive create, git staging) is in
/// <c>DocsCloseoutRealFileSystemTests</c> in the integration project.
/// </summary>
public sealed class DocsCloseoutCommandTests
{
    private const string Plan = DocsCloseoutRepository.Plan;
    private const string TaskFile = DocsCloseoutRepository.TaskFile;
    private const string Archived = DocsCloseoutRepository.Archived;
    private const string PlanIndex = DocsCloseoutRepository.PlanIndex;
    private const string Inventory = DocsCloseoutRepository.Inventory;
    private const string Date = DocsCloseoutRepository.Date;
    private const string EarlierArchive = "# an earlier archive entry that is not ours\n";

    private static string PlanIndexWithRow => DocsCloseoutRepository.PlanIndexWithRow;

    private static DocFixture Repository(string? planText = null, string? planIndex = null) =>
        DocsCloseoutRepository.Create(inMemory: true, planText, planIndex);

    private static (int Exit, string Output) Run(DocFixture fixture, bool archive, string path = Plan, string? date = Date)
    {
        DocsCloseoutSettings settings = new()
        {
            Path = Path.Combine(fixture.Root, path.Replace('/', Path.DirectorySeparatorChar)),
            Format = "json",
            Archive = archive,
            Date = date
        };

        TestConsole console = new() { EmitAnsiSequences = false };
        int exit = DocsTestCommands.Closeout(Services(fixture), console).Execute(null!, settings, CancellationToken.None);
        return (exit, console.Output);
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

        TestConsole console = new() { EmitAnsiSequences = false };
        int exit = DocsTestCommands.Closeout(Services(fixture), console).Execute(null!, settings, CancellationToken.None);
        return (exit, console.Output);
    }

    /// <summary>The host's services over the in-memory checkout: git answers as it does in a folder that is no repository, and the exclusive create writes into memory.</summary>
    private static IServiceProvider Services(DocFixture fixture) =>
        DocsMemoryServicesD.Services(fixture, services =>
        {
            services.AddSingleton<IBoundedProcessExecutor>(NotAGitRepository());
            services.AddSingleton<IExclusiveFileWriter>(new InMemoryExclusiveFileWriter(fixture.Memory!));
        });

    private static ScriptedProcessExecutor NotAGitRepository() =>
        new(_ => new ProcessResult(128, string.Empty, "fatal: not a git repository"));

    private static string Read(DocFixture fixture, string relative) => fixture.ReadText(relative);

    private static bool Exists(DocFixture fixture, string relative) => fixture.Exists(relative);

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
        fixture.Write("6-Docs/plans/2026-10-25-widget.md", CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", linkPrefix: ".."));

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
        string plan = CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", linkPrefix: "..") + """

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
        string plan = CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", linkPrefix: "..") + """

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
        string plan = CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", linkPrefix: "..") + """

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
        string plan = CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", linkPrefix: "..") + """

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
            linkPrefix: "..",
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
        Assert.Contains("Would rewrite 1 relative link", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-20-widget.md", Read(fixture, PlanIndex), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmappedDecisionRefusesTheArchiveNamingWhatIsMissingAndMovesNothing()
    {
        string sections = CloseoutCorpus.DecisionsSection("D1", "D2")
                          + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Widget architecture](../reference/widget.md)"))
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
        fixture.Memory!.DeleteFile(fixture.FullPath(PlanIndex));

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
                          + CloseoutCorpus.CloseoutMappingSection(("D1, D2, H1", "[Widget](../reference/missing.md)"))
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
        string plan = CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", linkPrefix: "..");
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
        fixture.Memory!.DeleteFile(fixture.FullPath(Inventory));

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
        fixture.Memory!.DeleteFile(fixture.FullPath(Inventory));
        DocsCloseoutSettings settings = new()
        {
            Path = fixture.FullPath(Plan),
            Format = "json",
            Archive = true,
            DryRun = true,
            Date = Date
        };

        TestConsole console = new() { EmitAnsiSequences = false };
        int exit = DocsTestCommands.Closeout(Services(fixture), console).Execute(null!, settings, CancellationToken.None);

        Assert.True(exit == 0, console.Output);
        Assert.Contains("Would create", console.Output, StringComparison.Ordinal);
        Assert.Contains("Would remove", console.Output, StringComparison.Ordinal);
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
    public void AnUnreadablePlanIndexIsNamedInTheReportNotThrown()
    {
        using DocFixture fixture = Repository();
        string index = fixture.FullPath(PlanIndex);
        fixture.Memory!.DeleteFile(index);
        fixture.Memory.CreateDirectory(index);

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
        string inventory = fixture.FullPath(Inventory);
        fixture.Memory!.DeleteFile(inventory);
        fixture.Memory.CreateDirectory(inventory);

        (int exit, string output) = Run(fixture, archive: true);

        Assert.Equal(1, exit);
        Assert.Contains(PlanCloseout.IndexNotEditable, output, StringComparison.Ordinal);
        Assert.Contains("could not be read", output, StringComparison.Ordinal);
        Assert.True(Exists(fixture, Plan));
        Assert.False(Exists(fixture, Archived));
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
}
