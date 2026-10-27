using System.Diagnostics;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <see cref="GateRunner"/> records how each gate ended, not only its exit code. A gate whose program
/// is missing is <see cref="GateOutcome.NotStarted"/>, a gate that ran past its limit is
/// <see cref="GateOutcome.TimedOut"/>, and a gate that ran to the end is <see cref="GateOutcome.Completed"/>.
/// The three must stay distinguishable, because a missing tool and a failing check are fixed differently.
/// A gate also starts in its declared working directory, not in the repository root.
/// </summary>
public sealed class GateRunnerOutcomeTests : IDisposable
{
    private const string MissingProgram = "kw-definitely-not-a-real-program";

    private readonly TempDirectory _root = new();

    /// <summary>Deletes the temporary repository.</summary>
    public void Dispose()
    {
        _root.Dispose();
    }

    [Fact]
    public void AGateWhoseProgramIsMissingIsNotStarted()
    {
        PlannedTaskGate gate = new("missing-tool", true, 30, [], [MissingProgram, "--check"]);

        TaskGateRun run = GateRunner.RunTaskGates([gate], _root.Path, fix: false);

        GateResult result = Assert.Single(run.Report.Gates);
        Assert.Equal(GateOutcome.NotStarted, result.Outcome);
        Assert.False(result.Passed);
        Assert.Contains(MissingProgram, result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AGateThatRunsPastItsLimitIsTimedOut()
    {
        string[] sleep = OperatingSystem.IsWindows()
            ? ["ping", "-n", "30", "127.0.0.1"]
            : ["sleep", "30"];
        PlannedTaskGate gate = new("slow-gate", true, 1, [], sleep);

        TaskGateRun run = GateRunner.RunTaskGates([gate], _root.Path, fix: false);

        GateResult result = Assert.Single(run.Report.Gates);
        Assert.Equal(GateOutcome.TimedOut, result.Outcome);
        Assert.False(result.Passed);
        Assert.Contains("timed out", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AGateThatRunsToTheEndIsCompletedWhetherOrNotItPasses()
    {
        PlannedTaskGate passing = new("git-version", true, 30, [], ["git", "--version"]);
        PlannedTaskGate failing = new("git-no-such-command", true, 30, [], ["git", "kw-no-such-subcommand"]);

        TaskGateRun run = GateRunner.RunTaskGates([passing, failing], _root.Path, fix: false);

        Assert.Equal(2, run.Report.Gates.Count);
        Assert.All(run.Report.Gates, result => Assert.Equal(GateOutcome.Completed, result.Outcome));
        Assert.True(run.Report.Gates[0].Passed);
        Assert.False(run.Report.Gates[1].Passed);
    }

    /// <summary>
    /// A gate with a <c>working-directory</c> must start inside it. A file tracked only in the subdirectory
    /// is found by <c>git ls-files --error-unmatch</c> there and not from the root, so the exit code alone
    /// proves where the process started.
    /// </summary>
    [Fact]
    public void AGateWithAWorkingDirectoryStartsInThatDirectory()
    {
        string web = Path.Combine(_root.Path, "web");
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "marker.txt"), "marker");
        RunGit(_root.Path, "init", "-q");
        RunGit(_root.Path, "add", "web/marker.txt");

        string[] probe = ["git", "ls-files", "--error-unmatch", "marker.txt"];
        PlannedTaskGate inWeb = new("probe-in-web", true, 30, [], probe, WorkingDirectory: "web");
        PlannedTaskGate atRoot = new("probe-at-root", true, 30, [], probe);

        TaskGateRun run = GateRunner.RunTaskGates([inWeb, atRoot], _root.Path, fix: false);

        Assert.True(run.Report.Gates[0].Passed, run.Report.Gates[0].Summary);
        Assert.False(run.Report.Gates[1].Passed, "The same probe at the repository root must not find web/marker.txt.");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started for the test repository.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
