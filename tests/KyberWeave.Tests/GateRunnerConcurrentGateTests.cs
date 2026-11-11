using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A council gate declared <c>concurrent</c> starts at its place in the declared order and does not hold up
/// the gates declared after it. Its result still sits at its declared place in the report, it is still stopped
/// at its own time limit, and a run that stops on a blocking failure does not leave it running.
/// </summary>
/// <remarks>
/// The overlap is proved without a clock: the concurrent gate waits for a file that only the gate declared
/// after it writes, so it can pass only if the two ran at the same time.
/// </remarks>
public sealed class GateRunnerConcurrentGateTests : IDisposable
{
    private const string ReleaseFile = "released";

    private readonly TempDirectory _root = new();

    /// <summary>Deletes the temporary repository.</summary>
    public void Dispose()
    {
        _root.Dispose();
    }

    [Fact]
    public void AConcurrentGateRunsAlongsideTheGatesDeclaredAfterIt()
    {
        ReviewGate waiter = new("waits-for-release", WaitForReleaseFile(), TimeoutSeconds: 30) { Concurrent = true };
        ReviewGate release = new("release", WriteReleaseFile(), TimeoutSeconds: 30);

        GateReport report = GateRunner.Run(new ReviewConfig { Gates = [waiter, release] }, _root.Path);

        Assert.Equal(["waits-for-release", "release"], report.Gates.Select(gate => gate.Id));
        Assert.All(report.Gates, gate => Assert.True(gate.Passed, $"{gate.Id}: {gate.Summary}"));
    }

    /// <summary>The control for the test above: without the key the same pair cannot overlap.</summary>
    [Fact]
    public void WithoutTheKeyAGateHoldsUpTheGatesDeclaredAfterIt()
    {
        ReviewGate waiter = new("waits-for-release", WaitForReleaseFile(), TimeoutSeconds: 2);
        ReviewGate release = new("release", WriteReleaseFile(), TimeoutSeconds: 30);

        GateReport report = GateRunner.Run(new ReviewConfig { Gates = [waiter, release] }, _root.Path);

        Assert.Equal(GateOutcome.TimedOut, report.Gates[0].Outcome);
        Assert.True(report.Gates[1].Passed, report.Gates[1].Summary);
    }

    [Fact]
    public void AConcurrentGateThatRunsPastItsLimitIsTimedOutAndNeverPassed()
    {
        ReviewGate slow = new("slow", Sleep30(), TimeoutSeconds: 1) { Concurrent = true };
        ReviewGate quick = new("quick", ["git", "--version"], TimeoutSeconds: 30);

        GateReport report = GateRunner.Run(new ReviewConfig { Gates = [slow, quick] }, _root.Path);

        Assert.Equal(["slow", "quick"], report.Gates.Select(gate => gate.Id));
        Assert.Equal(GateOutcome.TimedOut, report.Gates[0].Outcome);
        Assert.False(report.Gates[0].Passed);
        Assert.Contains("timed out", report.Gates[0].Summary, StringComparison.Ordinal);
        Assert.True(report.Gates[1].Passed, report.Gates[1].Summary);
    }

    /// <summary>
    /// A run asked to stop at the first blocking failure skips what has not finished. A concurrent gate still
    /// running is stopped and left out of the report like the gates that never started, and its process is gone.
    /// </summary>
    [Fact]
    public void StoppingOnABlockingFailureStopsAConcurrentGateThatIsStillRunning()
    {
        ReviewGate slow = new("slow", RecordPidThenSleep30(), TimeoutSeconds: 120) { Concurrent = true };
        ReviewGate started = new("slow-has-started", WaitForPidFile(), TimeoutSeconds: 30);
        ReviewGate failing = new("failing", ["git", "kw-no-such-subcommand"], TimeoutSeconds: 30);
        ReviewGate never = new("never-started", ["git", "--version"], TimeoutSeconds: 30);

        long start = Stopwatch.GetTimestamp();
        GateReport report = GateRunner.Run(
            new ReviewConfig { Gates = [slow, started, failing, never] }, _root.Path, stopOnBlockingFailure: true);

        Assert.Equal(["slow-has-started", "failing"], report.Gates.Select(gate => gate.Id));
        Assert.True(
            Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(25),
            "The run must not wait for the stopped gate's own 30 second sleep.");
        Assert.False(IsRunning(ReadRecordedPid()), "The stopped gate's process must not outlive the run.");
    }

    /// <summary>A concurrent blocking gate that fails stops the run too, once its result is in.</summary>
    [Fact]
    public void AConcurrentGateIsAlwaysJoinedBeforeTheReportIsReturned()
    {
        ReviewGate failing = new("fails-late", FailAfterRelease(), TimeoutSeconds: 30) { Concurrent = true };
        ReviewGate release = new("release", WriteReleaseFile(), TimeoutSeconds: 30);

        GateReport report = GateRunner.Run(new ReviewConfig { Gates = [failing, release] }, _root.Path);

        Assert.Equal(["fails-late", "release"], report.Gates.Select(gate => gate.Id));
        Assert.Equal(GateOutcome.Completed, report.Gates[0].Outcome);
        Assert.False(report.Gates[0].Passed);
    }

    private static string[] WaitForReleaseFile() => WaitForFile(ReleaseFile, exitCode: 0);

    private static string[] FailAfterRelease() => WaitForFile(ReleaseFile, exitCode: 3);

    private static string[] WaitForPidFile() => WaitForFile("pid", exitCode: 0);

    private static string[] WaitForFile(string name, int exitCode) =>
        OperatingSystem.IsWindows()
            ?
            [
                "powershell", "-NoProfile", "-Command",
                $"while (-not (Test-Path {name})) {{ Start-Sleep -Milliseconds 100 }}; exit {exitCode.ToString(CultureInfo.InvariantCulture)}"
            ]
            :
            [
                "sh", "-c",
                $"while [ ! -s {name} ]; do sleep 0.1; done; exit {exitCode.ToString(CultureInfo.InvariantCulture)}"
            ];

    private static string[] WriteReleaseFile() =>
        OperatingSystem.IsWindows()
            ? ["cmd", "/c", $"echo released> {ReleaseFile}"]
            : ["sh", "-c", $"echo released > {ReleaseFile}"];

    private static string[] Sleep30() =>
        OperatingSystem.IsWindows() ? ["ping", "-n", "30", "127.0.0.1"] : ["sleep", "30"];

    private static string[] RecordPidThenSleep30() =>
        OperatingSystem.IsWindows()
            ? ["powershell", "-NoProfile", "-Command", "Set-Content -Path pid -Value $PID; Start-Sleep -Seconds 30"]
            : ["sh", "-c", "echo $$ > pid; sleep 30"];

    private int ReadRecordedPid() =>
        int.Parse(File.ReadAllText(Path.Combine(_root.Path, "pid")).Trim(), CultureInfo.InvariantCulture);

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
