using KyberWeave.Core;
using KyberWeave.Application.Review;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs packet</c> and <c>docs tasks --list</c>: the machine hands a worker its own task's packet, so a
/// conductor passes an id and no one retypes 33 KB of brief.
/// </summary>
public sealed class DocsPacketCommandTests
{
    /// <summary>The host's services over the fixture's in-memory checkout; git answers as it does outside a repository.</summary>
    private static IServiceProvider Services(DocFixture fixture, Action<IServiceCollection>? configure = null) =>
        DocsMemoryServicesD.Services(fixture, services =>
        {
            services.AddSingleton<IGitRepositoryFacts>(new FakeGitRepositoryFacts());
            configure?.Invoke(services);
        });

    private static DocFixture Ready() => Written(PlanTaskFileProbe.CorpusInMemory());

    private static DocFixture Written(DocFixture fixture)
    {
        _ = fixture.EnsureDirectory(".git");
        TestConsole console = new() { EmitAnsiSequences = false };
        _ = DocsTestCommands.Tasks(Services(fixture), console).Execute(
            null!,
            new DocsTasksSettings { Path = fixture.FullPath(PlanTaskFileProbe.PlanPath), Format = "json", Write = true },
            CancellationToken.None);
        return fixture;
    }

    /// <summary>A checkout beside <paramref name="sibling"/> on the same in-memory file system.</summary>
    private static DocFixture ReadyBeside(DocFixture sibling, string folder) =>
        Written(PlanTaskFileProbe.CorpusIn(DocFixture.CreateInMemoryBeside(sibling, folder)));

    private static (int Exit, string Output) Packet(
        DocFixture fixture, string task, string? plan = null, string? prior = null, IServiceProvider? services = null, params string[] worktrees)
    {
        DocsPacketSettings settings = new()
        {
            Task = task,
            Plan = plan,
            PriorResults = prior,
            WorkingDirectory = fixture.Root,
            ListWorktrees = _ => [fixture.Root, .. worktrees],
        };
        TestConsole console = new() { EmitAnsiSequences = false };
        int exit = DocsTestCommands.Packet(services ?? Services(fixture), console).Execute(null!, settings, CancellationToken.None);
        return (exit, console.Output);
    }

    /// <summary>The CodeGraph ports are the injected factory's, so a checkout outside this one is read the same way.</summary>
    [Fact]
    public void TheCodeGraphPortsComeFromTheInjectedFactory()
    {
        using DocFixture fixture = Ready();
        FakeCodeGraphPortsFactory codeGraph = new(FakeCodeGraphResolver.WithNodes());
        IServiceProvider services = Services(fixture, collection => collection.AddSingleton<ICodeGraphPortsFactory>(codeGraph));

        (int exit, string output) = Packet(fixture, "T1", services: services);

        Assert.True(exit == 0, output);
        Assert.Contains(codeGraph.Roots, root => Path.GetFullPath(root) == Path.GetFullPath(fixture.Root));
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
        Assert.StartsWith("<task>\nTask T1", output, StringComparison.Ordinal);
        Assert.Contains("Worktree: " + fixture.Root, output, StringComparison.Ordinal);
        Assert.DoesNotContain("{worktree}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("{prior_results}", output, StringComparison.Ordinal);
        Assert.Contains(PlanTaskPacket.NoPriorResults, output, StringComparison.Ordinal);
        Assert.DoesNotContain("Task T2", output, StringComparison.Ordinal);
    }

    /// <summary>The packet carries the task's resolved test command, so the worker neither fills placeholders nor guesses.</summary>
    [Fact]
    public void ThePacketPrintsTheTasksResolvedTestCommand()
    {
        using DocFixture fixture = Ready();

        (int exit, string output) = Packet(fixture, "T1");

        Assert.True(exit == 0, output);
        Assert.Contains(PacketTestCommandRenderer.Heading, output, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName~PlanTaskFileValidatorTests", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PriorResultsFromAFileInTheScratchAreaFillTheirSection()
    {
        using DocFixture fixture = Ready();
        string results = fixture.FullPath(ScratchLayout.Root + "/results.txt");
        _ = fixture.Write(ScratchLayout.Root + "/results.txt", "T1 reported: built, 4 tests green.");

        (int exit, string output) = Packet(fixture, "T2", prior: results);

        Assert.True(exit == 0, output);
        Assert.Contains("T1 reported: built, 4 tests green.", output, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanTaskPacket.NoPriorResults, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A prior-results file outside the run's scratch area is refused by this command under its own code, not
    /// reported as a stale task file after the prompt service refuses it.
    /// </summary>
    [Fact]
    public void APriorResultsFileOutsideTheScratchAreaIsRefusedUnderItsOwnCode()
    {
        using DocFixture fixture = Ready();
        string results = fixture.FullPath("results.txt");
        _ = fixture.Write("results.txt", "a file outside the scratch area");

        (int exit, string output) = Packet(fixture, "T2", prior: results);

        Assert.Equal(1, exit);
        Assert.Contains(DocsPacketCommand.PriorResultsUnreadable, output, StringComparison.Ordinal);
        Assert.Contains(ScratchLayout.Root, output, StringComparison.Ordinal);
        Assert.DoesNotContain(DocsPacketCommand.TaskFileStale, output, StringComparison.Ordinal);
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
        _ = fixture.Write(
            Path.GetDirectoryName(PlanTaskFileProbe.PlanPath)!.Replace('\\', '/') + "/2026-11-08-other-plan.md",
            "# Other\n\n**Status: In progress.**\n");

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
        _ = fixture.Write(
            PlanTaskFileProbe.PlanPath,
            fixture.ReadText(PlanTaskFileProbe.PlanPath).Replace("**Status: Ready.**", "**Status: Draft.**", StringComparison.Ordinal));

        (int exit, string output) = Packet(fixture, "T1");

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-TASKS-CLI-002", output, StringComparison.Ordinal);
    }

    private static DocFixture CanonicalWithoutPlan()
    {
        DocFixture fixture = Ready();
        _ = fixture.Write(
            PlanTaskFileProbe.PlanPath,
            fixture.ReadText(PlanTaskFileProbe.PlanPath).Replace("**Status: Ready.**", "**Status: Draft.**", StringComparison.Ordinal));
        return fixture;
    }

    [Fact]
    public void ARunFromTheCanonicalCheckoutPrintsThePacketOfTheOneWorktreeThatHasTheTask()
    {
        using DocFixture canonical = CanonicalWithoutPlan();
        using DocFixture worktree = ReadyBeside(canonical, "worktree");

        (int exit, string output) = Packet(canonical, "T1", worktrees: worktree.Root);

        Assert.True(exit == 0, output);
        Assert.Contains("Worktree: " + worktree.Root, output, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralWorktreesWithTheTaskAreListedWithTheCommandToRunFromEach()
    {
        using DocFixture canonical = CanonicalWithoutPlan();
        using DocFixture first = ReadyBeside(canonical, "first");
        using DocFixture second = ReadyBeside(canonical, "second");

        (int exit, string output) = Packet(canonical, "T1", worktrees: [first.Root, second.Root]);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-TASKS-CLI-002", output, StringComparison.Ordinal);
        Assert.Contains("cd " + first.Root + " && kyber-weave docs packet T1", output, StringComparison.Ordinal);
        Assert.Contains("cd " + second.Root + " && kyber-weave docs packet T1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Worktree:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorktreeWithoutTheTaskIsNotAMatchAndTheFindingNamesTheDirectorySearched()
    {
        using DocFixture canonical = CanonicalWithoutPlan();
        using DocFixture worktree = ReadyBeside(canonical, "worktree");

        (int exit, string output) = Packet(canonical, "T9", worktrees: worktree.Root);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-TASKS-CLI-002", output, StringComparison.Ordinal);
        Assert.Contains(canonical.Root, output, StringComparison.Ordinal);
    }

    [Fact]
    public void ListPrintsOneLinePerTaskAndNothingElse()
    {
        using DocFixture fixture = Ready();
        DocsTasksSettings settings = new() { Path = fixture.FullPath(PlanTaskFileProbe.PlanPath), List = true };
        TestConsole console = new() { EmitAnsiSequences = false };

        int exit = DocsTestCommands.Tasks(Services(fixture), console).Execute(null!, settings, CancellationToken.None);

        Assert.True(exit == 0, console.Output);
        string[] lines = console.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("T1  needs=", lines[0], StringComparison.Ordinal);
        Assert.Contains("depends-on=", lines[1], StringComparison.Ordinal);
        Assert.Contains("files=", lines[1], StringComparison.Ordinal);
    }
}
