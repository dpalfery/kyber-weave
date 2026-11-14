using System.Globalization;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Parsing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs finalize</c>: the mechanical half of finalizing an approved Draft plan. A Draft plan with no open decision
/// is set to Ready with its approval recorded and its task file written; every refusal changes nothing. Each test
/// builds the repository on disk, because the step validates the files it wrote.
/// </summary>
public sealed class PlanFinalizeTests
{
    private const string Date = "2026-11-13";
    private const string Approval = "AUTONOMOUS_APPROVAL: approve-and-execute";
    private const string PlanIndexTable = """
        ---
        id: plans/index
        title: Plans
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-11-07
        ---

        # Plans

        ## Active Plans

        | Plan | Status |
        |---|---|
        | [Ready tasks](2026-11-07-ready-tasks.md) | Draft |
        """;

    /// <summary>The probe plan as an autonomous Draft awaiting approval.</summary>
    internal static string DraftPlan(bool autonomous = true) => PlanTaskFileProbe.PlanMarkdown()
        .Replace("status: current", "status: draft", StringComparison.Ordinal)
        .Replace("development-mode: test-first", autonomous ? "development-mode: test-first\nautonomy: autonomous" : "development-mode: test-first\nautonomy: supervised", StringComparison.Ordinal)
        .Replace("**Status: Ready.** This plan is approved for execution.", "**Status: Draft.** Awaiting approve and execute.", StringComparison.Ordinal);

    internal static DocFixture Repository(string? plan = null)
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus(plan ?? DraftPlan()).Write(PlanTaskFileProbe.PlanIndexPath, PlanIndexTable);
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        return fixture;
    }

    private static (int Exit, string Output) Run(DocFixture fixture, string mode = "autonomous", string? approval = Approval, string path = PlanTaskFileProbe.PlanPath)
    {
        DocsFinalizeSettings settings = new()
        {
            Path = Path.Combine(fixture.Root, path.Replace('/', Path.DirectorySeparatorChar)),
            Format = "json",
            ApprovalMode = mode,
            Approval = approval,
            Date = Date
        };

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => new DocsFinalizeCommand().Execute(null!, settings, CancellationToken.None));
        return (run.Result, run.Output);
    }

    private static string Read(DocFixture fixture, string relative) =>
        File.ReadAllText(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Exists(DocFixture fixture, string relative) =>
        File.Exists(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Asserts a refusal with <paramref name="code"/> left the plan and index as they were and wrote no task file.</summary>
    private static void AssertRefusedUnchanged(DocFixture fixture, (int Exit, string Output) run, string code, string plan, string index)
    {
        Assert.Equal(1, run.Exit);
        Assert.Contains(code, run.Output, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
        Assert.Equal(index, Read(fixture, PlanTaskFileProbe.PlanIndexPath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    [Fact]
    public void FinalizingSetsReadyRecordsTheApprovalAndWritesAValidTaskFile()
    {
        using DocFixture fixture = Repository();

        (int exit, string output) = Run(fixture);

        Assert.True(exit == 0, output);
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        Assert.Contains("status: current", plan, StringComparison.Ordinal);
        Assert.Contains($"**Status: Ready, {Date}.**", plan, StringComparison.Ordinal);
        Assert.Contains($"| AP1 | Approve and execute |", plan, StringComparison.Ordinal);
        Assert.Contains($"ANSWERED: autonomous: recommendation accepted, {Approval}, {Date} |", plan, StringComparison.Ordinal);
        Assert.Contains("| [Ready tasks](2026-11-07-ready-tasks.md) | Ready |", Read(fixture, PlanTaskFileProbe.PlanIndexPath), StringComparison.Ordinal);
        Assert.True(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
        Assert.DoesNotContain("KW-DOC-TASKS", new ReportedFindings(fixture).Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASupervisedApprovalIsRecordedAsTheOwners()
    {
        using DocFixture fixture = Repository(DraftPlan(autonomous: false));

        (int exit, string output) = Run(fixture, mode: "supervised", approval: "owner chose approve and execute in chat");

        Assert.True(exit == 0, output);
        Assert.Contains("ANSWERED: owner approval, owner chose approve and execute in chat, 2026-11-13 |",
            Read(fixture, PlanTaskFileProbe.PlanPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableExistingTaskFileIsARefusalThatChangesNothing()
    {
        if (OperatingSystem.IsWindows())
            return;

        using DocFixture fixture = Repository();
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);
        string taskPath = Path.Combine(fixture.Root, PlanTaskFileProbe.TaskFilePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
        File.WriteAllText(taskPath, "{}");
        File.SetUnixFileMode(taskPath, UnixFileMode.None);
        try
        {
            if (Environment.UserName == "root")
                return;

            (int exit, string output) = Run(fixture);

            Assert.Equal(1, exit);
            Assert.Contains("could not be read", output, StringComparison.Ordinal);
            Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
            Assert.Equal(index, Read(fixture, PlanTaskFileProbe.PlanIndexPath));
        }
        finally
        {
            File.SetUnixFileMode(taskPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Theory]
    [InlineData("""{"schema-version":1,"tasks":[]}""", "(no tasks)")]
    [InlineData("""{"tasks":[]}""", null)]
    [InlineData("""{"schema-version":2,"tasks":[]}""", null)]
    [InlineData("""{"version":1}""", null)]
    [InlineData("""{"schema-version":1}""", null)]
    [InlineData("""{"schema-version":1,"tasks":null}""", null)]
    [InlineData("""{"schema-version":1,"tasks":"none"}""", null)]
    [InlineData("""{"schema-version":1,"tasks":[null]}""", null)]
    [InlineData("{}", null)]
    [InlineData("not json", null)]
    [InlineData("null", null)]
    [InlineData("[]", null)]
    public void TheReadyQueueSaysNoTasksForAValidEmptyFileAndNothingForAnUnreadableOne(string taskFile, string? expected)
    {
        Assert.Equal(expected, PlanFinalize.ReadyQueueOf(taskFile));
    }

    [Fact]
    public void ASecondRunReportsAlreadyReadyAndChangesNothing()
    {
        using DocFixture fixture = Repository();
        Assert.Equal(0, Run(fixture).Exit);
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string taskFile = Read(fixture, PlanTaskFileProbe.TaskFilePath);

        (int exit, string output) = Run(fixture);

        Assert.True(exit == 0, output);
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.False(report.RootElement.GetProperty("metrics").GetProperty("finalized").GetBoolean());
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
        Assert.Equal(taskFile, Read(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("approved\nand more")]
    public void AMissingOrMultiLineApprovalIsRefused(string? approval)
    {
        using DocFixture fixture = Repository();
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        AssertRefusedUnchanged(fixture, Run(fixture, approval: approval), "KW-DOC-FINALIZE-003", plan, index);
    }

    [Fact]
    public void AnAutonomousApprovalOfASupervisedPlanIsRefused()
    {
        using DocFixture fixture = Repository(DraftPlan(autonomous: false));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        AssertRefusedUnchanged(fixture, Run(fixture), "KW-DOC-FINALIZE-003", plan, index);
    }

    [Fact]
    public void AnAutonomousApprovalOfAPlanThatDeclaresNoAutonomyIsAccepted()
    {
        using DocFixture fixture = Repository(DraftPlan().Replace("autonomy: autonomous\n", string.Empty, StringComparison.Ordinal));

        (int exit, string output) = Run(fixture);

        Assert.True(exit == 0, output);
    }

    /// <summary>A plan that derives a task file but then fails validation is restored byte for byte.</summary>
    [Fact]
    public void AFinalizedPlanThatFailsValidationIsRestoredAndGoesBackToTheArchitect()
    {
        using DocFixture fixture = Repository(DraftPlan().Replace("owner: Maintainers\n", string.Empty, StringComparison.Ordinal));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        (int Exit, string Output) run = Run(fixture);

        AssertRefusedUnchanged(fixture, run, "KW-DOC-FINALIZE-005", plan, index);
        Assert.Contains("undone", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanThatIsNotDraftIsRefused()
    {
        using DocFixture fixture = Repository(DraftPlan().Replace("**Status: Draft.**", "**Status: Blocked.**", StringComparison.Ordinal));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        AssertRefusedUnchanged(fixture, Run(fixture), "KW-DOC-FINALIZE-002", plan, index);
    }

    [Fact]
    public void AnOpenDecisionIsRefusedAndNamed()
    {
        using DocFixture fixture = Repository(DraftPlan().Replace("| user: explicit decision 2026-11-07 |", "| OPEN |", StringComparison.Ordinal));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        (int Exit, string Output) run = Run(fixture);

        AssertRefusedUnchanged(fixture, run, "KW-DOC-FINALIZE-004", plan, index);
        Assert.Contains("D1", run.Output, StringComparison.Ordinal);
    }

    /// <summary>Under autonomy a decision the architect left RECOMMENDED is accepted as recommended, not sent back.</summary>
    [Fact]
    public void AutonomyAcceptsARecommendedDecisionAndRecordsIt()
    {
        using DocFixture fixture = Repository(DraftPlan().Replace("| user: explicit decision 2026-11-07 |", "| RECOMMENDED |", StringComparison.Ordinal));

        (int exit, string output) = Run(fixture);

        Assert.True(exit == 0, output);
        Assert.Contains($"| autonomous: recommendation accepted, {Date} |", Read(fixture, PlanTaskFileProbe.PlanPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ASupervisedRunDoesNotAcceptARecommendedDecision()
    {
        using DocFixture fixture = Repository(DraftPlan(autonomous: false).Replace("| user: explicit decision 2026-11-07 |", "| RECOMMENDED |", StringComparison.Ordinal));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        AssertRefusedUnchanged(fixture, Run(fixture, mode: "supervised", approval: "owner approved"), "KW-DOC-FINALIZE-004", plan, index);
    }

    /// <summary>The architect numbers its own approve-and-execute row, so the gate is found by its question, not only an AP id.</summary>
    [Fact]
    public void AnOpenApproveAndExecuteRowWithAnyIdIsAnsweredInPlace()
    {
        string gate = "| D9 | Approve and execute this plan as written? | Approved as written. | The gate. | OPEN |";
        using DocFixture fixture = Repository(DraftPlan().Replace("| user: explicit decision 2026-11-07 |", "| user: explicit decision 2026-11-07 |\n" + gate, StringComparison.Ordinal));

        (int exit, string output) = Run(fixture);

        Assert.True(exit == 0, output);
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        Assert.Contains($"| ANSWERED: autonomous: recommendation accepted, {Approval}, {Date} |", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("| AP1 |", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanTheTaskFileCannotBeWrittenFromGoesBackToTheArchitect()
    {
        using DocFixture fixture = Repository(DraftPlan().Replace("- **Required skills:** `test-dev`", string.Empty, StringComparison.Ordinal));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        (int Exit, string Output) run = Run(fixture);

        AssertRefusedUnchanged(fixture, run, "KW-DOC-FINALIZE-005", plan, index);
        Assert.Contains("architect", run.Output, StringComparison.Ordinal);
    }

    private static PlanFinalize Finalizer(DocFixture fixture, Action<string>? afterActivePlanCheck = null) =>
        new(fixture.Root, KyberWeaveConfig.ProductDefaults,
            () => new DocumentLoader(fixture.Root, KyberWeaveConfig.ProductDefaults.Ontology))
        {
            AfterActivePlanCheck = afterActivePlanCheck
        };

    private static string Absolute(DocFixture fixture, string relative) =>
        Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A folder where the plan index should be: reading it throws, and must become a finding, not a crash.</summary>
    private static void ReplaceIndexWithFolder(DocFixture fixture)
    {
        string index = Absolute(fixture, PlanTaskFileProbe.PlanIndexPath);
        File.Delete(index);
        Directory.CreateDirectory(index);
    }

    [Fact]
    public void AnUnreadablePlanIndexIsARefusalThatChangesNothing()
    {
        using DocFixture fixture = Repository();
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        ReplaceIndexWithFolder(fixture);

        (int exit, string output) = Run(fixture);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-FINALIZE-005", output, StringComparison.Ordinal);
        Assert.Contains("The plan index could not be read", output, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    [Fact]
    public void AnAlreadyReadyPlanWhosePlanIndexIsUnreadableIsAFindingNotACrash()
    {
        using DocFixture fixture = Repository();
        Assert.Equal(0, Run(fixture).Exit);
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        ReplaceIndexWithFolder(fixture);

        (int exit, string output) = Run(fixture);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-FINALIZE-005", output, StringComparison.Ordinal);
        Assert.Contains("The plan index could not be read, so nothing was committed", output, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
    }

    [Fact]
    public void APlanThatVanishesAfterTheActiveCheckIsRefusedAsMissingAndNothingIsWritten()
    {
        using DocFixture fixture = Repository();

        PlanFinalizeOutcome outcome = Finalizer(fixture, File.Delete)
            .Run(PlanTaskFileProbe.PlanPath, PlanApprovalMode.Autonomous, Approval, DateOnly.Parse(Date, CultureInfo.InvariantCulture));

        Assert.False(outcome.Finalized);
        Diagnostic refusal = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanFinalize.NotAnActivePlan, refusal.Code);
        Assert.Contains("does not exist", refusal.Message, StringComparison.Ordinal);
        Assert.False(Exists(fixture, PlanTaskFileProbe.PlanPath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    /// <summary>A folder in the plan's place passes the active check, then throws on read: a finding, not a crash.</summary>
    [Fact]
    public void APlanThatCannotBeReadAfterTheActiveCheckIsAFindingNotACrash()
    {
        using DocFixture fixture = Repository();
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        PlanFinalizeOutcome outcome = Finalizer(fixture, path =>
            {
                File.Delete(path);
                Directory.CreateDirectory(path);
            })
            .Run(PlanTaskFileProbe.PlanPath, PlanApprovalMode.Autonomous, Approval, DateOnly.Parse(Date, CultureInfo.InvariantCulture));

        Assert.False(outcome.Finalized);
        Diagnostic refusal = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanFinalize.NotAnActivePlan, refusal.Code);
        Assert.Contains("could not be read", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(index, Read(fixture, PlanTaskFileProbe.PlanIndexPath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    [Fact]
    public void RestoringAfterTheTaskFileFolderVanishedDeletesNothingAndDoesNotThrow()
    {
        using DocFixture fixture = Repository();
        string gone = Absolute(fixture, "6-Docs/vanished/plan.tasks.json");

        PlanFinalize.DeleteIfPresent(gone);

        Assert.False(File.Exists(gone));
        Assert.False(Directory.Exists(Path.GetDirectoryName(gone)));
        Assert.True(Exists(fixture, PlanTaskFileProbe.PlanPath));
    }

    [Fact]
    public void RestoringDeletesAnExistingFileButNotItsFolder()
    {
        using DocFixture fixture = Repository();
        string file = Absolute(fixture, PlanTaskFileProbe.TaskFilePath);
        File.WriteAllText(file, "{}");

        PlanFinalize.DeleteIfPresent(file);

        Assert.False(File.Exists(file));
        Assert.True(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public void APathThatIsNotAPlanIsRefused()
    {
        using DocFixture fixture = Repository();
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        AssertRefusedUnchanged(fixture, Run(fixture, path: "src/Placeholder.cs"), "KW-DOC-FINALIZE-001", plan, index);
        Assert.Equal("// the repository's existing source", Read(fixture, "src/Placeholder.cs"));
    }
}
