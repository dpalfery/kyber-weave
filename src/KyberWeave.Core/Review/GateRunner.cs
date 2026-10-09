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
                : RunOne(gate, workingDirectory);
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

    private static GateResult RunOne(ReviewGate gate, string workingDirectory)
    {
        ProcessStartInfo startInfo = new(gate.Run[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (string argument in gate.Run.Skip(1))
            startInfo.ArgumentList.Add(argument);

        long start = Stopwatch.GetTimestamp();
        TimeSpan timeout = TimeSpan.FromSeconds(gate.TimeoutSeconds);
        try
        {
            ProcessResult process = ProcessRunner.Run(startInfo, string.Empty, timeout);
            return new GateResult(
                gate.Id,
                gate.Blocking,
                process.ExitCode,
                Summarize(process),
                (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (TimeoutException ex)
        {
            return new GateResult(
                gate.Id,
                gate.Blocking,
                -1,
                $"timed out after {gate.TimeoutSeconds} seconds: {ex.Message}",
                (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A gate that cannot start has not passed. Reporting it as a failure with the
            // reason keeps the distinction visible: "the tests failed" and "the test command
            // does not exist here" both block, but they are fixed very differently.
            return new GateResult(
                gate.Id,
                gate.Blocking,
                -1,
                $"could not start '{gate.Run[0]}': {ex.Message}",
                (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
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
