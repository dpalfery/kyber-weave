using KyberWeave.Cli.Commands.Run;
using KyberWeave.Contracts.Services;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What <c>run integrate</c> passes to the service and what it prints: the request the environment supplies, the
/// one-line outcome, and the exit code the worker branches on.
/// </summary>
public sealed class RunIntegrateCommandTests
{
    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly StubIntegrate _integrate = new();
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/work/task", homeDirectory: "/home/dev");

    public RunIntegrateCommandTests() => _console.Profile.Width = 400;

    [Fact]
    public void TheRequestCarriesTheTaskTheCurrentDirectoryAndTheOperatorsHome()
    {
        _integrate.Outcome = new RunIntegrateOutcome(RunIntegrateStatus.Integrated, "abc1234");

        int exit = Run(new RunIntegrateSettings { Task = "T3" });

        RunIntegrateRequest request = Assert.Single(_integrate.Requests);
        Assert.Equal("T3", request.TaskId);
        Assert.Null(request.TaskWorktree);
        Assert.Null(request.RunWorktree);
        Assert.Equal("/work/task", request.WorkingDirectory);
        Assert.Equal("/home/dev", request.HomeDirectory);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void TheNamedWorktreesArePassedThrough()
    {
        _integrate.Outcome = new RunIntegrateOutcome(RunIntegrateStatus.Integrated, "abc1234");

        _ = Run(new RunIntegrateSettings { Task = "T3", Path = "../task", RunWorktree = "../run" });

        RunIntegrateRequest request = Assert.Single(_integrate.Requests);
        Assert.Equal("../task", request.TaskWorktree);
        Assert.Equal("../run", request.RunWorktree);
    }

    [Fact]
    public void ASuccessfulIntegrationEndsWithTheRunHead()
    {
        _integrate.Outcome = new RunIntegrateOutcome(RunIntegrateStatus.Integrated, "abc1234");

        int exit = Run(new RunIntegrateSettings { Task = "T3" });

        Assert.Equal(0, exit);
        Assert.Equal("INTEGRATED: abc1234\n", Output());
    }

    [Fact]
    public void NothingToIntegrateStillPrintsTheIntegratedLine()
    {
        _integrate.Outcome = new RunIntegrateOutcome(RunIntegrateStatus.NothingToIntegrate, "abc1234");

        int exit = Run(new RunIntegrateSettings { Task = "T3" });

        Assert.Equal(0, exit);
        Assert.Equal("INTEGRATED: abc1234 (nothing to integrate)\n", Output());
    }

    [Fact]
    public void AConflictPrintsTheFilesAndExitsThree()
    {
        _integrate.Outcome = new RunIntegrateOutcome(
            RunIntegrateStatus.Conflict,
            Message: "stopped at a conflict",
            Files: ["src/Left.cs", "src/Right.cs"]);

        int exit = Run(new RunIntegrateSettings { Task = "T3" });

        Assert.Equal(3, exit);
        Assert.Equal("CONFLICT: src/Left.cs, src/Right.cs\n", Output());
    }

    [Fact]
    public void ARefusalExitsTwoWithOneErrorLine()
    {
        _integrate.Outcome = new RunIntegrateOutcome(
            RunIntegrateStatus.RefusedInput,
            Message: "the run worktree is the repository's canonical checkout");

        int exit = Run(new RunIntegrateSettings { Task = "T3" });

        Assert.Equal(2, exit);
        Assert.Equal(
            "kyber-weave run integrate: error: the run worktree is the repository's canonical checkout\n",
            Output());
    }

    [Fact]
    public void AnIntegratedTaskWhoseEvidenceCouldNotBeCopiedPrintsTheShaThenTheErrorAndExitsFour()
    {
        _integrate.Outcome = new RunIntegrateOutcome(
            RunIntegrateStatus.IntegratedWithoutEvidence,
            "abc1234",
            Message: "the task's commits are integrated at abc1234, but copying its scratchpad failed: disk full");

        int exit = Run(new RunIntegrateSettings { Task = "T3" });

        Assert.Equal(4, exit);
        Assert.Equal(
            "INTEGRATED: abc1234\n" +
            "kyber-weave run integrate: error: the task's commits are integrated at abc1234, but copying its scratchpad failed: disk full\n",
            Output());
    }

    [Fact]
    public void ALockOrGitFailureExitsOneWithOneErrorLine()
    {
        _integrate.Outcome = new RunIntegrateOutcome(
            RunIntegrateStatus.Failed,
            Message: "the run worktree is locked by '/work/run/.git/kyber-weave-run-integrate.lock'");

        int exit = Run(new RunIntegrateSettings { Task = "T3" });

        Assert.Equal(1, exit);
        Assert.StartsWith("kyber-weave run integrate: error: ", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void NotesArePrintedBeforeTheFinalLine()
    {
        _integrate.Outcome = new RunIntegrateOutcome(
            RunIntegrateStatus.Integrated,
            "abc1234",
            Notes: ["the task's scratchpad was empty."]);

        int exit = Run(new RunIntegrateSettings { Task = "T3" });

        Assert.Equal(0, exit);
        Assert.Equal(
            "  the task's scratchpad was empty.\n" +
            "INTEGRATED: abc1234\n",
            Output());
    }

    private string Output() => _console.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private int Run(RunIntegrateSettings settings)
    {
        RunIntegrateCommand command = new(_integrate, _environment, _console);
        return command.Execute(null!, settings);
    }

    private sealed class StubIntegrate : IRunIntegrateService
    {
        public List<RunIntegrateRequest> Requests { get; } = [];

        public RunIntegrateOutcome Outcome { get; set; } = new(RunIntegrateStatus.Integrated, "abc1234");

        public RunIntegrateOutcome Integrate(RunIntegrateRequest request)
        {
            Requests.Add(request);
            return Outcome;
        }
    }
}
