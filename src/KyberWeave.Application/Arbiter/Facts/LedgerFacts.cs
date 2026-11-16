using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// Supplies the facts the rules read from the ledger: <c>ledger.completed-tasks</c> and
/// <c>ledger.in-flight.paths</c>.
/// </summary>
internal static class LedgerFacts
{
    private const string CompletedTasks = "ledger.completed-tasks";
    private const string InFlightPaths = "ledger.in-flight.paths";

    /// <summary>
    /// Adds the ledger facts when <paramref name="facts"/> declares them, which only the
    /// triggers that read them do.
    /// </summary>
    public static ArbiterFactSet Enrich(
        ArbiterFactSet facts,
        IArbiterLedger ledger,
        IArbiterDecisionLog decisions,
        ArbiterEvent ev,
        string ledgerId,
        ArbiterRunScope? run = null)
    {
        if (!facts.Contains(CompletedTasks))
        {
            return facts;
        }

        string? planFile = Text(facts, "delegation.plan-file");
        facts = facts.With(CompletedTasks, ledger.CompletedTasks(planFile, run).ToList(), ArbiterFactLabel.Derived);
        return facts.With(InFlightPaths, ReadInFlight(ledger, decisions, ev, ledgerId, run), ArbiterFactLabel.Derived);
    }

    /// <summary>
    /// A harness that never reports returns would leave every dispatch in flight forever,
    /// and a refused dispatch never ran, so neither can hold a file.
    /// </summary>
    private static List<string> ReadInFlight(
        IArbiterLedger ledger,
        IArbiterDecisionLog decisions,
        ArbiterEvent ev,
        string ledgerId,
        ArbiterRunScope? run)
    {
        if (!ev.ObservesReturns)
        {
            return [];
        }

        return [.. ledger.InFlightPaths(ledgerId, ev.Session, NotDispatchedLedgerIds(decisions), run)];
    }

    /// <summary>
    /// The ledger ids whose dispatch never ran its worker: refused by an escalation, or answered by a shortcut
    /// that substituted a digest for the worker. The audit counts a spawn as a dispatch not in this set.
    /// </summary>
    /// <param name="decisions">The decision log the ids are read from.</param>
    public static IReadOnlySet<string> NotDispatchedLedgerIds(IArbiterDecisionLog decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        return decisions.ReadAll()
            .Where(decision => string.Equals(decision.Outcome, RuleEffects.Escalate, StringComparison.Ordinal)
                || string.Equals(decision.Outcome, RuleEffects.Answer, StringComparison.Ordinal))
            .Select(decision => decision.LedgerId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? Text(ArbiterFactSet facts, string name) =>
        facts.TryGet(name, out ArbiterFact? fact) && fact.Value is string { Length: > 0 } value ? value : null;
}
