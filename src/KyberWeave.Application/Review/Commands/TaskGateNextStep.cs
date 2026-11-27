using KyberWeave.Core;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>
///     The lines a developer's task-gate run ends with: the one next step, and on a passing verification run the
///     completion digest with every field the run knows already filled in, so the worker pastes it rather than
///     reading the task-gate skill to compose it.
/// </summary>
/// <remarks>
///     Only the run's own facts are filled: the status, the files from <c>--paths</c>, the gate counts, the report
///     path and the number of task-gate runs the caller counted. The test counts, the commit and the remote head are
///     not known to one invocation and stay as placeholders.
/// </remarks>
public static class TaskGateNextStep
{
    /// <summary>The prefix of the next-step line.</summary>
    public const string NextPrefix = "next: ";

    /// <summary>The scratch path a task's passing report is written to.</summary>
    /// <param name="task">The task id, normalised so a path-like or multi-line value cannot name another folder.</param>
    /// <exception cref="ArgumentException">The task id leaves nothing a folder can be named with.</exception>
    public static string PassingReportPath(string task) => ScratchLayout.TaskGateReport(Slug(task), red: false);

    /// <summary>The scratch path a task's failing-run (<c>--red</c>) report is written to.</summary>
    /// <param name="task">The task id, normalised so a path-like or multi-line value cannot name another folder.</param>
    /// <exception cref="ArgumentException">The task id leaves nothing a folder can be named with.</exception>
    public static string FailingReportPath(string task) => ScratchLayout.TaskGateReport(Slug(task), red: true);

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
                    ? $"kyber-weave review gates . --agent {Quoted(agent)} --task {Slug(task)}"
                    : Command(agent, paths, task, PassingReportPath(task ?? "<task-id>"))),
            ]
            : [
                NextPrefix + "fix what RED_REJECTED names, without adding a stub to make the tests compile, and run " +
                "this command again before implementing.",
            ];

    /// <summary>The lines after a run without <c>--red</c>.</summary>
    /// <param name="report">The gate report the run wrote.</param>
    /// <param name="declared">The agent's declared gates, to tell a whole-project gate from a scoped one.</param>
    /// <param name="fixRun">Whether the run's fix commands changed a task file, which then does not count as the verification run.</param>
    /// <param name="agent">The agent the gates belong to.</param>
    /// <param name="paths">The files the task changed, as given with <c>--paths</c>.</param>
    /// <param name="reportPath">Where the report was written, or null.</param>
    /// <param name="runCount">The ordinal of this task-gate run, counting from 1.</param>
    public static IReadOnlyList<string> AfterRun(
        GateReport report,
        IReadOnlyList<ReviewTaskGateView> declared,
        bool fixRun,
        string agent,
        IReadOnlyList<string> paths,
        string? reportPath,
        int runCount)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(declared);

        List<GateResult> failed = [.. report.Gates.Where(gate => !gate.Passed && !gate.NotApplicable)];
        if (failed.Count > 0)
        {
            List<string> lines =
            [
                NextPrefix + "fix what the failing gate names, then run this command with --fix; run it once more " +
                "without --fix only when that run says its fixes changed a task file. " +
                "Stop after 2 such cycles; if a gate still fails, return " + ReturnStatus.EscalationStatusLine + ", " +
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

        return Digest(report, agent, paths, reportPath, runCount);
    }

    /// <summary>The completion digest for an agent with no declared gates.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="paths">The files the task changed.</param>
    /// <param name="reportPath">Where the empty report was written, or null.</param>
    /// <param name="runCount">The ordinal of this task-gate run, counting from 1.</param>
    public static IReadOnlyList<string> NoneDeclared(string agent, IReadOnlyList<string> paths, string? reportPath, int runCount) =>
        Digest(new GateReport(GateReport.CurrentSchema, []), agent, paths, reportPath, runCount);

    private static IReadOnlyList<string> Digest(
        GateReport report, string agent, IReadOnlyList<string> paths, string? reportPath, int runCount)
    {
        int skipped = report.Gates.Count(gate => gate.NotApplicable);
        string status = report.Gates.Count == 0
            ? ReturnStatus.NoneDeclared
            : skipped == report.Gates.Count ? ReturnStatus.NoneApplicable : ReturnStatus.Pass;
        return
        [
            NextPrefix + "commit your task's files and tests, then return this completion digest with that commit's sha; fill in the <...> fields:",
            ReturnStatus.Prefix + " " + ReturnStatus.Pass,
            "FILES: " + (paths.Count > 0 ? string.Join(", ", paths) : "<comma-separated paths>"),
            "TESTS: total=<N> failed=<N> passed=<N>",
            "COMMIT: <commit-sha>",
            "REMOTE_HEAD: <remote-head-sha | UNPUSHED>",
            $"{ReturnStatus.TaskGate} {status} — agent={agent} gates={report.Gates.Count} failed=none skipped={skipped} " +
            $"report={reportPath ?? "<none>"} runs={runCount}",
        ];
    }

    private static string Command(string agent, IReadOnlyList<string> paths, string? task, string outPath)
    {
        string files = paths.Count > 0 ? string.Join(' ', paths.Select(path => "--paths " + Quoted(path))) : "--paths <file>";
        string taskWord = task is null ? "<task-id>" : Slug(task);
        return $"kyber-weave review gates . --agent {Quoted(agent)} {files} --task {taskWord} --out {outPath}";
    }

    /// <summary>The task's slug, which names its scratch folder. A value that leaves none is refused, never written to a guessed folder.</summary>
    private static string Slug(string task)
    {
        string slug = TaskSlug.Normalise(task);
        return slug.Length > 0
            ? slug
            : throw new ArgumentException("A task id made only of punctuation cannot name a task folder.", nameof(task));
    }

    /// <summary>
    /// The path as a shell word: left bare when every character is plain, quoted otherwise, so a path carrying a
    /// separator or a space stays one argument when the line is pasted.
    /// </summary>
    private static string Quoted(string path) =>
        path.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '-' or '_')
            ? path
            : ShellQuote.Quote(path);
}

/// <summary>What the next step needs to know about one declared task gate.</summary>
/// <param name="Id">The gate's id.</param>
/// <param name="WholeProject">Whether its command checks the whole project rather than the changed files.</param>
public sealed record ReviewTaskGateView(string Id, bool WholeProject);
