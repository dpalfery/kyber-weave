using System.Diagnostics;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A council has a wall-clock budget. When it runs out, the gate that is running is stopped and every gate
/// after it never starts; the stopped gates are reported as KW-REVIEW-039 and do not fail the run. A single
/// <c>--gate</c> run is not a council run, so the budget does not cut it. The gates here are POSIX shell
/// commands (<c>sleep</c>, <c>touch</c>), so the facts skip on Windows.
/// </summary>
public sealed class GateRunnerDeadlineTests : IDisposable
{
    private const string GateStoppedForTime = "KW-REVIEW-039";
    private const string GateFailed = "KW-REVIEW-022";

    private readonly TempDirectory _temp = new();
    private readonly string _repo;

    public GateRunnerDeadlineTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(_repo);
    }

    /// <summary>Deletes the temporary repository.</summary>
    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void ACouncilDeadlineStopsTheRunningGateAndNoLaterGateStarts()
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
        string report = Path.Combine(_temp.Path, "gates.json");

        Stopwatch clock = Stopwatch.StartNew();
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(["review", "gates", _repo, "--council", "--format", "json", "--out", report]));
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"The run took {clock.Elapsed}, so the deadline did not stop it.");
        Assert.Equal(0, run.Result);
        Assert.Contains(GateStoppedForTime, run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(GateFailed, run.Output, StringComparison.Ordinal);

        GateReport gates = ReviewJson.ReadGates(File.ReadAllText(report));
        Assert.Equal(GateOutcome.DeadlinePassed, gates.Gates[0].Outcome);
        Assert.Equal(GateOutcome.DeadlinePassed, gates.Gates[1].Outcome);
        Assert.False(File.Exists(Path.Combine(_repo, "later.marker")), "The gate after the deadline was started.");
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
        string report = Path.Combine(_temp.Path, "gates.json");

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(["review", "gates", _repo, "--gate", "pause", "--format", "json", "--out", report]));

        Assert.Equal(0, run.Result);
        GateResult result = Assert.Single(ReviewJson.ReadGates(File.ReadAllText(report)).Gates);
        Assert.Equal(GateOutcome.Completed, result.Outcome);
        Assert.True(result.Passed);
    }

    /// <summary>
    /// A gate stopped at its own time limit is reported as KW-REVIEW-039 with the timed-out outcome, not as a
    /// failed check (KW-REVIEW-022), and it does not make <c>review gates</c> exit 1.
    /// </summary>
    [Fact]
    public void AGateStoppedAtItsOwnTimeLimitIsKw039NotKw022AndDoesNotFailTheRun()
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
        string report = Path.Combine(_temp.Path, "gates.json");

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(["review", "gates", _repo, "--format", "json", "--out", report]));

        Assert.Equal(0, run.Result);
        Assert.Contains(GateStoppedForTime, run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(GateFailed, run.Output, StringComparison.Ordinal);
        Assert.Equal(GateOutcome.TimedOut, Assert.Single(ReviewJson.ReadGates(File.ReadAllText(report)).Gates).Outcome);
    }

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

    private void WriteConfiguration(string yaml)
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        File.WriteAllText(Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"), yaml);
    }
}
