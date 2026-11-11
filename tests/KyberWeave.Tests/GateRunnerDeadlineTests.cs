using System.Diagnostics;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Tests;

/// <summary>
/// The council's wall-clock budget is opt-in. Only <c>review gates --council --deadline</c> applies the configured
/// <c>review.council-deadline-seconds</c>: the gate that is running is stopped, every gate after it never starts,
/// and the stopped gates are reported as KW-REVIEW-039 and leave the exit code alone. Without <c>--deadline</c> no
/// run deadline applies, and a blocking gate stopped by its own <c>timeout-seconds</c> fails the run as it always has
/// (KW-REVIEW-022, exit 1). A single <c>--gate</c> run is never cut by the budget, and <c>--deadline</c> is refused
/// with <c>--gate</c> and with <c>--agent</c>. The gates here are POSIX shell commands (<c>sleep</c>,
/// <c>touch</c>), so the facts skip on Windows.
/// </summary>
public sealed class GateRunnerDeadlineTests : IDisposable
{
    private const string GateStoppedForTime = "KW-REVIEW-039";
    private const string GateFailed = "KW-REVIEW-022";
    private const int InputRefused = 2;

    private readonly TempDirectory _temp = new();
    private readonly string _repo;
    private readonly string _report;

    public GateRunnerDeadlineTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(_repo);
        _report = Path.Combine(_temp.Path, "gates.json");
    }

    /// <summary>Deletes the temporary repository.</summary>
    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void ACouncilDeadlineWithDeadlineFlagStopsTheRunningGateAndNoLaterGateStarts()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gates use POSIX sleep and touch.");

        WriteConfiguration("""
            review:
              council-deadline-seconds: 2
              gates:
                - id: slow
                  run: [sleep, "30"]
                  timeout-seconds: 60
                - id: later
                  run: [touch, later.marker]
            """);

        Stopwatch clock = Stopwatch.StartNew();
        CapturedConsoleExecution<int> run = Gates("--council", "--deadline");
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"The run took {clock.Elapsed}, so the deadline did not stop it.");
        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, GateStoppedForTime, "Warning");
        Assert.DoesNotContain(GateFailed, run.Output, StringComparison.Ordinal);

        GateReport gates = ReviewJson.ReadGates(File.ReadAllText(_report));
        Assert.Equal(GateOutcome.DeadlinePassed, gates.Gates[0].Outcome);
        Assert.Equal(GateOutcome.DeadlinePassed, gates.Gates[1].Outcome);
        Assert.False(File.Exists(Path.Combine(_repo, "later.marker")), "The gate after the deadline was started.");
    }

    /// <summary>
    /// Without <c>--deadline</c> a council run has no run deadline, even when <c>review.council-deadline-seconds</c> is
    /// set and <c>--council</c> is passed. A gate that takes three seconds completes against a two-second budget.
    /// </summary>
    [Fact]
    public void WithoutTheDeadlineFlagACouncilRunIsNotCutByTheConfiguredBudget()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gate uses POSIX sleep.");

        WriteConfiguration("""
            review:
              council-deadline-seconds: 2
              gates:
                - id: pause
                  run: [sleep, "3"]
                  timeout-seconds: 60
            """);

        CapturedConsoleExecution<int> run = Gates("--council");

        Assert.Equal(0, run.Result);
        GateResult result = Assert.Single(ReviewJson.ReadGates(File.ReadAllText(_report)).Gates);
        Assert.Equal(GateOutcome.Completed, result.Outcome);
        Assert.True(result.Passed);
    }

    /// <summary>
    /// A gate named with <c>--gate</c> runs alone, outside the council budget. A gate that takes three seconds
    /// completes even though the council deadline is one second.
    /// </summary>
    [Fact]
    public void ASingleNamedGateIsNotCutByTheCouncilDeadline()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gate uses POSIX sleep.");

        WriteConfiguration("""
            review:
              council-deadline-seconds: 1
              gates:
                - id: pause
                  run: [sleep, "3"]
                  timeout-seconds: 60
            """);

        CapturedConsoleExecution<int> run = Gates("--gate", "pause");

        Assert.Equal(0, run.Result);
        GateResult result = Assert.Single(ReviewJson.ReadGates(File.ReadAllText(_report)).Gates);
        Assert.Equal(GateOutcome.Completed, result.Outcome);
        Assert.True(result.Passed);
    }

    /// <summary>
    /// Without <c>--deadline</c>, a blocking gate stopped at its own time limit is a failure: KW-REVIEW-022 at error,
    /// exit 1, and the outcome says it timed out. It is not the NOT RUN warning it becomes under <c>--deadline</c>.
    /// </summary>
    [Fact]
    public void AGateStoppedAtItsOwnTimeLimitWithoutDeadlineFailsTheRun()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gate uses POSIX sleep.");

        WriteConfiguration("""
            review:
              gates:
                - id: slow
                  run: [sleep, "30"]
                  timeout-seconds: 1
            """);

        CapturedConsoleExecution<int> run = Gates();

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, GateFailed, "Error");
        Assert.DoesNotContain(GateStoppedForTime, run.Output, StringComparison.Ordinal);
        Assert.Equal(GateOutcome.TimedOut, Assert.Single(ReviewJson.ReadGates(File.ReadAllText(_report)).Gates).Outcome);
    }

    /// <summary>
    /// With <c>--deadline</c>, a blocking gate stopped at its own time limit is NOT RUN: KW-REVIEW-039 as a warning,
    /// and it is left out of the exit code, so the run exits 0.
    /// </summary>
    [Fact]
    public void AGateStoppedAtItsOwnTimeLimitWithDeadlineIsNotRunAndExitsZero()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gate uses POSIX sleep.");

        WriteConfiguration("""
            review:
              gates:
                - id: slow
                  run: [sleep, "30"]
                  timeout-seconds: 1
            """);

        CapturedConsoleExecution<int> run = Gates("--council", "--deadline");

        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, GateStoppedForTime, "Warning");
        Assert.DoesNotContain(GateFailed, run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deadline cut is reported with the deadline's value and the key that set it, so a reader knows which
    /// budget ran out and where to change it.
    /// </summary>
    [Fact]
    public void TheDeadlineCutMessageNamesTheDeadlineValueAndItsConfigKey()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gate uses POSIX sleep.");

        WriteConfiguration("""
            review:
              council-deadline-seconds: 2
              gates:
                - id: slow
                  run: [sleep, "30"]
                  timeout-seconds: 60
            """);

        CapturedConsoleExecution<int> run = Gates("--council", "--deadline");

        AssertDiagnostic(run.Output, GateStoppedForTime, "Warning", "review.council-deadline-seconds");
        Assert.Contains("2 seconds", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deadline cut is re-run by naming the gate alone. It does not tell the reader to raise the gate's own
    /// <c>timeout-seconds</c>, because the gate was not too slow; the council ran out of budget.
    /// </summary>
    [Fact]
    public void TheDeadlineCutHintReRunsTheGateAloneAndDoesNotAskToRaiseItsTimeout()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gate uses POSIX sleep.");

        WriteConfiguration("""
            review:
              council-deadline-seconds: 2
              gates:
                - id: slow
                  run: [sleep, "30"]
                  timeout-seconds: 60
            """);

        CapturedConsoleExecution<int> run = Gates("--council", "--deadline");

        Assert.Contains("--gate slow", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("timeout-seconds", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--deadline</c> is a council budget. A single named gate is a deliberate re-check outside it, so the two
    /// options together are an input error, refused with exit 2 before any gate starts.
    /// </summary>
    [Fact]
    public void TheDeadlineFlagIsRefusedWithGate()
    {
        WriteConfiguration("review:\n  gates:\n    - id: slow\n      run: [git, --version]\n");

        CapturedConsoleExecution<int> run = Gates("--gate", "slow", "--deadline");

        Assert.Equal(InputRefused, run.Result);
        Assert.Contains("--deadline", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--deadline</c> is a council budget. A task-gate run for one agent is not the council, so the two options
    /// together are refused with exit 2 before the agent is looked up.
    /// </summary>
    [Fact]
    public void TheDeadlineFlagIsRefusedWithAgent()
    {
        WriteConfiguration("review:\n  gates:\n    - id: slow\n      run: [git, --version]\n");

        CapturedConsoleExecution<int> run = Gates("--agent", "rust-dev", "--deadline");

        Assert.Equal(InputRefused, run.Result);
        Assert.Contains("--deadline", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The configured budget is read from <c>review.council-deadline-seconds</c>. Only a host that writes the key
    /// gets a value other than the default.
    /// </summary>
    [Fact]
    public void TheCouncilDeadlineIsReadFromTheHostConfiguration()
    {
        WriteConfiguration("ontology:\n  docs-root: docs\nreview:\n  council-deadline-seconds: 42\n");

        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(_repo);

        Assert.True(loaded.Success);
        Assert.NotNull(loaded.Config);
        Assert.Equal(42, loaded.Config.Review.CouncilDeadlineSeconds);
    }

    [Fact]
    public void AHostWithNoDeadlineKeyGetsSixHundredSeconds()
    {
        WriteConfiguration("ontology:\n  docs-root: docs\n");

        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(_repo);

        Assert.True(loaded.Success);
        Assert.NotNull(loaded.Config);
        Assert.Equal(600, loaded.Config.Review.CouncilDeadlineSeconds);
    }

    /// <summary>
    /// Stopping on failure treats a timed-out blocking gate as a failure, so the run stops there. With no deadline in
    /// play, a gate that timed out is no different from one that failed.
    /// </summary>
    [Fact]
    public void ATimedOutBlockingTaskGateStopsTheRunWhenStoppingOnFailure()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gate uses POSIX sleep.");

        PlannedTaskGate slow = new("slow-gate", true, 1, [], ["sleep", "30"]);
        PlannedTaskGate later = new("later-gate", true, 30, [], ["git", "--version"]);

        TaskGateRun run = GateRunner.RunTaskGates([slow, later], _repo, fix: false, stopOnBlockingFailure: true);

        GateResult result = Assert.Single(run.Report.Gates);
        Assert.Equal(GateOutcome.TimedOut, result.Outcome);
    }

    private void WriteConfiguration(string yaml)
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        File.WriteAllText(Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"), yaml);
    }

    /// <summary>Runs <c>review gates</c> on the repository with JSON output and the report written to a known path.</summary>
    private CapturedConsoleExecution<int> Gates(params string[] options)
    {
        List<string> arguments = ["review", "gates", _repo, .. options, "--format", "json", "--out", _report];
        return ProcessConsoleCapture.Run(() => CommandAppFixture.ReviewGates().Run([.. arguments]));
    }
}
