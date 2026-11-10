using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// Supplies the facts the rules read from the ledger: <c>ledger.completed-tasks</c>,
/// <c>ledger.red-evidence</c> and <c>ledger.in-flight.paths</c>.
/// </summary>
internal static class LedgerFacts
{
    private const string CompletedTasks = "ledger.completed-tasks";
    private const string RedEvidence = "ledger.red-evidence";
    private const string InFlightPaths = "ledger.in-flight.paths";

    /// <summary>
    /// Adds the ledger facts when <paramref name="facts"/> declares them, which only the
    /// triggers that read them do. <c>ledger.red-evidence</c> stays absent until a return
    /// names the task or one of its dependencies, because the rule tests for its presence.
    /// </summary>
    public static ArbiterFactSet Enrich(
        ArbiterFactSet facts,
        InFlightLedger ledger,
        DecisionLog decisions,
        ArbiterEvent ev,
        string ledgerId)
    {
        if (!facts.Contains(CompletedTasks))
        {
            return facts;
        }

        string? planFile = Text(facts, "delegation.plan-file");
        facts = facts.With(CompletedTasks, ledger.CompletedTasks(planFile).ToList(), ArbiterFactLabel.Derived);

        string? redTask = FirstRedTask(facts, ledger, planFile);
        if (redTask is not null)
        {
            facts = facts.With(RedEvidence, redTask, ArbiterFactLabel.Derived);
        }

        return facts.With(InFlightPaths, ReadInFlight(ledger, decisions, ev, ledgerId), ArbiterFactLabel.Derived);
    }

    private static string? FirstRedTask(ArbiterFactSet facts, InFlightLedger ledger, string? planFile)
    {
        string? task = Text(facts, "plan.task");
        if (task is null)
        {
            return null;
        }

        HashSet<string> candidates = new(StringComparer.Ordinal) { task };
        if (facts.TryGet("plan.task.depends-on", out ArbiterFact? dependsOn)
            && dependsOn.Value is IEnumerable<string> dependencies)
        {
            candidates.UnionWith(dependencies);
        }

        return ledger.RedEvidenceTasks(planFile).FirstOrDefault(candidates.Contains);
    }

    /// <summary>
    /// A harness that never reports returns would leave every dispatch in flight forever,
    /// and a refused dispatch never ran, so neither can hold a file.
    /// </summary>
    private static List<string> ReadInFlight(
        InFlightLedger ledger,
        DecisionLog decisions,
        ArbiterEvent ev,
        string ledgerId)
    {
        if (!ev.ObservesReturns)
        {
            return [];
        }

        HashSet<string> refused = decisions.ReadAll()
            .Where(decision => string.Equals(decision.Outcome, RuleEffects.Escalate, StringComparison.Ordinal))
            .Select(decision => decision.LedgerId)
            .ToHashSet(StringComparer.Ordinal);
        return [.. ledger.InFlightPaths(ledgerId, ev.Session, refused)];
    }

    private static string? Text(ArbiterFactSet facts, string name) =>
        facts.TryGet(name, out ArbiterFact? fact) && fact.Value is string { Length: > 0 } value ? value : null;
}
