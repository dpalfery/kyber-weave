using KyberWeave.Cli.Commands.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Core.Review;

/// <summary>
///     The diagnostics <c>review gates</c> reports, and the rules that shape a gate report: which declared gates a run
///     takes, which earlier results a verifier cycle carries and which it keeps from the report already written.
/// </summary>
public static class ReviewGateReporting
{
    /// <summary>A review that declares no gates has no executed evidence.</summary>
    public const string NoGatesDeclared = "KW-REVIEW-020";

    /// <summary>The state of the CodeGraph index a review runs over.</summary>
    public const string IndexState = "KW-REVIEW-038";

    /// <summary>A gate the deadline or its own time limit stopped before a result.</summary>
    public const string GateNotRun = VerdictEngine.GateNotRun;

    /// <summary>A report file that could not be written.</summary>
    public const string ReportWriteFailed = "KW-REVIEW-025";

    /// <summary>A gate left out because it does not apply to the change.</summary>
    public const string GateSkipped = "KW-REVIEW-026";

    /// <summary>An agent with no task gates declared.</summary>
    public const string NoGatesForAgent = "KW-REVIEW-027";

    /// <summary>An input refused before any gate started.</summary>
    public const string InvalidInput = "KW-REVIEW-028";

    /// <summary>A task gate whose fix command did not succeed.</summary>
    public const string FixFailed = "KW-REVIEW-029";

    /// <summary>A gate that never started, or was refused.</summary>
    public const string GateNotStarted = "KW-REVIEW-033";

    /// <summary>The changed set could not be listed, so include-scoped gates ran unscoped.</summary>
    public const string ChangedSetUnknown = "KW-REVIEW-034";

    /// <summary>The prior cycle's findings could not be read.</summary>
    public const string PriorFindingsUnreadable = "KW-REVIEW-023";

    /// <summary>A gate whose earlier passing result this cycle carries.</summary>
    public const string GateCarried = "KW-REVIEW-048";

    /// <summary>A RED run accepted as valid failing evidence.</summary>
    public const string RedAccepted = "KW-REVIEW-051";

    /// <summary>A RED run that is not valid failing evidence.</summary>
    public const string RedRejected = "KW-REVIEW-052";

    /// <summary>A build that fails only because sibling tasks' tests name symbols nobody created.</summary>
    public const string SiblingTestsMissingSymbols = "KW-REVIEW-053";

    /// <summary>The ref the council's changed set is measured from when none is named.</summary>
    public const string CouncilDefaultBase = "origin/HEAD";

    /// <summary>The code-review skill reference a reviewer reads for this kind of cycle.</summary>
    /// <param name="verifier">Whether the cycle is a verifier cycle.</param>
    public static string CycleReference(bool verifier) =>
        verifier ? "references/verifier-cycle.md" : "references/first-review.md";

    /// <summary>
    ///     Whether the changed set must be computed before gates run. A gate can be scoped either by <c>include</c> or by
    ///     <c>applies-when.paths</c>; either one makes a null changed set mean "run everything".
    /// </summary>
    /// <param name="gates">The declared gates.</param>
    public static bool NeedsChangedSet(IReadOnlyList<ReviewGate> gates) =>
        gates.Any(gate => gate.Include.Count > 0 || gate.AppliesWhen is not null);

    /// <summary><c>windows</c> or <c>unix</c>, for a gate's <c>os</c> filter, read from the environment seam.</summary>
    /// <param name="environment">Reports the operating-system family the process runs on.</param>
    public static string CurrentOs(IProcessEnvironment environment) =>
        environment.OperatingSystem == OperatingSystemKind.Windows ? "windows" : "unix";

    /// <summary>
    ///     The declared gates the caller named with <c>--gate</c>, in declaration order, or an empty list with a
    ///     refusal naming what is wrong.
    /// </summary>
    /// <remarks>
    ///     Declaration order rather than the order the ids were given, because the gate report reads in the order the
    ///     host declares its suite. An id that names no declared gate is refused: a run that quietly executed nothing
    ///     would report zero gates, which a reader is entitled to take as a review that passed.
    /// </remarks>
    /// <param name="gates">The declared gates.</param>
    /// <param name="named">The ids given.</param>
    /// <param name="refusals">Receives the refusal.</param>
    public static IReadOnlyList<ReviewGate> SelectNamedGates(
        IReadOnlyList<ReviewGate> gates, IReadOnlyList<string> named, ICollection<string> refusals)
    {
        HashSet<string> wanted = new(named, StringComparer.Ordinal);
        List<string> unknown = [];
        foreach (string id in named)
        {
            if (!gates.Any(gate => string.Equals(gate.Id, id, StringComparison.Ordinal))
                && !unknown.Contains(id, StringComparer.Ordinal))
            {
                unknown.Add(id);
            }
        }

        if (unknown.Count > 0)
        {
            refusals.Add(
                $"--gate names no declared repository gate: {string.Join(", ", unknown)}. " +
                $"review.gates declares {string.Join(", ", gates.Select(gate => gate.Id))}.");
            return [];
        }

        return [.. gates.Where(gate => wanted.Contains(gate.Id))];
    }

    /// <summary>
    ///     The gate report with an earlier passing result in place of each gate this cycle did not run, in declaration
    ///     order, so the report is complete and the verdict reads the same evidence it would from a full run.
    /// </summary>
    /// <param name="declared">The declared gates.</param>
    /// <param name="ran">The report of the gates this cycle ran.</param>
    /// <param name="plan">The cycle plan holding the carried results.</param>
    /// <param name="report">Receives one finding per carried gate.</param>
    public static GateReport WithCarriedGates(
        IReadOnlyList<ReviewGate> declared, GateReport ran, ReviewCyclePlan plan, DiagnosticReport report)
    {
        List<GateResult> results = [];
        foreach (ReviewGate gate in declared)
        {
            if (ran.Gates.FirstOrDefault(result => string.Equals(result.Id, gate.Id, StringComparison.Ordinal)) is { } own)
            {
                results.Add(own);
                continue;
            }

            GateCycleDecision? decision = plan.Gates.FirstOrDefault(
                candidate => candidate is { Action: GateCycleAction.Carry, Carried: not null }
                             && string.Equals(candidate.GateId, gate.Id, StringComparison.Ordinal));
            if (decision?.Carried is not { } carried)
                continue;

            results.Add(carried with { Summary = $"carried from cycle {decision.FromCycle}: {carried.Summary}" });
            report.Add(new Diagnostic(
                GateCarried,
                Severity.Info,
                $"{gate.Id}: carried from cycle {decision.FromCycle} ({decision.Reason}).",
                gate.Id));
        }

        results.AddRange(ran.Gates.Where(result => !results.Contains(result)));
        return ran with { Gates = results };
    }

    /// <summary>
    ///     The report already written, with each result this run produced in place of the earlier result for the same
    ///     gate and the rest kept, in declaration order.
    /// </summary>
    /// <param name="declared">The declared gates.</param>
    /// <param name="ran">The report of this run.</param>
    /// <param name="existing">The report already on disk.</param>
    public static GateReport MergedWithExisting(IReadOnlyList<ReviewGate> declared, GateReport ran, GateReport existing)
    {
        List<GateResult> results = [];
        foreach (ReviewGate gate in declared)
        {
            GateResult? pick = ran.Gates.FirstOrDefault(result => string.Equals(result.Id, gate.Id, StringComparison.Ordinal))
                               ?? existing.Gates.FirstOrDefault(result => string.Equals(result.Id, gate.Id, StringComparison.Ordinal));
            if (pick is not null)
                results.Add(pick);
        }

        results.AddRange(existing.Gates.Where(old => results.All(kept => !string.Equals(kept.Id, old.Id, StringComparison.Ordinal))));
        return ran with { Gates = results };
    }

    /// <summary>Reports every gate the plan left out.</summary>
    /// <param name="report">Receives the findings.</param>
    /// <param name="skipped">The skipped gates.</param>
    public static void AddSkips(DiagnosticReport report, IEnumerable<SkippedTaskGate> skipped)
    {
        foreach (SkippedTaskGate skip in skipped)
            report.Add(new Diagnostic(
                GateSkipped,
                Severity.Info,
                $"{skip.Id}: skipped, {skip.Reason}.",
                skip.Id));
    }

    /// <summary>Reports every refusal as an error that ends the run before any gate process has started.</summary>
    /// <param name="report">Receives the findings.</param>
    /// <param name="reasons">What was refused.</param>
    /// <param name="hint">The remedy, or null for the default one.</param>
    public static void AddRefusals(DiagnosticReport report, IEnumerable<string> reasons, string? hint = null)
    {
        string defaultHint = "Refused before any gate started. Pass repository-relative paths, and a task slug " +
                             "made of lowercase alphanumerics and single hyphens.";
        foreach (string reason in reasons)
            report.Add(new Diagnostic(
                InvalidInput,
                Severity.Error,
                reason,
                "review gates",
                Hint: hint ?? defaultHint));
    }

    /// <summary>
    ///     Reports the index state under <see cref="IndexState" />. A failed index is a warning and never stops the
    ///     gates: a review is a read-only run, and ADR 0061 keeps read-only runs going without an index.
    /// </summary>
    /// <param name="report">Receives the finding.</param>
    /// <param name="root">The tree the index belongs to.</param>
    /// <param name="index">What the index guard found.</param>
    public static void AddIndexDiagnostic(DiagnosticReport report, string root, WorktreeIndexOutcome index)
    {
        if (index.Status is WorktreeIndexStatus.Ready)
        {
            report.Add(new Diagnostic(
                IndexState,
                Severity.Info,
                $"The CodeGraph index for '{root}' was {(index.Created ? "created" : "synced")}.",
                "review.gates"));
            return;
        }

        report.Add(new Diagnostic(
            IndexState,
            Severity.Warning,
            $"The CodeGraph index for '{root}' could not be prepared ({IndexReason(index)}), so the gates run anyway.",
            "review.gates",
            Hint: $"kyber-weave doctor --ensure {ShellQuote.Quote(root)}"));
    }

    /// <summary>The first failed check's reason, or a plain line when none says why.</summary>
    /// <param name="index">What the index guard found.</param>
    public static string IndexReason(WorktreeIndexOutcome index) =>
        index.Checks.FirstOrDefault(check => !check.Passed)?.Reason ?? "no reason was reported";

    /// <summary>
    ///     Reports one gate. A gate whose program never started is <see cref="GateNotStarted" />, kept apart from a
    ///     check that ran and failed (<see cref="ReviewGateOutcome.Failed" />), because they are fixed in different places.
    ///     Under <c>--deadline</c>, a gate that time stopped is <see cref="GateNotRun" />, a warning: it reached no
    ///     result, so it neither passed nor failed and does not decide the exit code. Without it, a timed-out gate
    ///     is a failed check, as it always was, so CI and task gates keep failing on a gate that hangs.
    /// </summary>
    /// <param name="report">Receives the finding.</param>
    /// <param name="gate">The gate's result.</param>
    /// <param name="timeIsNotRun">Whether a gate stopped for time is reported as not run.</param>
    public static void AddGateDiagnostic(DiagnosticReport report, GateResult gate, bool timeIsNotRun)
    {
        string detail = $"{gate.Id}: {gate.Summary} ({gate.DurationMilliseconds} ms)";

        if (gate.NotApplicableReason is not null)
        {
            report.Add(new Diagnostic(
                ReviewGateOutcome.NotApplicable,
                Severity.Info,
                detail,
                gate.Id));
            return;
        }

        if (timeIsNotRun && VerdictEngine.IsStoppedForTime(gate))
        {
            // A deadline cut is the council running out of budget, not a slow gate, so raising the gate's own
            // timeout would change nothing; the remedy is to run it alone.
            string hint = gate.Outcome == GateOutcome.DeadlinePassed
                ? $"Re-run it alone with 'kyber-weave review gates --gate {gate.Id}'."
                : $"Re-run it alone with 'kyber-weave review gates --gate {gate.Id}', or give it a longer time limit.";
            report.Add(new Diagnostic(
                GateNotRun,
                Severity.Warning,
                $"{detail}. NOT RUN to a result, so what it checks is unverified.",
                gate.Id,
                Hint: hint));
            return;
        }

        if (gate.Outcome == GateOutcome.NotStarted)
        {
            report.Add(new Diagnostic(
                GateNotStarted,
                gate.Blocking ? Severity.Error : Severity.Warning,
                detail,
                gate.Id));
            return;
        }

        report.Add(new Diagnostic(
            gate.Passed ? ReviewGateOutcome.Passed : ReviewGateOutcome.Failed,
            gate.Passed ? Severity.Info : gate.Blocking ? Severity.Error : Severity.Warning,
            detail,
            gate.Id));
    }

    /// <summary>
    ///     Tells a developer whose build fails only because other tasks' tests name symbols nobody has created that this
    ///     is the cause, in the exact words to report, so it does not have to guess.
    /// </summary>
    /// <param name="report">Receives the findings.</param>
    /// <param name="planned">The gates that were planned.</param>
    /// <param name="ran">What they reported.</param>
    /// <param name="outputs">Each gate's output, by gate id.</param>
    /// <param name="changed">The files the task changed.</param>
    /// <param name="repositoryRoot">Absolute repository root used to resolve rooted compiler paths, or null when unbound.</param>
    /// <param name="canonicalRepositoryRoot">Canonical absolute repository root when different from <paramref name="repositoryRoot"/>, or null.</param>
    public static void AddSiblingNotes(
        DiagnosticReport report,
        IReadOnlyList<PlannedTaskGate> planned,
        GateReport ran,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<string> changed,
        string? repositoryRoot = null,
        string? canonicalRepositoryRoot = null)
    {
        foreach (GateResult gate in ran.Gates.Where(result => !result.Passed))
        {
            PlannedTaskGate? plannedGate = planned.FirstOrDefault(candidate => string.Equals(candidate.Id, gate.Id, StringComparison.Ordinal));
            if (plannedGate is null || !outputs.TryGetValue(gate.Id, out string? output))
                continue;

            RedGateObservation observation = new(
                gate.Id,
                string.Join(' ', plannedGate.Run),
                gate.ExitCode,
                output,
                plannedGate.WorkingDirectory,
                repositoryRoot,
                canonicalRepositoryRoot);
            if (RedClassifier.SiblingSymbols(observation, changed) is not { Count: > 0 } symbols)
                continue;

            report.Add(new Diagnostic(
                SiblingTestsMissingSymbols,
                Severity.Warning,
                $"NOT VERIFIED: {observation.Command} — sibling RED tests reference symbols no task has created yet: {string.Join(", ", symbols)}",
                gate.Id,
                Hint: "Every compiler error is a missing symbol in a file this task did not change. Build the production projects "
                      + "this task touched, report the line above in the digest, and do not edit those tests."));
        }
    }

    /// <summary>
    ///     Judges a RED task's gate runs. A failed gate the classifier accepts is reported as passed with the reason, one
    ///     it rejects keeps its failure, and the verdict is one diagnostic carrying the classifier's summary or the next step.
    /// </summary>
    /// <param name="ran">What the gates reported.</param>
    /// <param name="planned">The gates that were planned.</param>
    /// <param name="outputs">Each gate's output, by gate id.</param>
    /// <param name="changed">The files the task changed.</param>
    /// <param name="projectOf">Names the project file a changed file belongs to.</param>
    /// <param name="report">Receives the verdict.</param>
    /// <param name="repositoryRoot">Absolute repository root used to resolve rooted compiler paths, or null when unbound.</param>
    /// <param name="canonicalRepositoryRoot">Canonical absolute repository root when different from <paramref name="repositoryRoot"/>, or null.</param>
    public static (GateReport Report, RedOutcome Outcome) ApplyRed(
        GateReport ran,
        IReadOnlyList<PlannedTaskGate> planned,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<string> changed,
        Func<string, string?> projectOf,
        DiagnosticReport report,
        string? repositoryRoot = null,
        string? canonicalRepositoryRoot = null)
    {
        List<RedGateObservation> observations = [];
        foreach (GateResult gate in ran.Gates)
        {
            PlannedTaskGate? plannedGate = planned.FirstOrDefault(candidate => string.Equals(candidate.Id, gate.Id, StringComparison.Ordinal));
            if (plannedGate is not null && outputs.TryGetValue(gate.Id, out string? output))
                observations.Add(new RedGateObservation(
                    gate.Id,
                    string.Join(' ', plannedGate.Run),
                    gate.ExitCode,
                    output,
                    plannedGate.WorkingDirectory,
                    repositoryRoot,
                    canonicalRepositoryRoot));
        }

        RedOutcome outcome = RedClassifier.Evaluate(
            observations,
            changed,
            projectOf,
            anyTestGate: planned.Any(gate => RedClassifier.IsTestGate(gate.Id, string.Join(' ', gate.Run))));
        List<GateResult> results = [];
        foreach (GateResult gate in ran.Gates)
        {
            if (outcome.AcceptedGates.TryGetValue(gate.Id, out string? why))
                results.Add(gate with { ExitCode = 0, Summary = "RED accepted: " + why });
            else
                results.Add(gate);
        }

        if (outcome.Passed)
        {
            report.Add(new Diagnostic(
                RedAccepted,
                Severity.Info,
                outcome.EvidenceLine,
                "review gates --red",
                Hint: "The failing run is recorded in this run's report; now implement the task and run the task gate again without --red."));
        }
        else
        {
            report.Add(new Diagnostic(
                RedRejected,
                Severity.Error,
                $"Not valid RED evidence: {outcome.Reason}",
                "review gates --red",
                Hint: "Valid RED is tests that ran and failed on an assertion, or a build that failed only on missing-symbol "
                      + "errors in the files this task changed. Fix what the message names, then run the same command again."));
        }

        return (ran with { Gates = results }, outcome);
    }

    /// <summary>
    ///     The last lines of a RED run: the task-gate status for the report just written, then <c>RED_EVIDENCE:</c>,
    ///     the classifier's summary, when the run is a valid failing run, or <c>RED_REJECTED:</c> with the reason when it
    ///     is not. The report is the proof; neither line goes into the completion digest.
    /// </summary>
    /// <param name="outcome">The classifier's verdict.</param>
    /// <param name="agent">The agent whose gates ran.</param>
    /// <param name="report">The report just written.</param>
    /// <param name="reportPath">Where it was written, or null.</param>
    /// <param name="runCount">The ordinal of this task-gate run, counting from 1.</param>
    public static IReadOnlyList<string> RedLines(RedOutcome outcome, string agent, GateReport report, string? reportPath, int runCount)
    {
        int skipped = report.Gates.Count(gate => gate.NotApplicable);
        string failed = outcome.Passed
            ? "none"
            : string.Join(",", report.Gates.Where(gate => !gate.Passed && !gate.NotApplicable).Select(gate => gate.Id).DefaultIfEmpty("none"));
        return
        [
            $"TASK_GATE: {(outcome.Passed ? "PASS" : "FAIL")} — agent={agent} gates={report.Gates.Count} failed={failed} skipped={skipped} report={reportPath ?? "<none>"} runs={runCount}",
            outcome.Passed ? outcome.EvidenceLine : $"RED_REJECTED: {outcome.Reason}",
        ];
    }
}
