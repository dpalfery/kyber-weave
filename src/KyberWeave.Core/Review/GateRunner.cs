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
    private const string DeadlineKey = "review.council-deadline-seconds";

    private static readonly ArbiterRule GateSelectRule =
        ArbiterConfig.ProductDefaults.Rules.Single(rule => rule.Id == "KW-ARB-GATE-001");

    /// <summary>Runs every declared gate and returns the normalized report.</summary>
    /// <remarks>
    /// Gates start in declaration order and each waits for the one before it, except a gate declared
    /// <c>concurrent</c>: it starts at its place and runs alongside the gates declared after it. The report
    /// lists results in declaration order either way. At most <see cref="ReviewConfig.Parallelism"/> gates run at one
    /// time, concurrent or not.
    /// <para>
    /// The deadline is opt-in rather than read from <paramref name="config"/>, because a single named gate, a task
    /// gate, and a run outside the council are not spending the council's budget and must never be cut by it.
    /// </para>
    /// </remarks>
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
    /// <param name="deadline">
    /// The wall-clock budget for the whole run, or null for none. Each gate runs for the shorter of its own
    /// timeout and the time left; every gate still running at the deadline, and every gate not yet started, is reported as
    /// <see cref="GateOutcome.DeadlinePassed"/>. Only <c>review gates --deadline</c> passes one.
    /// </param>
    public static GateReport Run(
        ReviewConfig config,
        string workingDirectory,
        bool stopOnBlockingFailure = false,
        IReadOnlyCollection<string>? changedPaths = null,
        string? baseRef = null,
        TimeSpan? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Gate working directory not found: {workingDirectory}");

        IReadOnlyList<ReviewGate> gates = config.Gates;
        GateResult?[] results = new GateResult?[gates.Count];
        List<(int Index, Task<GateResult?> Run)> alongside = [];
        DateTime startedUtc = DateTime.UtcNow;
        long started = Stopwatch.GetTimestamp();
        bool stopped = false;

        using CancellationTokenSource stop = new();
        using SemaphoreSlim slots = new(Math.Max(1, config.Parallelism));
        try
        {
            for (int index = 0; index < gates.Count && !stopped; index++)
            {
                ReviewGate gate = gates[index];

                GateResult? settled;
                if (AppliesWhenReason(gate, changedPaths) is string reason)
                    settled = NotApplicable(gate, reason);
                else if (deadline is { } passed && deadline - Stopwatch.GetElapsedTime(started) <= TimeSpan.Zero)
                    settled = DeadlinePassedBeforeStart(gate, passed);
                else
                    settled = MissingRequirement(gate, workingDirectory);

                if (settled is null && gate.Concurrent)
                {
                    alongside.Add((index, Task.Run(() => RunInSlot(gate, workingDirectory, deadline, started, slots, stop.Token))));
                    continue;
                }

                results[index] = settled ?? RunInSlot(gate, workingDirectory, deadline, started, slots, CancellationToken.None);

                stopped = stopOnBlockingFailure
                          && (StopsTheRun(gate, results[index], deadline)
                              || alongside.Exists(run => run.Run.IsCompletedSuccessfully
                                                         && StopsTheRun(gates[run.Index], run.Run.Result, deadline)));
            }
        }
        finally
        {
            // A stopped run skips what has not finished, and no run returns with a gate's process still alive.
            if (stopped)
                stop.Cancel();

            // Every concurrent gate is waited for before any result is read: reading rethrows a gate's
            // unexpected fault, and that must not leave another gate's process running behind the run.
            foreach ((_, Task<GateResult?> run) in alongside)
                ((IAsyncResult)run).AsyncWaitHandle.WaitOne();

            foreach ((int index, Task<GateResult?> run) in alongside)
                results[index] = run.GetAwaiter().GetResult();
        }

        return new GateReport(
            GateReport.CurrentSchema,
            results.OfType<GateResult>().ToList(),
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
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new GateResult(
                    gate.Id,
                    gate.Blocking,
                    -1,
                    $"required path '{path}' could not be resolved ({ex.Message}), so the gate did not start.",
                    0,
                    Outcome: GateOutcome.NotStarted);
            }

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

    /// <summary>
    /// Reports a gate the council deadline passed before it could start. It never ran, so it is neither a pass nor
    /// a failure, and nothing is executed for it.
    /// </summary>
    private static GateResult DeadlinePassedBeforeStart(ReviewGate gate, TimeSpan deadline) =>
        new(
            gate.Id,
            gate.Blocking,
            -1,
            $"not started: the council deadline of {deadline.TotalSeconds:0} seconds ({DeadlineKey}) had passed",
            0,
            Outcome: GateOutcome.DeadlinePassed);

    /// <summary>
    /// Whether <paramref name="result"/> stops a stop-on-blocking-failure run. Under a deadline, a gate that time
    /// stopped reached no result, so it is not a failure and does not stop the run.
    /// </summary>
    private static bool StopsTheRun(ReviewGate gate, GateResult? result, TimeSpan? deadline) =>
        result is not null && gate.Blocking && !result.Passed && result.NotApplicableReason is null
        && !(deadline is not null && VerdictEngine.IsStoppedForTime(result));

    /// <summary>
    /// Runs a gate once one of the run's slots is free, or returns null when the run was stopped before the gate
    /// finished: a stopped gate has no result, and is left out of the report like a gate that never started.
    /// </summary>
    /// <remarks>
    /// The time left on the deadline is taken once the slot is held, so a gate that waited for a slot is cut at the
    /// same deadline as one that did not, and a gate the deadline passed while it waited never starts.
    /// </remarks>
    private static GateResult? RunInSlot(
        ReviewGate gate,
        string workingDirectory,
        TimeSpan? deadline,
        long runStarted,
        SemaphoreSlim slots,
        CancellationToken stop)
    {
        try
        {
            slots.Wait(stop);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            TimeSpan? timeLeft = deadline - Stopwatch.GetElapsedTime(runStarted);
            if (deadline is { } passed && timeLeft <= TimeSpan.Zero)
                return DeadlinePassedBeforeStart(gate, passed);

            RunBudget? budget = deadline is { } limit ? new RunBudget(limit, timeLeft) : null;
            return RunOne(gate, workingDirectory, budget, stop);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            slots.Release();
        }
    }

    private static GateResult RunOne(ReviewGate gate, string workingDirectory, RunBudget? budget, CancellationToken stop)
    {
        string gateDirectory = ResolveWorkingDirectory(workingDirectory, gate.WorkingDirectory);
        return RunArgv(
            gate.Id, gate.Blocking, gate.Run, gate.TimeoutSeconds, gateDirectory, gate.FailOnOutput, budget, stop);
    }

    /// <summary>The run deadline and the time left on it when a gate starts.</summary>
    private sealed record RunBudget(TimeSpan Deadline, TimeSpan? TimeLeft);

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
        bool failOnOutput,
        RunBudget? budget = null,
        CancellationToken stop = default)
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
        TimeSpan ownTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        bool deadlineFirst = budget?.TimeLeft is { } left && left <= ownTimeout;
        TimeSpan timeout = deadlineFirst && budget?.TimeLeft is { } remaining ? remaining : ownTimeout;
        try
        {
            ProcessResult process = ProcessRunner.Run(startInfo, string.Empty, timeout, cancellationToken: stop);

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
        catch (TimeoutException ex) when (deadlineFirst)
        {
            return new GateResult(
                id,
                blocking,
                -1,
                $"stopped at the council deadline of {budget?.Deadline.TotalSeconds:0} seconds ({DeadlineKey}) " +
                $"after running {timeout.TotalSeconds:0} seconds: {ex.Message}",
                (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                Outcome: GateOutcome.DeadlinePassed);
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
