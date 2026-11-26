namespace KyberWeave.Core.Arbiter;

/// <summary>
/// Declares the facts each trigger supplies. The validator checks every rule
/// against this table (<c>KW-ARB-CONFIG-002</c>).
/// </summary>
/// <remarks>
/// <c>delegate.returned</c> and <c>delegate.planner.returned</c> extend their
/// pre-dispatch facts rather than restating them, so the two rows cannot drift
/// apart when a fact is added to one trigger.
/// </remarks>
public static class ArbiterTriggerCatalog
{
    /// <summary>
    /// The fact holding the task's one technology, from its <c>Technology:</c> label, when it names one. It
    /// selects the technology folder's rules; a task that names none leaves it absent.
    /// </summary>
    public const string TaskTechnologyFact = "plan.task.technology";

    private static readonly IReadOnlySet<string> DelegateFacts = new HashSet<string>(StringComparer.Ordinal)
    {
        "caller",
        "delegation.target",
        "delegation.target-class",
        "delegation.target-profile",
        "delegation.prompt",
        "delegation.head",
        "delegation.body-head",
        "delegation.markers",
        "delegation.paths",
        "delegation.planning-paths",
        "delegation.plan-file",
        "delegation.plan-file-raw",
        "delegation.task",
        "delegation.task-raw",
        "delegation.identity",
        "config.planning-dirs",
        "plan.exists",
        "plan.status",
        "plan.development-mode",
        "plan.tasks.count",
        "plan.task-list",
        "plan.task",
        "plan.task.text",
        "plan.task.files",
        "plan.task.depends-on",
        "plan.task.skills",
        "plan.task.technology",
        "plan.out-of-scope",
        "plan.test-contract.row",
        "ledger.in-flight.paths",
        "ledger.completed-tasks",
        "ledger.task-returns",
        "harness.observes-returns",
        "harness.model-override",
        "roster.caller.delegates-to",
        "roster.descriptions",
        "run.started",
        "rules.<id>.answer",
    };

    private static readonly IReadOnlySet<string> DelegatePlannerFacts = new HashSet<string>(StringComparer.Ordinal)
    {
        "caller",
        "delegation.target",
        "delegation.prompt",
        "delegation.head",
        "delegation.markers",
        "delegation.plan-file",
        "delegation.plan-file-raw",
        "delegation.plan-file-rejected",
        "delegation.target-profile",
        "config.planning-dirs",
        "roster.caller.delegates-to",
        "run.started",
    };

    /// <summary>Trigger name to the facts it supplies.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> FactsByTrigger { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ArbiterTriggers.Delegate] = DelegateFacts,
            [ArbiterTriggers.DelegateReturned] = Union(
                DelegateFacts,
                [
                    "git.changed-paths.since-dispatch", "git.changed-paths.attributed", "ledger.concurrent.paths", "gate.report.agrees",
                    "gate.report.failing-run-first", "gate.report.claim", "delegation.returned-tail", "checkout.stray-paths", "config.task-gates-declared",
                    "plan.task.files-all-tests",
                ]),
            [ArbiterTriggers.DelegatePlanner] = DelegatePlannerFacts,
            [ArbiterTriggers.DelegatePlannerReturned] = Union(
                DelegatePlannerFacts,
                ["git.changed-paths.since-dispatch", "git.changed-paths.attributed", "ledger.concurrent.paths", "checkout.stray-paths"]),
            [ArbiterTriggers.Investigate] = new HashSet<string>(StringComparer.Ordinal)
            {
                "caller",
                "delegation.target",
                "delegation.target-class",
                "delegation.target-profile",
                "delegation.markers",
                "roster.caller.delegates-to",
                "run.started",
            },
            [ArbiterTriggers.InvestigateReturned] = new HashSet<string>(StringComparer.Ordinal)
            {
                "caller",
                "delegation.target",
                "delegation.target-class",
                "delegation.markers",
                "roster.caller.delegates-to",
                "git.changed-paths.since-dispatch",
                "git.changed-paths.attributed",
                "ledger.concurrent.paths",
                "delegation.target-profile",
                "checkout.stray-paths",
            },
            [ArbiterTriggers.DelegateModel] = new HashSet<string>(StringComparer.Ordinal)
            {
                "plan.task.text",
                "plan.task.files",
                "plan.task.file-kinds",
                // The rating the task file records, which the difficulty rule reads under the tiered roster
                // instead of asking the model a second time.
                "plan.task.difficulty",
                "ledger.task.prior-attempt",
                "model-targeting.prior-result",
                "model-targeting.prior-profile",
                "model-targeting.ladder.next",
                "delegation.model",
                "harness.model-override",
                "harness.observes-returns",
            },
            [ArbiterTriggers.IntakeShortcut] = new HashSet<string>(StringComparer.Ordinal)
            {
                "intake.request",
            },
            [ArbiterTriggers.TriageShortcut] = new HashSet<string>(StringComparer.Ordinal)
            {
                "defect.report",
            },
            [ArbiterTriggers.DocsImpactShortcut] = new HashSet<string>(StringComparer.Ordinal)
            {
                "docs.governed-paths-changed",
                "review.changed-paths",
                "review.diff-summary",
            },
            [ArbiterTriggers.DelegateFailure] = new HashSet<string>(StringComparer.Ordinal)
            {
                "failure.previous-text",
                "failure.current-text",
                "ledger.task.prior-attempt",
            },
            [ArbiterTriggers.SectionsSelect] = new HashSet<string>(StringComparer.Ordinal)
            {
                "task.text",
                "section.candidates",
                "section.candidate.*",
            },
            [ArbiterTriggers.CommentTriage] = new HashSet<string>(StringComparer.Ordinal)
            {
                "comment.body",
                "comment.path",
                "comment.line",
                "comment.author",
                "file.text-at-line",
                "comment.fixing-commits",
            },
            // Asked by `review verdict` on the seat files, never by a hook.
            [ArbiterTriggers.ReviewVerdict] = new HashSet<string>(StringComparer.Ordinal)
            {
                "finding.severity",
                "finding.claim",
                "finding.failure-scenario",
                "override.class",
            },
            [ArbiterTriggers.LensSpawn] = new HashSet<string>(StringComparer.Ordinal)
            {
                "lens.name",
                "lens.applicability",
                "review.changed-paths",
                "review.base-ref",
                "review.diff-summary",
                "review.comment-only-paths",
                "review.rename-only-paths",
                "review.no-code-change-paths",
                "review.changed-hunks",
            },
            [ArbiterTriggers.LensReturned] = new HashSet<string>(StringComparer.Ordinal)
            {
                "finding.id",
                "finding.file",
                "finding.line",
                "finding.excerpt",
                "file.text-at-line",
                "review.changed-hunks",
                "review.comment-only-paths",
                "review.rename-only-paths",
                "review.no-code-change-paths",
                "finding.changed-lines",
                "finding.severity",
                "finding.gate-corroborated",
            },
            // A refuter's return reads no facts: its outcome is parsed from the return text, not a rule.
            [ArbiterTriggers.RefuteReturned] = new HashSet<string>(StringComparer.Ordinal),
            [ArbiterTriggers.RefuteSpawn] = new HashSet<string>(StringComparer.Ordinal)
            {
                "finding.id",
                "finding.claim",
                "finding.excerpt",
                "finding.file",
                "finding.line",
                "file.surroundings",
                "gates.report",
                "finding.dropped",
                "review.prior-findings",
                "review.changed-hunks",
                "review.comment-only-paths",
                "review.rename-only-paths",
                "review.no-code-change-paths",
            },
            [ArbiterTriggers.GateSelect] = new HashSet<string>(StringComparer.Ordinal)
            {
                "gate.id",
                "gate.applies-when.paths",
                "review.changed-paths",
                "review.base-ref",
            },
            // Raised by the worker guard on a sub-agent's own command call, so a technology's rules can
            // declare the forms a read-only dispatch may run.
            [ArbiterTriggers.WorkerCommand] = new HashSet<string>(StringComparer.Ordinal)
            {
                "worker.name",
                "worker.capability-profile",
                "worker.harness",
                "command.text",
                "command.form",
                "command.single",
                "command.head",
                "command.program",
                "command.words",
            },
        };

    /// <summary>Every fact any trigger supplies (with <c>rules.&lt;id&gt;.answer</c> as a pattern).</summary>
    public static IReadOnlySet<string> AllFacts { get; } = new HashSet<string>(
        FactsByTrigger.Values.SelectMany(facts => facts).Where(fact => !IsAnswerRefPattern(fact)),
        StringComparer.Ordinal);

    /// <summary>Whether <paramref name="fact"/> is a step-0 answer reference (<c>rules.&lt;id&gt;.answer</c>).</summary>
    public static bool IsAnswerRef(string fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return fact.StartsWith("rules.", StringComparison.Ordinal) &&
            fact.EndsWith(".answer", StringComparison.Ordinal) &&
            fact.Length > "rules.".Length + ".answer".Length;
    }

    /// <summary>Attempts to get the facts <paramref name="trigger"/> supplies.</summary>
    public static bool TryGetFacts(string trigger, out IReadOnlySet<string> facts)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (FactsByTrigger.TryGetValue(trigger, out IReadOnlySet<string>? found))
        {
            facts = found;
            return true;
        }

        facts = new HashSet<string>(StringComparer.Ordinal);
        return false;
    }

    private static bool IsAnswerRefPattern(string fact) => string.Equals(fact, "rules.<id>.answer", StringComparison.Ordinal);

    private static IReadOnlySet<string> Union(IReadOnlySet<string> first, IEnumerable<string> rest)
    {
        HashSet<string> union = new(first, StringComparer.Ordinal);
        foreach (string fact in rest)
            union.Add(fact);
        return union;
    }
}
