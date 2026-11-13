using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs packet</c> and <c>docs tasks --list</c>: the machine hands a worker its own task's packet, so a
/// conductor passes an id and no one retypes 33 KB of brief.
/// </summary>
public sealed class DocsPacketCommandTests
{
    private static DocFixture Ready()
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        new DocsTasksCommand().Execute(
            null!,
            new DocsTasksSettings { Path = Path.Combine(fixture.Root, PlanTaskFileProbe.PlanPath), Format = "json", Write = true },
            CancellationToken.None);
        return fixture;
    }

    private static (int Exit, string Output) Packet(DocFixture fixture, string task, string? plan = null, string? prior = null)
    {
        DocsPacketSettings settings = new() { Task = task, Plan = plan, PriorResults = prior, WorkingDirectory = fixture.Root };
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => new DocsPacketCommand().Execute(null!, settings, CancellationToken.None));
        return (execution.Result, execution.Output);
    }

    [Theory]
    [InlineData("T1")]
    [InlineData("t1")]
    [InlineData("task 1")]
    [InlineData("Task-1")]
    [InlineData("1")]
    public void ThePacketIsPrintedForAnyNaturalSpellingOfTheTaskIdWithNoPlanArgument(string typed)
    {
        using DocFixture fixture = Ready();

        (int exit, string output) = Packet(fixture, typed);

        Assert.True(exit == 0, output);
        Assert.StartsWith("Task T1", output, StringComparison.Ordinal);
        Assert.Contains("Worktree: " + fixture.Root, output, StringComparison.Ordinal);
        Assert.DoesNotContain("{worktree}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("{prior_results}", output, StringComparison.Ordinal);
        Assert.Contains(PlanTaskPacket.NoPriorResults, output, StringComparison.Ordinal);
        Assert.DoesNotContain("Task T2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PriorResultsFromAFileFillTheirSection()
    {
        using DocFixture fixture = Ready();
        string results = Path.Combine(fixture.Root, "results.txt");
        File.WriteAllText(results, "T1 reported: built, 4 tests green.");

        (int exit, string output) = Packet(fixture, "T2", prior: results);

        Assert.True(exit == 0, output);
        Assert.Contains("T1 reported: built, 4 tests green.", output, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanTaskPacket.NoPriorResults, output, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleTaskFileIsRefusedWithTheCommandThatFixesIt()
    {
        using DocFixture fixture = Ready();
        fixture.Write(
            PlanTaskFileProbe.PlanPath,
            PlanTaskFileProbe.PlanMarkdown().Replace("The task file validator", "The renamed validator", StringComparison.Ordinal));

        (int exit, string output) = Packet(fixture, "T1");

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-TASKS-CLI-004", output, StringComparison.Ordinal);
        Assert.Contains("docs tasks " + PlanTaskFileProbe.PlanPath + " --write", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Worktree:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownTaskListsTheTasksThePlanHas()
    {
        using DocFixture fixture = Ready();

        (int exit, string output) = Packet(fixture, "T9");

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-TASKS-CLI-003", output, StringComparison.Ordinal);
        Assert.Contains("T1, T2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralActivePlansAreListedAndNoneIsGuessed()
    {
        using DocFixture fixture = Ready();
        string folder = Path.GetDirectoryName(Path.Combine(fixture.Root, PlanTaskFileProbe.PlanPath.Replace('/', Path.DirectorySeparatorChar)))!;
        File.WriteAllText(Path.Combine(folder, "2026-11-08-other-plan.md"), "# Other\n\n**Status: In progress.**\n");

        (int exit, string output) = Packet(fixture, "T1");

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-TASKS-CLI-002", output, StringComparison.Ordinal);
        Assert.Contains("2026-11-08-other-plan.md", output, StringComparison.Ordinal);
        Assert.Contains(PlanTaskFileProbe.PlanPath, output, StringComparison.Ordinal);

        (int named, string text) = Packet(fixture, "T1", plan: PlanTaskFileProbe.PlanPath);
        Assert.True(named == 0, text);
    }

    [Fact]
    public void NoActivePlanSaysSoAndTellsTheWorkerNotToHuntForOne()
    {
        using DocFixture fixture = Ready();
        string plan = Path.Combine(fixture.Root, PlanTaskFileProbe.PlanPath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(plan, File.ReadAllText(plan).Replace("**Status: Ready.**", "**Status: Draft.**", StringComparison.Ordinal));

        (int exit, string output) = Packet(fixture, "T1");

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-TASKS-CLI-002", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ListPrintsOneLinePerTaskAndNothingElse()
    {
        using DocFixture fixture = Ready();
        DocsTasksSettings settings = new() { Path = Path.Combine(fixture.Root, PlanTaskFileProbe.PlanPath), List = true };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => new DocsTasksCommand().Execute(null!, settings, CancellationToken.None));

        Assert.True(execution.Result == 0, execution.Output);
        string[] lines = execution.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("T1  role=", lines[0], StringComparison.Ordinal);
        Assert.Contains("depends-on=", lines[1], StringComparison.Ordinal);
        Assert.Contains("files=", lines[1], StringComparison.Ordinal);
    }
}
