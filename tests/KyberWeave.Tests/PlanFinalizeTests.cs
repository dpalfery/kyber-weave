using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Xunit;

using KyberWeave.Tests.Fakes;
using static KyberWeave.Tests.PlanFinalizeFixture;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs finalize</c>: the mechanical half of finalizing an approved Draft plan. A Draft plan with no open decision
/// is set to Ready with its approval recorded and its task file written; every refusal changes nothing. Each test
/// builds the repository on disk, because the step validates the files it wrote.
/// </summary>
public sealed class PlanFinalizeTests
{
    /// <summary>The probe plan as an autonomous Draft awaiting approval.</summary>
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
            () => DocsTestCommands.Finalize().Execute(null!, settings, CancellationToken.None));
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
    public void FinalizingWritesExactlyOneYamlTaskFileEvenWhenALegacyJsonSitsBesideIt()
    {
        using DocFixture fixture = Repository();
        PlanTaskFileOutcome seeded = new PlanTaskFileGenerator(
            fixture.Root, KyberWeaveConfigDefaults.Product, DocsPorts.Files)
            .Run(PlanTaskFileProbe.PlanPath, PlanTaskFileMode.Write);
        Assert.False(seeded.Report.HasErrors, string.Join("; ", seeded.Report.Items.Select(item => item.Message)));
        File.WriteAllText(
            Path.Combine(fixture.Root, PlanTaskFileProbe.LegacyTaskFilePath.Replace('/', Path.DirectorySeparatorChar)),
            PlanTaskFileProbe.LegacyJsonTaskFile());

        (int exit, string output) = Run(fixture);

        Assert.True(exit == 0, output);
        Assert.True(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.LegacyTaskFilePath));
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
    [UnsupportedOSPlatform("windows")]
    public void AnUnreadableExistingTaskFileIsARefusalThatChangesNothing()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess,
            "Unix file modes are not enforced on Windows or for a privileged user, so an unreadable file cannot be simulated.");

        using DocFixture fixture = Repository();
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);
        string taskPath = Path.Combine(fixture.Root, PlanTaskFileProbe.TaskFilePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
        File.WriteAllText(taskPath, "{}");
        File.SetUnixFileMode(taskPath, UnixFileMode.None);
        try
        {
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
    [InlineData("{ schema-version: 2, tasks: [] }", "(no tasks)")]
    [InlineData("""{"tasks":[]}""", null)]
    [InlineData("""{"schema-version":2,"tasks":[]}""", "(no tasks)")]
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

    /// <summary>
    /// A YAML tag makes the reader hand back a double, so a non-finite value reaches the JSON writer,
    /// which has no literal for it. An unreadable task file names no ready tasks; it must not take the
    /// whole command down with an <see cref="ArgumentException"/>.
    /// </summary>
    [Fact]
    public void ATaggedNonFiniteAgentConfidenceIsAnUnreadableTaskFile()
    {
        const string TaskFile = """
            schema-version: 2
            tasks:
              - id: T1
                agent-confidence: !!float .nan
            """;

        Assert.Null(PlanFinalize.ReadyQueueOf(TaskFile));
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
        new(fixture.Root, KyberWeaveConfigDefaults.Product,
            () => RealDocumentLoader.Create(fixture.Root, KyberWeaveConfigDefaults.Product.Ontology),
            DocsPorts.Files, DocsPorts.Process, DocsTestCommands.SchemaGate, DocsPorts.Paths, DocsPorts.Environment)
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

        PlanFinalize.DeleteIfPresent(DocsPorts.Files, gone);

        Assert.False(File.Exists(gone));
        Assert.False(Directory.Exists(Path.GetDirectoryName(gone)));
        Assert.True(Exists(fixture, PlanTaskFileProbe.PlanPath));
    }

    [Fact]
    public void RestoringAFileThatCannotBeDeletedIsReportedNotThrown()
    {
        using DocFixture fixture = Repository();
        string folderWhereTheFileBelongs = Absolute(fixture, PlanTaskFileProbe.TaskFilePath);
        Directory.CreateDirectory(folderWhereTheFileBelongs);

        bool gone = PlanFinalize.DeleteIfPresent(DocsPorts.Files, folderWhereTheFileBelongs);

        Assert.False(gone);
        Assert.True(Directory.Exists(folderWhereTheFileBelongs));
    }

    [Fact]
    public void RestoreNamesEveryFileItCannotUndoAndStillUndoesTheRest()
    {
        using DocFixture fixture = Repository();
        string plan = PlanTaskFileProbe.PlanPath;
        string index = PlanTaskFileProbe.PlanIndexPath;
        string indexBefore = Read(fixture, index);
        string finalizedPlan = Read(fixture, plan);
        // A folder where the plan and the task file belong: neither can be written or deleted as a file.
        File.Delete(Absolute(fixture, plan));
        Directory.CreateDirectory(Absolute(fixture, plan));
        Directory.CreateDirectory(Absolute(fixture, PlanTaskFileProbe.TaskFilePath));
        File.WriteAllText(Absolute(fixture, index), "changed by finalize");

        List<string> notUndone = Finalizer(fixture).Restore(
            plan, finalizedPlan, index, indexBefore, PlanTaskFileProbe.TaskFilePath, taskFileBefore: null);

        Assert.Equal([plan, PlanTaskFileProbe.TaskFilePath], notUndone);
        Assert.Equal(indexBefore, Read(fixture, index));
    }

    [Fact]
    public void RestoreReportsNothingWhenEveryFileIsPutBack()
    {
        using DocFixture fixture = Repository();
        string planBefore = Read(fixture, PlanTaskFileProbe.PlanPath);
        File.WriteAllText(Absolute(fixture, PlanTaskFileProbe.PlanPath), "changed by finalize");
        File.WriteAllText(Absolute(fixture, PlanTaskFileProbe.TaskFilePath), "{}");

        List<string> notUndone = Finalizer(fixture).Restore(
            PlanTaskFileProbe.PlanPath, planBefore, planIndex: null, indexText: null, PlanTaskFileProbe.TaskFilePath, taskFileBefore: null);

        Assert.Empty(notUndone);
        Assert.Equal(planBefore, Read(fixture, PlanTaskFileProbe.PlanPath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    [Fact]
    public void ACouldNotUndoReportNamesTheFilesAndTellsAnUntrackedTaskFileToBeDeletedByHand()
    {
        DiagnosticReport report = new();

        PlanFinalize.ReportNotUndone(
            report, "6-Docs/plans/p.md", ["6-Docs/plans/p.md", "6-Docs/plans/p.tasks.json"], createdTaskFile: "6-Docs/plans/p.tasks.json");

        Diagnostic error = Assert.Single(report.Items);
        Assert.Equal(PlanFinalize.PlanNeedsArchitect, error.Code);
        Assert.Equal("Could not undo the change to: 6-Docs/plans/p.md, 6-Docs/plans/p.tasks.json.", error.Message);
        Assert.Contains("git checkout -- <file>", error.Hint, StringComparison.Ordinal);
        Assert.Contains("Restore 6-Docs/plans/p.md with", error.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Restore 6-Docs/plans/p.md, 6-Docs/plans/p.tasks.json", error.Hint, StringComparison.Ordinal);
        Assert.Contains("'6-Docs/plans/p.tasks.json' did not exist before this step", error.Hint, StringComparison.Ordinal);
        Assert.Contains("delete it by hand", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrackedTaskFileThatCannotBeRestoredGetsTheCheckoutAdvice()
    {
        DiagnosticReport report = new();

        PlanFinalize.ReportNotUndone(report, "p.md", ["p.tasks.json"], createdTaskFile: null);

        Diagnostic error = Assert.Single(report.Items);
        Assert.Contains("git checkout -- <file>", error.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("delete it by hand", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsReportedWhenNothingWasLeftBehind()
    {
        DiagnosticReport report = new();

        PlanFinalize.ReportNotUndone(report, "p.md", [], createdTaskFile: "p.tasks.json");

        Assert.Empty(report.Items);
    }

    [Fact]
    public void RestoringReportsAFileOrAFolderAlreadyGoneAsDeleted()
    {
        using DocFixture fixture = Repository();

        Assert.True(PlanFinalize.DeleteIfPresent(DocsPorts.Files, Absolute(fixture, "6-Docs/vanished/plan.tasks.json")));
        Assert.True(PlanFinalize.DeleteIfPresent(DocsPorts.Files, Absolute(fixture, "6-Docs/never-there.json")));
    }

    [Fact]
    public void RestoringDeletesAnExistingFileButNotItsFolder()
    {
        using DocFixture fixture = Repository();
        string file = Absolute(fixture, PlanTaskFileProbe.TaskFilePath);
        File.WriteAllText(file, "{}");

        PlanFinalize.DeleteIfPresent(DocsPorts.Files, file);

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

    [Theory]
    [InlineData(PlanTaskFileProbe.PlanPath)]
    [InlineData(PlanTaskFileProbe.PlanIndexPath)]
    public void ALinkAtThePlanOrIndexPathIsRefusedAndNothingIsWrittenThroughIt(string linkedPath)
    {
        using DocFixture fixture = Repository();
        string linked = Absolute(fixture, linkedPath);
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

        (int exit, string output) = Run(fixture);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-FINALIZE-005", output, StringComparison.Ordinal);
        Assert.Contains("is a symbolic link", output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(target));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    [Fact]
    public void RestoreLeavesADanglingLinkAtTheTaskFilePathAlone()
    {
        using DocFixture fixture = Repository();
        string taskFile = Absolute(fixture, PlanTaskFileProbe.TaskFilePath);
        File.CreateSymbolicLink(taskFile, Path.Combine(fixture.Root, "nowhere.json"));
        string planBefore = Read(fixture, PlanTaskFileProbe.PlanPath);

        List<string> notUndone = Finalizer(fixture).Restore(
            PlanTaskFileProbe.PlanPath, planBefore, planIndex: null, indexText: null, PlanTaskFileProbe.TaskFilePath, taskFileBefore: null);

        Assert.Empty(notUndone);
        Assert.NotNull(new FileInfo(taskFile).LinkTarget);
    }

    [Fact]
    public void RestoreNeverWritesThroughALinkAtATaskFilePlanOrIndexPath()
    {
        using DocFixture fixture = Repository();
        string outside = Path.Combine(fixture.Root, "outside.json");
        File.WriteAllText(outside, "owner's bytes");
        string taskFile = Absolute(fixture, PlanTaskFileProbe.TaskFilePath);
        try
        {
            File.CreateSymbolicLink(taskFile, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("Symbolic-link creation is not available here; the link guard was not run.");
        }

        List<string> notUndone = Finalizer(fixture).Restore(
            PlanTaskFileProbe.PlanPath, Read(fixture, PlanTaskFileProbe.PlanPath), planIndex: null, indexText: null,
            PlanTaskFileProbe.TaskFilePath, taskFileBefore: "{\"before\":true}");

        Assert.Equal([PlanTaskFileProbe.TaskFilePath], notUndone);
        Assert.Equal("owner's bytes", File.ReadAllText(outside));
    }

    [Fact]
    public void TheReadyQueueIsListedFromAReadableTaskFileAndSaysNothingExtra()
    {
        using DocFixture fixture = Repository();
        File.WriteAllText(Absolute(fixture, PlanTaskFileProbe.TaskFilePath), "{\"schema-version\":1,\"tasks\":[]}");
        DiagnosticReport report = new();

        string? queue = Finalizer(fixture).ReadyQueue(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.PlanPath, report);

        Assert.Equal("(no tasks)", queue);
        Assert.Empty(report.Items);
    }

    [Fact]
    public void ADroppedReadyQueueIsReportedWithItsCause()
    {
        using DocFixture fixture = Repository();
        // A folder where the task file belongs: it is there, but cannot be read as a file.
        Directory.CreateDirectory(Absolute(fixture, PlanTaskFileProbe.TaskFilePath));
        DiagnosticReport unreadable = new();
        DiagnosticReport corrupt = new();
        DiagnosticReport absent = new();

        Assert.Null(Finalizer(fixture).ReadyQueue(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.PlanPath, unreadable));
        Directory.Delete(Absolute(fixture, PlanTaskFileProbe.TaskFilePath));
        Assert.Null(Finalizer(fixture).ReadyQueue(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.PlanPath, absent));
        File.WriteAllText(Absolute(fixture, PlanTaskFileProbe.TaskFilePath), "not json");
        Assert.Null(Finalizer(fixture).ReadyQueue(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.PlanPath, corrupt));

        Diagnostic cause = Assert.Single(unreadable.Items);
        Assert.Equal(Severity.Warning, cause.Severity);
        Assert.Contains("could not be read", cause.Message, StringComparison.Ordinal);
        Assert.Contains(PlanTaskFileProbe.TaskFilePath, cause.Message, StringComparison.Ordinal);
        Assert.Contains("it is not there", Assert.Single(absent.Items).Message, StringComparison.Ordinal);
        Assert.Contains("not a valid task file", Assert.Single(corrupt.Items).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsLinkAgreesWithTheFileInfoLinkTargetCheckForEveryKindOfPath()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");

        using DocFixture fixture = Repository();
        string file = Path.Combine(fixture.Root, "file.txt");
        string folder = Path.Combine(fixture.Root, "folder");
        File.WriteAllText(file, "x");
        Directory.CreateDirectory(folder);
        string fileLink = Path.Combine(fixture.Root, "file-link");
        string folderLink = Path.Combine(fixture.Root, "folder-link");
        string danglingFileLink = Path.Combine(fixture.Root, "dangling-file-link");
        string danglingFolderLink = Path.Combine(fixture.Root, "dangling-folder-link");
        File.CreateSymbolicLink(fileLink, file);
        Directory.CreateSymbolicLink(folderLink, folder);
        File.CreateSymbolicLink(danglingFileLink, Path.Combine(fixture.Root, "nowhere.txt"));
        Directory.CreateSymbolicLink(danglingFolderLink, Path.Combine(fixture.Root, "nowhere-folder"));
        string[] paths = [file, folder, fileLink, folderLink, danglingFileLink, danglingFolderLink, Path.Combine(fixture.Root, "missing")];

        foreach (string path in paths)
        {
            Assert.Equal(new FileInfo(path).LinkTarget is not null, PlanFinalize.IsLink(DocsPorts.Files, path));
        }

        Assert.True(PlanFinalize.IsLink(DocsPorts.Files, danglingFileLink));
        Assert.True(PlanFinalize.IsLink(DocsPorts.Files, danglingFolderLink));
        Assert.False(PlanFinalize.IsLink(DocsPorts.Files, Path.Combine(fixture.Root, "missing")));
    }
}
