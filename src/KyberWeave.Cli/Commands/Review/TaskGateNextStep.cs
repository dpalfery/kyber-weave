using KyberWeave.Core.Review;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     The lines a developer's task-gate run ends with: the one next step, and on a passing verification run the
///     completion digest with every field the run knows already filled in, so the worker pastes it rather than
///     reading the task-gate skill to compose it.
/// </summary>
/// <remarks>
///     Only the run's own facts are filled: the status, the files from <c>--paths</c>, the gate counts and the report
///     path. The test counts, the commit, the remote head and the number of runs are not known to one invocation and
///     stay as placeholders.
/// </remarks>
internal static class TaskGateNextStep
{
    /// <summary>The prefix of the next-step line.</summary>
    public const string NextPrefix = "next: ";

    /// <summary>The scratch path a task's passing report is written to.</summary>
    public static string PassingReportPath(string task) => $".agent_scratchpad/{task}/task-gate.json";

    /// <summary>The scratch path a task's failing-run (<c>--red</c>) report is written to.</summary>
    public static string FailingReportPath(string task) => $".agent_scratchpad/{task}/task-gate-red.json";

    /// <summary>The lines after a <c>--red</c> run.</summary>
    /// <param name="accepted">Whether the run was accepted as the failing run.</param>
    /// <param name="agent">The agent the gates belong to.</param>
    /// <param name="paths">The files the task changed, as given with <c>--paths</c>.</param>
    /// <param name="task">The task id, or null when none was given.</param>
    /// <param name="fromPlan">Whether the files came from the plan's task, so the short command finds them again.</param>
    public static IReadOnlyList<string> AfterFailingRun(bool accepted, string agent, IReadOnlyList<string> paths, string? task, bool fromPlan = false) =>
        accepted
            ? [
                NextPrefix + "the failing run is recorded; do not paste its lines into the digest. Implement the task, " +
                "then run the passing run:",
                "  " + (fromPlan && task is not null
                    ? $"kyber-weave review gates . --agent {agent} --task {task}"
                    : Command(agent, paths, task, PassingReportPath(task ?? "<task-id>"))),
            ]
            : [
                NextPrefix + "fix what RED_REJECTED names, without adding a stub to make the tests compile, and run " +
                "this command again before implementing.",
            ];

    /// <summary>The lines after a run without <c>--red</c>.</summary>
    /// <param name="report">The gate report the run wrote.</param>
    /// <param name="declared">The agent's declared gates, to tell a whole-project gate from a scoped one.</param>
    /// <param name="fixRun">Whether the run applied fixes, which never counts as the verification run.</param>
    /// <param name="agent">The agent the gates belong to.</param>
    /// <param name="paths">The files the task changed, as given with <c>--paths</c>.</param>
    /// <param name="reportPath">Where the report was written, or null.</param>
    public static IReadOnlyList<string> AfterRun(
        GateReport report,
        IReadOnlyList<ReviewTaskGateView> declared,
        bool fixRun,
        string agent,
        IReadOnlyList<string> paths,
        string? reportPath)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(declared);

        List<GateResult> failed = [.. report.Gates.Where(gate => !gate.Passed && !gate.NotApplicable)];
        if (failed.Count > 0)
        {
            List<string> lines =
            [
                NextPrefix + "fix what the failing gate names, then run this command with --fix and again without it. " +
                "Stop after 2 such cycles; if a gate still fails, return STATUS: ESCALATION, " +
                "CIRCUIT_BREAKER_TRIGGER: ITERATION_CAP_EXCEEDED and FAILURE_CLUSTER: task-gate/" + failed[0].Id +
                " (the task-gate skill's Escalation section).",
            ];
            foreach (GateResult gate in failed.Where(gate => declared.Any(view => view.Id == gate.Id && view.WholeProject)))
                lines.Add($"  {gate.Id} checks the whole project, so it can fail on a file another worker is still editing: " +
                          "re-run it once after that change settles before counting the failure.");
            return lines;
        }

        if (fixRun)
            return [NextPrefix + "run this command again without --fix; only that run counts."];

        return Digest(report, agent, paths, reportPath);
    }

    /// <summary>The completion digest for an agent with no declared gates.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="paths">The files the task changed.</param>
    /// <param name="reportPath">Where the empty report was written, or null.</param>
    public static IReadOnlyList<string> NoneDeclared(string agent, IReadOnlyList<string> paths, string? reportPath) =>
        Digest(new GateReport(GateReport.CurrentSchema, []), agent, paths, reportPath);

    private static IReadOnlyList<string> Digest(GateReport report, string agent, IReadOnlyList<string> paths, string? reportPath)
    {
        int skipped = report.Gates.Count(gate => gate.NotApplicable);
        string status = report.Gates.Count == 0
            ? "NONE_DECLARED"
            : skipped == report.Gates.Count ? "NONE_APPLICABLE" : "PASS";
        return
        [
            NextPrefix + "return this completion digest; fill in the <...> fields:",
            "STATUS: PASS",
            "FILES: " + (paths.Count > 0 ? string.Join(", ", paths) : "<comma-separated paths>"),
            "TESTS: total=<N> failed=<N> passed=<N>",
            "COMMIT: <commit-sha | NONE>",
            "REMOTE_HEAD: <remote-head-sha | UNPUSHED>",
            $"TASK_GATE: {status} — agent={agent} gates={report.Gates.Count} failed=none skipped={skipped} " +
            $"report={reportPath ?? "<none>"} runs=<k>",
        ];
    }

    private static string Command(string agent, IReadOnlyList<string> paths, string? task, string outPath)
    {
        string files = paths.Count > 0 ? string.Join(' ', paths.Select(path => "--paths " + path)) : "--paths <file>";
        return $"kyber-weave review gates . --agent {agent} {files} --task {task ?? "<task-id>"} --out {outPath}";
    }
}

/// <summary>What the next step needs to know about one declared task gate.</summary>
/// <param name="Id">The gate's id.</param>
/// <param name="WholeProject">Whether its command checks the whole project rather than the changed files.</param>
internal sealed record ReviewTaskGateView(string Id, bool WholeProject);
