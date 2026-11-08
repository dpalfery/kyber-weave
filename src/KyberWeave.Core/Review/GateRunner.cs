using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
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
    private static readonly ArbiterRule GateSelectRule =
        ArbiterConfig.ProductDefaults.Rules.Single(rule => rule.Id == "KW-ARB-GATE-001");

    /// <summary>Runs every declared gate and returns the normalized report.</summary>
    /// <param name="config">The host's review configuration.</param>
    /// <param name="workingDirectory">The repository root the gates run against.</param>
    /// <param name="stopOnBlockingFailure">
    /// Whether to skip the remaining gates once a blocking one fails. Off by default: one
    /// run that reports every failure is worth more to the person fixing them than a
    /// sequence of runs each revealing one.
    /// </param>
    /// <param name="changedPaths">
    /// Repository-relative paths the change touches, or null when no base was given. Null
    /// means <c>applies-when</c> is not evaluated and every gate runs.
    /// </param>
    /// <param name="baseRef">The commit the change was measured against, recorded on the report.</param>
    public static GateReport Run(
        ReviewConfig config,
        string workingDirectory,
        bool stopOnBlockingFailure = false,
        IReadOnlyCollection<string>? changedPaths = null,
        string? baseRef = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Gate working directory not found: {workingDirectory}");

        List<GateResult> results = new(config.Gates.Count);
        DateTime startedUtc = DateTime.UtcNow;

        foreach (ReviewGate gate in config.Gates)
        {
            GateResult result = AppliesWhenReason(gate, changedPaths) is string reason
                ? NotApplicable(gate, reason)
                : MissingRequirement(gate, workingDirectory) ?? RunOne(gate, workingDirectory);
            results.Add(result);

            if (stopOnBlockingFailure
                && gate.Blocking
                && !result.Passed
                && result.NotApplicableReason is null)
                break;
        }

        return new GateReport(
            GateReport.CurrentSchema,
            results,
            CoverageCollector.ReadNewest(workingDirectory, startedUtc),
            CoverageCollector.ReadAll(workingDirectory, startedUtc),
            baseRef);
    }

    /// <summary>
    /// Why <paramref name="gate"/> does not apply, or null when it runs. A gate without
    /// <c>applies-when</c> always runs, as does every gate when <paramref name="changedPaths"/>
    /// is null; the rest are judged by <c>KW-ARB-GATE-001</c> through the rule engine.
    /// </summary>
    private static string? AppliesWhenReason(ReviewGate gate, IReadOnlyCollection<string>? changedPaths)
    {
        if (changedPaths is null)
            return null;
        if (gate.AppliesWhen is null || gate.AppliesWhen.Paths.Count == 0)
            return null;

        ArbiterFactSet facts = new ArbiterFactSet()
            .With("gate.id", gate.Id, ArbiterFactLabel.Derived)
            .With("gate.applies-when.paths", gate.AppliesWhen.Paths.ToList(), ArbiterFactLabel.Derived)
            .With("review.changed-paths", changedPaths.ToList(), ArbiterFactLabel.Derived);

        ArbiterOutcome outcome = RuleEngine.EvaluateStep0([GateSelectRule], facts, "gate.select");
        if (!string.Equals(outcome.CombinedEffect, RuleEffects.NotApplicable, StringComparison.Ordinal))
            return null;

        return $"no changed path matches applies-when paths [{string.Join(", ", gate.AppliesWhen.Paths)}]";
    }

    /// <summary>
    /// Reports a gate that did not apply without executing it. Exit code zero keeps a
    /// reader that predates the not-applicable state from mistaking an unexecuted gate
    /// for a failure; <see cref="GateResult.Passed"/> is still false, so nothing counts
    /// it as a pass either.
    /// </summary>
    private static GateResult NotApplicable(ReviewGate gate, string reason) =>
        new(gate.Id, gate.Blocking, 0, reason, 0, reason);

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

    /// <summary>
    /// The result for a gate whose declared prerequisite path is absent, or null when the gate may start.
    /// </summary>
    /// <remarks>
    /// A missing dependency is the same class of fact as a program that could not start: the check never ran,
    /// so it is reported as not started rather than as a code failure, and it is never a pass. The summary names
    /// the path and how to provide it, because the install is the action that unblocks the run.
    /// </remarks>
    private static GateResult? MissingRequirement(ReviewGate gate, string root)
    {
        foreach (string path in gate.RequiresPaths)
        {
            string full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (Path.Exists(full))
                continue;

            return new GateResult(
                gate.Id,
                gate.Blocking,
                -1,
                MissingRequirementSummary(path),
                0,
                Outcome: GateOutcome.NotStarted);
        }

        return null;
    }

    private static string MissingRequirementSummary(string path) =>
        $"required path '{path}' is missing, so the gate did not start. " +
        (path.StartsWith("dash/", StringComparison.Ordinal)
            ? "Install the dashboard dependencies with 'npm install --prefix dash', then run the gate again."
            : "Install whatever provides it, then run the gate again.");

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
                Outcome: GateOutcome.NotStarted);
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
                Outcome: GateOutcome.Completed);
        }
        catch (TimeoutException ex)
        {
            return new GateResult(
                id,
                blocking,
                -1,
                $"timed out after {timeoutSeconds} seconds: {ex.Message}",
                (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                Outcome: GateOutcome.TimedOut);
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
                Outcome: GateOutcome.NotStarted);
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
