using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Review;

/// <summary>Runs the deterministic gates a host declares and normalizes what they reported.</summary>
/// <remarks>
/// <para>
/// This is the only place a reviewer executes anything. Everything it can run comes from
/// <see cref="ReviewConfig.Gates"/>, already split into argv by the configuration loader, and
/// goes to <see cref="ProcessRunner"/>, which refuses a shell. There is no path by which a
/// value in a diff, a finding, or a lens prompt becomes part of a command line.
/// </para>
/// <para>
/// Gates are repeatable by construction — same commands, same working directory, same
/// normalization — which is what makes their output usable as evidence rather than as
/// another claim needing corroboration.
/// </para>
/// </remarks>
public static class GateRunner
{
    /// <summary>Runs every declared gate and returns the normalized report.</summary>
    /// <param name="config">The host's review configuration.</param>
    /// <param name="workingDirectory">The repository root the gates run against.</param>
    /// <param name="stopOnBlockingFailure">
    /// Whether to skip the remaining gates once a blocking one fails. Off by default: one
    /// run that reports every failure is worth more to the person fixing them than a
    /// sequence of runs each revealing one.
    /// </param>
    public static GateReport Run(
        ReviewConfig config,
        string workingDirectory,
        bool stopOnBlockingFailure = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Gate working directory not found: {workingDirectory}");

        List<GateResult> results = new(config.Gates.Count);
        DateTime startedUtc = DateTime.UtcNow;

        foreach (ReviewGate gate in config.Gates)
        {
            GateResult result = RunOne(gate, workingDirectory);
            results.Add(result);

            if (stopOnBlockingFailure && gate.Blocking && !result.Passed)
                break;
        }

        return new GateReport(
            GateReport.CurrentSchema,
            results,
            CoverageCollector.ReadNewest(workingDirectory, startedUtc),
            CoverageCollector.ReadAll(workingDirectory, startedUtc));
    }

    /// <summary>
    /// Runs one agent's planned task gates in the order they were declared. A fix command, when
    /// requested, runs before its verify command; a fix that does not succeed is reported beside the
    /// run and never decides the gate on its own, because the verify command is what the change must pass.
    /// </summary>
    /// <param name="gates">The gates that will start, already expanded by <see cref="TaskGateSelection.Plan"/>.</param>
    /// <param name="workingDirectory">The repository root the gates run against.</param>
    /// <param name="fix">Whether to run each gate's fix command before its verify command.</param>
    /// <param name="stopOnBlockingFailure">Whether to skip the remaining gates once a blocking one fails.</param>
    public static TaskGateRun RunTaskGates(
        IReadOnlyList<PlannedTaskGate> gates,
        string workingDirectory,
        bool fix,
        bool stopOnBlockingFailure = false)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Gate working directory not found: {workingDirectory}");

        List<GateResult> results = new(gates.Count);
        List<GateResult> fixFailures = [];
        DateTime startedUtc = DateTime.UtcNow;

        foreach (PlannedTaskGate gate in gates)
        {
            string gateDirectory = ResolveWorkingDirectory(workingDirectory, gate.WorkingDirectory);

            if (fix && gate.Fix.Count > 0)
            {
                GateResult applied = RunArgv(gate.Id, false, gate.Fix, gate.TimeoutSeconds, gateDirectory, false);
                if (!applied.Passed)
                    fixFailures.Add(applied);
            }

            GateResult result = RunArgv(
                gate.Id, gate.Blocking, gate.Run, gate.TimeoutSeconds, gateDirectory, gate.FailOnOutput);
            results.Add(result);

            if (stopOnBlockingFailure && gate.Blocking && !result.Passed)
                break;
        }

        GateReport report = new(
            GateReport.CurrentSchema,
            results,
            CoverageCollector.ReadNewest(workingDirectory, startedUtc),
            CoverageCollector.ReadAll(workingDirectory, startedUtc));

        return new TaskGateRun(report, fixFailures);
    }

    private static GateResult RunOne(ReviewGate gate, string workingDirectory)
    {
        string gateDirectory = ResolveWorkingDirectory(workingDirectory, gate.WorkingDirectory);
        return RunArgv(
            gate.Id, gate.Blocking, gate.Run, gate.TimeoutSeconds, gateDirectory, gate.FailOnOutput);
    }

    /// <summary>
    /// The directory a gate starts in: the repository root, or the gate's declared subdirectory of it.
    /// The loader has already refused a declared directory that is absolute, leaves the root, or does not exist.
    /// </summary>
    private static string ResolveWorkingDirectory(string root, string? repositoryRelative)
    {
        if (string.IsNullOrEmpty(repositoryRelative))
            return root;

        return Path.GetFullPath(Path.Combine(root, repositoryRelative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static GateResult RunArgv(
        string id,
        bool blocking,
        IReadOnlyList<string> argv,
        int timeoutSeconds,
        string workingDirectory,
        bool failOnOutput)
    {
        if (!Directory.Exists(workingDirectory))
        {
            // Reported as not started, not as a failed check: the command was never given a place to run.
            return new GateResult(
                id,
                blocking,
                -1,
                $"working directory '{workingDirectory}' does not exist",
                0,
                GateOutcome.NotStarted);
        }

        ProcessStartInfo startInfo = new(argv[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (string argument in argv.Skip(1))
            startInfo.ArgumentList.Add(argument);

        long start = Stopwatch.GetTimestamp();
        TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);
        try
        {
            ProcessResult process = ProcessRunner.Run(startInfo, string.Empty, timeout);

            // A tool that reports by listing (gofmt -l exits 0 on Go before 1.26 even when files differ) can
            // only fail through its output. The exit code is rewritten to 1 so GateResult.Passed stays the single
            // judge, and the summary says why, since the process itself exited zero.
            bool printed = failOnOutput && process.ExitCode == 0 && !string.IsNullOrWhiteSpace(process.StandardOutput);
            return new GateResult(
                id,
                blocking,
                printed ? 1 : process.ExitCode,
                printed ? SummarizeOutput(process.StandardOutput) : Summarize(process),
                (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                GateOutcome.Completed);
        }
        catch (TimeoutException ex)
        {
            return new GateResult(
                id,
                blocking,
                -1,
                $"timed out after {timeoutSeconds} seconds: {ex.Message}",
                (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                GateOutcome.TimedOut);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // A gate that cannot start has not passed. Reporting it as a failure with the
            // reason keeps the distinction visible: "the tests failed" and "the test command
            // does not exist here" both block, but they are fixed very differently.
            return new GateResult(
                id,
                blocking,
                -1,
                $"could not start '{argv[0]}': {ex.Message}",
                (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                GateOutcome.NotStarted);
        }
    }

    private static string SummarizeOutput(string output)
    {
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return "exited 0 but printed output (fail-on-output): " + string.Join(" | ", lines.Take(3));
    }

    private static string Summarize(ProcessResult process)
    {
        if (process.ExitCode == 0)
            return "passed";

        // The tail, not the head: build and test runners put the summary at the end, and a
        // truncated head is almost always the banner nobody needs.
        string output = string.IsNullOrWhiteSpace(process.StandardError)
            ? process.StandardOutput
            : process.StandardError;

        string[] lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines.Length == 0
            ? $"exit code {process.ExitCode}"
            : string.Join(" | ", lines.TakeLast(3));
    }
}
