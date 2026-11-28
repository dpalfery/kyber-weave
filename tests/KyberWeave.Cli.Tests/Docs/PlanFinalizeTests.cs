using System.Globalization;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;
using Xunit;
using static KyberWeave.Tests.PlanFinalizeFixture;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs finalize</c>: the mechanical half of finalizing an approved Draft plan. A Draft plan with no open decision
/// is set to Ready with its approval recorded and its task file written; every refusal changes nothing. Each test
/// builds the repository in memory, because the step validates the files it wrote.
/// </summary>
public sealed class PlanFinalizeTests
{
    /// <summary>The probe plan as an autonomous Draft awaiting approval.</summary>
    private static async Task<(int Exit, string Output)> RunAsync(
        DocFixture fixture,
        string mode = "autonomous",
        string? approval = Approval,
        string path = PlanTaskFileProbe.PlanPath)
    {
        DocsFinalizeSettings settings = new()
        {
            Path = fixture.FullPath(path),
            Format = "json",
            ApprovalMode = mode,
            Approval = approval,
            Date = Date
        };

        TestConsole console = new() { EmitAnsiSequences = false };
        int exit = await DocsTestCommands.Finalize(Services(fixture), console).ExecuteAsync(null!, settings, CancellationToken.None);
        return (exit, console.Output);
    }

    /// <summary>The host's services over the in-memory checkout; git answers as it does in a folder that is no repository.</summary>
    private static IServiceProvider Services(DocFixture fixture) =>
        DocsMemoryServicesD.Services(fixture, services =>
            services.AddSingleton<IBoundedProcessExecutor>(NotAGitRepository()));

    private static ScriptedProcessExecutor NotAGitRepository() =>
        new(_ => new ProcessResult(128, string.Empty, "fatal: not a git repository"));

    private static string Read(DocFixture fixture, string relative) =>
        fixture.Memory!.ReadAllText(fixture.FullPath(relative));

    private static bool Exists(DocFixture fixture, string relative) =>
        fixture.Memory!.FileExists(fixture.FullPath(relative));

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
    public async Task FinalizingSetsReadyRecordsTheApprovalAndWritesAValidTaskFile()
    {
        using DocFixture fixture = RepositoryInMemory();

        (int exit, string output) = await RunAsync(fixture);

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
    public async Task FinalizingWritesExactlyOneYamlTaskFileEvenWhenALegacyJsonSitsBesideIt()
    {
        using DocFixture fixture = RepositoryInMemory();
        PlanTaskFileOutcome seeded = await new PlanTaskFileGenerator(
            fixture.Root, KyberWeaveConfigDefaults.Product, fixture.Store)
            .RunAsync(PlanTaskFileProbe.PlanPath, PlanTaskFileMode.Write, CancellationToken.None);
        Assert.False(seeded.Report.HasErrors, string.Join("; ", seeded.Report.Items.Select(item => item.Message)));
        fixture.Memory!.WriteAllText(
            Path.Combine(fixture.Root, PlanTaskFileProbe.LegacyTaskFilePath.Replace('/', Path.DirectorySeparatorChar)),
            PlanTaskFileProbe.LegacyJsonTaskFile());

        (int exit, string output) = await RunAsync(fixture);

        Assert.True(exit == 0, output);
        Assert.True(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.LegacyTaskFilePath));
    }

    [Fact]
    public async Task ASupervisedApprovalIsRecordedAsTheOwners()
    {
        using DocFixture fixture = RepositoryInMemory(DraftPlan(autonomous: false));

        (int exit, string output) = await RunAsync(fixture, mode: "supervised", approval: "owner chose approve and execute in chat");

        Assert.True(exit == 0, output);
        Assert.Contains("ANSWERED: owner approval, owner chose approve and execute in chat, 2026-11-13 |",
            Read(fixture, PlanTaskFileProbe.PlanPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableExistingTaskFileIsARefusalThatChangesNothing()
    {
        using DocFixture fixture = RepositoryInMemory();
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);
        string taskPath = fixture.FullPath(PlanTaskFileProbe.TaskFilePath);
        _ = fixture.Write(PlanTaskFileProbe.TaskFilePath, "{}");
        fixture.Memory!.FailWhen(
            operation => operation.Path == taskPath && operation.Name is "ReadAllText" or "ReadAllBytes" or "OpenRead" or "GetLength",
            new UnauthorizedAccessException("Access to the path is denied."));

        (int exit, string output) = await RunAsync(fixture);

        Assert.Equal(1, exit);
        Assert.Contains("could not be read", output, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
        Assert.Equal(index, Read(fixture, PlanTaskFileProbe.PlanIndexPath));
    }

    [Fact]
    public async Task ASecondRunReportsAlreadyReadyAndChangesNothing()
    {
        using DocFixture fixture = RepositoryInMemory();
        Assert.Equal(0, (await RunAsync(fixture)).Exit);
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string taskFile = Read(fixture, PlanTaskFileProbe.TaskFilePath);

        (int exit, string output) = await RunAsync(fixture);

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
    public async Task AMissingOrMultiLineApprovalIsRefused(string? approval)
    {
        using DocFixture fixture = RepositoryInMemory();
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        AssertRefusedUnchanged(fixture, await RunAsync(fixture, approval: approval), "KW-DOC-FINALIZE-003", plan, index);
    }

    [Fact]
    public async Task AnAutonomousApprovalOfASupervisedPlanIsRefused()
    {
        using DocFixture fixture = RepositoryInMemory(DraftPlan(autonomous: false));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        AssertRefusedUnchanged(fixture, await RunAsync(fixture), "KW-DOC-FINALIZE-003", plan, index);
    }

    [Fact]
    public async Task AnAutonomousApprovalOfAPlanThatDeclaresNoAutonomyIsAccepted()
    {
        using DocFixture fixture = RepositoryInMemory(DraftPlan().Replace("autonomy: autonomous\n", string.Empty, StringComparison.Ordinal));

        (int exit, string output) = await RunAsync(fixture);

        Assert.True(exit == 0, output);
    }

    /// <summary>A plan that derives a task file but then fails validation is restored byte for byte.</summary>
    [Fact]
    public async Task AFinalizedPlanThatFailsValidationIsRestoredAndGoesBackToTheArchitect()
    {
        using DocFixture fixture = RepositoryInMemory(DraftPlan().Replace("owner: Maintainers\n", string.Empty, StringComparison.Ordinal));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        (int Exit, string Output) run = await RunAsync(fixture);

        AssertRefusedUnchanged(fixture, run, "KW-DOC-FINALIZE-005", plan, index);
        Assert.Contains("undone", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlanThatIsNotDraftIsRefused()
    {
        using DocFixture fixture = RepositoryInMemory(DraftPlan().Replace("**Status: Draft.**", "**Status: Blocked.**", StringComparison.Ordinal));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        AssertRefusedUnchanged(fixture, await RunAsync(fixture), "KW-DOC-FINALIZE-002", plan, index);
    }

    [Fact]
    public async Task AnOpenDecisionIsRefusedAndNamed()
    {
        using DocFixture fixture = RepositoryInMemory(DraftPlan().Replace("| user: explicit decision 2026-11-07 |", "| OPEN |", StringComparison.Ordinal));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        (int Exit, string Output) run = await RunAsync(fixture);

        AssertRefusedUnchanged(fixture, run, "KW-DOC-FINALIZE-004", plan, index);
        Assert.Contains("D1", run.Output, StringComparison.Ordinal);
    }

    /// <summary>Under autonomy a decision the architect left RECOMMENDED is accepted as recommended, not sent back.</summary>
    [Fact]
    public async Task AutonomyAcceptsARecommendedDecisionAndRecordsIt()
    {
        using DocFixture fixture = RepositoryInMemory(DraftPlan().Replace("| user: explicit decision 2026-11-07 |", "| RECOMMENDED |", StringComparison.Ordinal));

        (int exit, string output) = await RunAsync(fixture);

        Assert.True(exit == 0, output);
        Assert.Contains($"| autonomous: recommendation accepted, {Date} |", Read(fixture, PlanTaskFileProbe.PlanPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASupervisedRunDoesNotAcceptARecommendedDecision()
    {
        using DocFixture fixture = RepositoryInMemory(DraftPlan(autonomous: false).Replace("| user: explicit decision 2026-11-07 |", "| RECOMMENDED |", StringComparison.Ordinal));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        AssertRefusedUnchanged(fixture, await RunAsync(fixture, mode: "supervised", approval: "owner approved"), "KW-DOC-FINALIZE-004", plan, index);
    }

    /// <summary>The architect numbers its own approve-and-execute row, so the gate is found by its question, not only an AP id.</summary>
    [Fact]
    public async Task AnOpenApproveAndExecuteRowWithAnyIdIsAnsweredInPlace()
    {
        string gate = "| D9 | Approve and execute this plan as written? | Approved as written. | The gate. | OPEN |";
        using DocFixture fixture = RepositoryInMemory(DraftPlan().Replace("| user: explicit decision 2026-11-07 |", "| user: explicit decision 2026-11-07 |\n" + gate, StringComparison.Ordinal));

        (int exit, string output) = await RunAsync(fixture);

        Assert.True(exit == 0, output);
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        Assert.Contains($"| ANSWERED: autonomous: recommendation accepted, {Approval}, {Date} |", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("| AP1 |", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlanTheTaskFileCannotBeWrittenFromGoesBackToTheArchitect()
    {
        using DocFixture fixture = RepositoryInMemory(DraftPlan().Replace("- **Required skills:** `test-dev`", string.Empty, StringComparison.Ordinal));
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        (int Exit, string Output) run = await RunAsync(fixture);

        AssertRefusedUnchanged(fixture, run, "KW-DOC-FINALIZE-005", plan, index);
        Assert.Contains("architect", run.Output, StringComparison.Ordinal);
    }

    /// <summary>A folder where the plan index should be: reading it throws, and must become a finding, not a crash.</summary>
    private static void ReplaceIndexWithFolder(DocFixture fixture)
    {
        string index = fixture.FullPath(PlanTaskFileProbe.PlanIndexPath);
        fixture.Memory!.DeleteFile(index);
        fixture.Memory!.CreateDirectory(index);
    }

    [Fact]
    public async Task AnUnreadablePlanIndexIsARefusalThatChangesNothing()
    {
        using DocFixture fixture = RepositoryInMemory();
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        ReplaceIndexWithFolder(fixture);

        (int exit, string output) = await RunAsync(fixture);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-FINALIZE-005", output, StringComparison.Ordinal);
        Assert.Contains("The plan index could not be read", output, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    [Fact]
    public async Task AnAlreadyReadyPlanWhosePlanIndexIsUnreadableIsAFindingNotACrash()
    {
        using DocFixture fixture = RepositoryInMemory();
        Assert.Equal(0, (await RunAsync(fixture)).Exit);
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        ReplaceIndexWithFolder(fixture);

        (int exit, string output) = await RunAsync(fixture);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-FINALIZE-005", output, StringComparison.Ordinal);
        Assert.Contains("The plan index could not be read, so nothing was committed", output, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
    }

    [Fact]
    public async Task APathThatIsNotAPlanIsRefused()
    {
        using DocFixture fixture = RepositoryInMemory();
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);
        string index = Read(fixture, PlanTaskFileProbe.PlanIndexPath);

        AssertRefusedUnchanged(fixture, await RunAsync(fixture, path: "src/Placeholder.cs"), "KW-DOC-FINALIZE-001", plan, index);
        Assert.Equal("// the repository's existing source", Read(fixture, "src/Placeholder.cs"));
    }

    [Theory]
    [InlineData(PlanTaskFileProbe.PlanPath)]
    [InlineData(PlanTaskFileProbe.PlanIndexPath)]
    public async Task ALinkAtThePlanOrIndexPathIsRefusedAndNothingIsWrittenThroughIt(string linkedPath)
    {
        using DocFixture fixture = RepositoryInMemory();
        string linked = fixture.FullPath(linkedPath);
        string target = fixture.FullPath("owner-copy.md");
        string before = fixture.Memory!.ReadAllText(linked);
        fixture.Memory!.MoveFile(linked, target);
        fixture.Memory!.CreateFileLink(linked, target);

        (int exit, string output) = await RunAsync(fixture);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-FINALIZE-005", output, StringComparison.Ordinal);
        Assert.Contains("is a symbolic link", output, StringComparison.Ordinal);
        Assert.Equal(before, fixture.Memory!.ReadAllText(target));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }
}
