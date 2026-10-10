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
    private static readonly IReadOnlySet<string> DelegateFacts = new HashSet<string>(StringComparer.Ordinal)
    {
        "caller",
        "delegation.target",
        "delegation.target-class",
        "delegation.target-profile",
        "delegation.prompt",
        "delegation.paths",
        "delegation.plan-file",
        "delegation.task",
        "plan.exists",
        "plan.status",
        "plan.development-mode",
        "plan.tasks.count",
        "plan.task",
        "plan.task.text",
        "plan.task.files",
        "plan.task.depends-on",
        "plan.task.skills",
        "plan.out-of-scope",
        "plan.test-contract.row",
        "ledger.in-flight.paths",
        "ledger.completed-tasks",
        "ledger.red-evidence",
        "harness.observes-returns",
        "roster.caller.delegates-to",
        "roster.descriptions",
        "rules.<id>.answer",
    };

    private static readonly IReadOnlySet<string> DelegatePlannerFacts = new HashSet<string>(StringComparer.Ordinal)
    {
        "caller",
        "delegation.target",
        "delegation.prompt",
        "delegation.markers",
        "delegation.plan-file",
        "config.planning-dirs",
    };

    /// <summary>Trigger name to the facts it supplies.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> FactsByTrigger { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["delegate"] = DelegateFacts,
            ["delegate.returned"] = Union(
                DelegateFacts,
                ["git.changed-paths.since-dispatch", "ledger.concurrent.paths"]),
            ["delegate.planner"] = DelegatePlannerFacts,
            ["delegate.planner.returned"] = Union(
                DelegatePlannerFacts,
                ["git.changed-paths.since-dispatch", "ledger.concurrent.paths"]),
            ["investigate"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "caller",
                "delegation.target",
                "delegation.target-class",
                "roster.caller.delegates-to",
            },
            ["investigate.returned"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "caller",
                "delegation.target",
                "delegation.target-class",
                "roster.caller.delegates-to",
                "git.changed-paths.since-dispatch",
                "ledger.concurrent.paths",
            },
            ["lens.spawn"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "lens.name",
                "lens.applicability",
                "review.changed-paths",
                "review.diff-summary",
            },
            ["lens.returned"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "finding.id",
                "finding.file",
                "finding.line",
                "finding.excerpt",
                "file.text-at-line",
                "review.changed-hunks",
            },
            ["refute.spawn"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "finding.id",
                "finding.claim",
                "finding.excerpt",
                "finding.file",
                "finding.line",
                "file.surroundings",
                "gates.report",
            },
            ["gate.select"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "gate.id",
                "gate.applies-when.paths",
                "review.changed-paths",
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
