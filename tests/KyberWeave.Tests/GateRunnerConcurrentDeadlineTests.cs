using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A gate declared <c>concurrent</c> runs alongside the gates after it, so the council's wall-clock budget has to
/// reach it too: a concurrent gate the deadline cuts is reported as not run, the run does not stop on it, and a
/// gate whose budget has already passed never starts. These facts are POSIX-only, because they use <c>sleep</c>.
/// </summary>
/// <remarks>
/// A concurrent gate writes its own process id to a file before it sleeps, and the facts check that the process is
/// gone when the run returns. The sleep is the process itself (<c>exec</c>), so the recorded id is the one to check.
/// </remarks>
public sealed class GateRunnerConcurrentDeadlineTests : IDisposable
{
    private const string PidFile = "concurrent.pid";
    private const string StartedFile = "started.marker";

    private readonly TempDirectory _root = new();

    /// <summary>Deletes the temporary repository.</summary>
    public void Dispose()
    {
        _root.Dispose();
    }

    /// <summary>
    /// The deadline reaches a concurrent gate: it is cut at the deadline and reported as not run, the run returns long
    /// before its 30 second sleep ends, and the sleeping process is gone by then. The quick gate after it still
    /// completes, because the run is not stopped by the cut.
    /// </summary>
    [Fact]
    public void ADeadlineCutsAConcurrentGateAndTheRunReturnsWithItsProcessGone()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gates use POSIX sleep and sh.");

        ReviewGate slow = new("slow", RecordPidThenSleep30(), TimeoutSeconds: 60) { Concurrent = true };
        ReviewGate quick = new("quick", ["git", "--version"], TimeoutSeconds: 30);

        long start = Stopwatch.GetTimestamp();
        GateReport report = GateRunner.Run(
            new ReviewConfig { Gates = [slow, quick] }, _root.Path, deadline: TimeSpan.FromSeconds(2));

        Assert.True(
            Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(25),
            "The run must not wait for the concurrent gate's own 30 second sleep.");
        Assert.Equal(["slow", "quick"], report.Gates.Select(gate => gate.Id));
        Assert.Equal(GateOutcome.DeadlinePassed, report.Gates[0].Outcome);
        Assert.False(report.Gates[0].Passed);
        Assert.True(report.Gates[1].Passed, report.Gates[1].Summary);
        Assert.False(IsRunning(ReadRecordedPid()), "The cut gate's process must not outlive the run.");
    }

    /// <summary>
    /// With stop-on-blocking-failure on, a concurrent blocking gate that the deadline cuts is not a failure that stops
    /// the run. A sequential gate the same cut touches is also not a stop, so the gate declared after it has a result.
    /// </summary>
    [Fact]
    public void ADeadlineCutOfAConcurrentBlockingGateDoesNotStopTheRun()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gates use POSIX sleep and sh.");

        ReviewGate slow = new("slow", RecordPidThenSleep30(), TimeoutSeconds: 60) { Concurrent = true };
        ReviewGate pivot = new("pivot", Sleep3(), TimeoutSeconds: 30);
        ReviewGate after = new("after", ["git", "--version"], TimeoutSeconds: 30);

        long start = Stopwatch.GetTimestamp();
        GateReport report = GateRunner.Run(
            new ReviewConfig { Gates = [slow, pivot, after] },
            _root.Path,
            stopOnBlockingFailure: true,
            deadline: TimeSpan.FromSeconds(2));

        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(25));
        Assert.Equal(["slow", "pivot", "after"], report.Gates.Select(gate => gate.Id));
        Assert.Equal(GateOutcome.DeadlinePassed, report.Gates[0].Outcome);
        Assert.Equal(GateOutcome.DeadlinePassed, report.Gates[1].Outcome);
        Assert.Equal(GateOutcome.DeadlinePassed, report.Gates[2].Outcome);
        Assert.False(IsRunning(ReadRecordedPid()), "The cut gate's process must not outlive the run.");
    }

    /// <summary>
    /// A concurrent gate whose budget has already passed when it would start is reported as not started, and no process
    /// is started for it: its marker file must not exist.
    /// </summary>
    [Fact]
    public void AConcurrentGateStartedAfterTheDeadlineNeverStartsItsProcess()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gate uses sh.");

        ReviewGate late = new("late", MarkStartedThenSleep30(), TimeoutSeconds: 60) { Concurrent = true };

        GateReport report = GateRunner.Run(
            new ReviewConfig { Gates = [late] }, _root.Path, deadline: TimeSpan.Zero);

        GateResult result = Assert.Single(report.Gates);
        Assert.Equal(GateOutcome.DeadlinePassed, result.Outcome);
        Assert.Contains("had passed", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root.Path, StartedFile)), "The gate's process was started after the deadline.");
    }

    /// <summary>
    /// Without a deadline, a concurrent blocking gate that times out on its own <c>timeout-seconds</c> still stops a run
    /// that stops on blocking failure: the gate declared after it is not run. Main's concurrent tests pin the timed-out
    /// outcome and the stop on a failed or still-running concurrent gate, but not this one.
    /// </summary>
    [Fact]
    public void AConcurrentGateTimedOutOnItsOwnLimitStopsTheRunWithoutADeadline()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The gates use POSIX sleep.");

        ReviewGate slow = new("slow", Sleep30(), TimeoutSeconds: 1) { Concurrent = true };
        ReviewGate pivot = new("pivot", Sleep3(), TimeoutSeconds: 30);
        ReviewGate never = new("never-started", ["git", "--version"], TimeoutSeconds: 30);

        GateReport report = GateRunner.Run(
            new ReviewConfig { Gates = [slow, pivot, never] }, _root.Path, stopOnBlockingFailure: true);

        Assert.Equal(["slow", "pivot"], report.Gates.Select(gate => gate.Id));
        Assert.Equal(GateOutcome.TimedOut, report.Gates[0].Outcome);
        Assert.False(report.Gates[0].Passed);
    }

    private static string[] Sleep30() => ["sleep", "30"];

    private static string[] Sleep3() => ["sleep", "3"];

    /// <summary>Records this shell's child's process id, then becomes the sleep, so the id is the sleep's own.</summary>
    private static string[] RecordPidThenSleep30() =>
        ["sh", "-c", $"echo $$ > {PidFile}; exec sleep 30"];

    /// <summary>Writes a marker that proves the process started, then becomes the sleep.</summary>
    private static string[] MarkStartedThenSleep30() =>
        ["sh", "-c", $"echo started > {StartedFile}; exec sleep 30"];

    private int ReadRecordedPid() =>
        int.Parse(File.ReadAllText(Path.Combine(_root.Path, PidFile)).Trim(), CultureInfo.InvariantCulture);

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
