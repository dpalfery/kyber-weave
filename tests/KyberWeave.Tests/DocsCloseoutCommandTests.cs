using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs closeout</c>: the mechanical half of a plan closeout. A plan with complete closeout evidence is archived
/// with its task file removed and both indexes updated; one without it is refused and nothing moves. Each test
/// builds the repository on disk, because the evidence check reads the same files the archive gate does.
/// </summary>
public sealed class DocsCloseoutCommandTests
{
    private const string Plan = "6-Docs/plans/2026-10-20-widget.md";
    private const string TaskFile = "6-Docs/plans/2026-10-20-widget.tasks.json";
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
            () => new DocsCloseoutCommand().Execute(null!, settings, CancellationToken.None));
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
            () => new DocsCloseoutCommand().Execute(null!, settings, CancellationToken.None));
        return (run.Result, run.Output);
    }

    private static string Read(DocFixture fixture, string relative) =>
        File.ReadAllText(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Exists(DocFixture fixture, string relative) =>
        File.Exists(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

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
