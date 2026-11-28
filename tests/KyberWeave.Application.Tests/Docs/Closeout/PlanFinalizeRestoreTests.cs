using System.Globalization;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static KyberWeave.Tests.PlanFinalizeFixture;

namespace KyberWeave.Tests;

/// <summary>
/// The finalize step's own behaviour: the ready-queue summary, what a refusal restores, how a link or a folder in a
/// file's place is treated, and a plan that vanishes between the checks. Runs over an in-memory checkout.
/// </summary>
public sealed class PlanFinalizeRestoreTests
{
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

    private static PlanFinalize Finalizer(DocFixture fixture, Action<string>? afterActivePlanCheck = null) =>
        new(fixture.Root, KyberWeaveConfigDefaults.Product,
            () => fixture.Loader(KyberWeaveConfigDefaults.Product.Ontology),
            fixture.Store, new ScriptedProcessExecutor(_ => new ProcessResult(128, string.Empty, "fatal: not a git repository")),
            DocsMemoryServicesD.Services(fixture).GetRequiredService<IDocsSchemaGate>(), fixture.Paths, fixture.Environment)
        {
            AfterActivePlanCheck = afterActivePlanCheck
        };

    private static string Read(DocFixture fixture, string relative) => fixture.ReadText(relative);

    private static bool Exists(DocFixture fixture, string relative) => fixture.Exists(relative);

    private static string Absolute(DocFixture fixture, string relative) =>
        fixture.FullPath(relative);

    [Fact]
    public async Task APlanThatVanishesAfterTheActiveCheckIsRefusedAsMissingAndNothingIsWritten()
    {
        using DocFixture fixture = RepositoryInMemory();

        PlanFinalizeOutcome outcome = await Finalizer(fixture, path => fixture.Memory!.DeleteFile(path))
            .RunAsync(PlanTaskFileProbe.PlanPath, PlanApprovalMode.Autonomous, Approval, DateOnly.Parse(Date, CultureInfo.InvariantCulture), CancellationToken.None);

        Assert.False(outcome.Finalized);
        Diagnostic refusal = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanFinalize.NotAnActivePlan, refusal.Code);
        Assert.Contains("does not exist", refusal.Message, StringComparison.Ordinal);
        Assert.False(Exists(fixture, PlanTaskFileProbe.PlanPath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    /// <summary>A folder in the plan's place passes the active check, then throws on read: a finding, not a crash.</summary>
    [Fact]
    public async Task APlanThatCannotBeReadAfterTheActiveCheckIsAFindingNotACrash()
    {
        using DocFixture fixture = RepositoryInMemory();
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        PlanFinalizeOutcome outcome = await Finalizer(fixture, path =>
            {
                fixture.Memory!.DeleteFile(path);
                fixture.Memory!.CreateDirectory(path);
            })
            .RunAsync(PlanTaskFileProbe.PlanPath, PlanApprovalMode.Autonomous, Approval, DateOnly.Parse(Date, CultureInfo.InvariantCulture), CancellationToken.None);

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
        using DocFixture fixture = RepositoryInMemory();
        string gone = Absolute(fixture, "6-Docs/vanished/plan.tasks.json");

        PlanFinalize.DeleteIfPresent(fixture.Memory!, gone);

        Assert.False(fixture.Memory!.FileExists(gone));
        Assert.False(fixture.Memory!.DirectoryExists(Path.GetDirectoryName(gone)!));
        Assert.True(Exists(fixture, PlanTaskFileProbe.PlanPath));
    }

    [Fact]
    public void RestoringAFileThatCannotBeDeletedIsReportedNotThrown()
    {
        using DocFixture fixture = RepositoryInMemory();
        string folderWhereTheFileBelongs = Absolute(fixture, PlanTaskFileProbe.TaskFilePath);
        fixture.Memory!.CreateDirectory(folderWhereTheFileBelongs);

        bool gone = PlanFinalize.DeleteIfPresent(fixture.Memory!, folderWhereTheFileBelongs);

        Assert.False(gone);
        Assert.True(fixture.Memory!.DirectoryExists(folderWhereTheFileBelongs));
    }

    [Fact]
    public void RestoreNamesEveryFileItCannotUndoAndStillUndoesTheRest()
    {
        using DocFixture fixture = RepositoryInMemory();
        string plan = PlanTaskFileProbe.PlanPath;
        string index = PlanTaskFileProbe.PlanIndexPath;
        string indexBefore = Read(fixture, index);
        string finalizedPlan = Read(fixture, plan);
        // A folder where the plan and the task file belong: neither can be written or deleted as a file.
        fixture.Memory!.DeleteFile(Absolute(fixture, plan));
        fixture.Memory!.CreateDirectory(Absolute(fixture, plan));
        fixture.Memory!.CreateDirectory(Absolute(fixture, PlanTaskFileProbe.TaskFilePath));
        fixture.Memory!.WriteAllText(Absolute(fixture, index), "changed by finalize");

        List<string> notUndone = Finalizer(fixture).Restore(
            plan, finalizedPlan, index, indexBefore, PlanTaskFileProbe.TaskFilePath, taskFileBefore: null);

        Assert.Equal([plan, PlanTaskFileProbe.TaskFilePath], notUndone);
        Assert.Equal(indexBefore, Read(fixture, index));
    }

    [Fact]
    public void RestoreReportsNothingWhenEveryFileIsPutBack()
    {
        using DocFixture fixture = RepositoryInMemory();
        string planBefore = Read(fixture, PlanTaskFileProbe.PlanPath);
        fixture.Memory!.WriteAllText(Absolute(fixture, PlanTaskFileProbe.PlanPath), "changed by finalize");
        fixture.Memory!.WriteAllText(Absolute(fixture, PlanTaskFileProbe.TaskFilePath), "{}");

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
        using DocFixture fixture = RepositoryInMemory();

        Assert.True(PlanFinalize.DeleteIfPresent(fixture.Memory!, Absolute(fixture, "6-Docs/vanished/plan.tasks.json")));
        Assert.True(PlanFinalize.DeleteIfPresent(fixture.Memory!, Absolute(fixture, "6-Docs/never-there.json")));
    }

    [Fact]
    public void RestoringDeletesAnExistingFileButNotItsFolder()
    {
        using DocFixture fixture = RepositoryInMemory();
        string file = Absolute(fixture, PlanTaskFileProbe.TaskFilePath);
        fixture.Memory!.WriteAllText(file, "{}");

        PlanFinalize.DeleteIfPresent(fixture.Memory!, file);

        Assert.False(fixture.Memory!.FileExists(file));
        Assert.True(fixture.Memory!.DirectoryExists(Path.GetDirectoryName(file)!));
    }

    [Fact]
    public void RestoreLeavesADanglingLinkAtTheTaskFilePathAlone()
    {
        using DocFixture fixture = RepositoryInMemory();
        string taskFile = Absolute(fixture, PlanTaskFileProbe.TaskFilePath);
        fixture.Memory!.CreateFileLink(taskFile, Path.Combine(fixture.Root, "nowhere.json"));
        string planBefore = Read(fixture, PlanTaskFileProbe.PlanPath);

        List<string> notUndone = Finalizer(fixture).Restore(
            PlanTaskFileProbe.PlanPath, planBefore, planIndex: null, indexText: null, PlanTaskFileProbe.TaskFilePath, taskFileBefore: null);

        Assert.Empty(notUndone);
        Assert.NotNull(fixture.Memory!.GetLinkTarget(taskFile));
    }

    [Fact]
    public void RestoreNeverWritesThroughALinkAtATaskFilePlanOrIndexPath()
    {
        using DocFixture fixture = RepositoryInMemory();
        string outside = Path.Combine(fixture.Root, "outside.json");
        fixture.Memory!.WriteAllText(outside, "owner's bytes");
        string taskFile = Absolute(fixture, PlanTaskFileProbe.TaskFilePath);
        fixture.Memory!.CreateFileLink(taskFile, outside);

        List<string> notUndone = Finalizer(fixture).Restore(
            PlanTaskFileProbe.PlanPath, Read(fixture, PlanTaskFileProbe.PlanPath), planIndex: null, indexText: null,
            PlanTaskFileProbe.TaskFilePath, taskFileBefore: "{\"before\":true}");

        Assert.Equal([PlanTaskFileProbe.TaskFilePath], notUndone);
        Assert.Equal("owner's bytes", fixture.Memory!.ReadAllText(outside));
    }

    [Fact]
    public void TheReadyQueueIsListedFromAReadableTaskFileAndSaysNothingExtra()
    {
        using DocFixture fixture = RepositoryInMemory();
        fixture.Memory!.WriteAllText(Absolute(fixture, PlanTaskFileProbe.TaskFilePath), "{\"schema-version\":1,\"tasks\":[]}");
        DiagnosticReport report = new();

        string? queue = Finalizer(fixture).ReadyQueue(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.PlanPath, report);

        Assert.Equal("(no tasks)", queue);
        Assert.Empty(report.Items);
    }

    [Fact]
    public void ADroppedReadyQueueIsReportedWithItsCause()
    {
        using DocFixture fixture = RepositoryInMemory();
        // A folder where the task file belongs: it is there, but cannot be read as a file.
        fixture.Memory!.CreateDirectory(Absolute(fixture, PlanTaskFileProbe.TaskFilePath));
        DiagnosticReport unreadable = new();
        DiagnosticReport corrupt = new();
        DiagnosticReport absent = new();

        Assert.Null(Finalizer(fixture).ReadyQueue(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.PlanPath, unreadable));
        fixture.Memory!.DeleteTreeNoFollow(Absolute(fixture, PlanTaskFileProbe.TaskFilePath));
        Assert.Null(Finalizer(fixture).ReadyQueue(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.PlanPath, absent));
        fixture.Memory!.WriteAllText(Absolute(fixture, PlanTaskFileProbe.TaskFilePath), "not json");
        Assert.Null(Finalizer(fixture).ReadyQueue(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.PlanPath, corrupt));

        Diagnostic cause = Assert.Single(unreadable.Items);
        Assert.Equal(Severity.Warning, cause.Severity);
        Assert.Contains("could not be read", cause.Message, StringComparison.Ordinal);
        Assert.Contains(PlanTaskFileProbe.TaskFilePath, cause.Message, StringComparison.Ordinal);
        Assert.Contains("it is not there", Assert.Single(absent.Items).Message, StringComparison.Ordinal);
        Assert.Contains("not a valid task file", Assert.Single(corrupt.Items).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsLinkIsTrueForEveryKindOfLinkAndFalseForAnythingElse()
    {
        using DocFixture fixture = RepositoryInMemory();
        string file = Path.Combine(fixture.Root, "file.txt");
        string folder = Path.Combine(fixture.Root, "folder");
        fixture.Memory!.WriteAllText(file, "x");
        fixture.Memory!.CreateDirectory(folder);
        string fileLink = Path.Combine(fixture.Root, "file-link");
        string folderLink = Path.Combine(fixture.Root, "folder-link");
        string danglingFileLink = Path.Combine(fixture.Root, "dangling-file-link");
        string danglingFolderLink = Path.Combine(fixture.Root, "dangling-folder-link");
        fixture.Memory!.CreateFileLink(fileLink, file);
        fixture.Memory!.CreateDirectoryLink(folderLink, folder);
        fixture.Memory!.CreateFileLink(danglingFileLink, Path.Combine(fixture.Root, "nowhere.txt"));
        fixture.Memory!.CreateDirectoryLink(danglingFolderLink, Path.Combine(fixture.Root, "nowhere-folder"));
        foreach (string link in new[] { fileLink, folderLink })
        {
            Assert.True(PlanFinalize.IsLink(fixture.Memory!, link));
        }

        foreach (string notALink in new[] { file, folder, Path.Combine(fixture.Root, "missing") })
        {
            Assert.False(PlanFinalize.IsLink(fixture.Memory!, notALink));
        }

        Assert.True(PlanFinalize.IsLink(fixture.Memory!, danglingFileLink));
        Assert.True(PlanFinalize.IsLink(fixture.Memory!, danglingFolderLink));
        Assert.False(PlanFinalize.IsLink(fixture.Memory!, Path.Combine(fixture.Root, "missing")));
    }
}
