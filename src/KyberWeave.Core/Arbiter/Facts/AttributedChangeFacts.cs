using KyberWeave.Core.Review;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// Supplies <c>git.changed-paths.attributed</c> and <c>ledger.concurrent.paths</c> on a return: the paths that
/// changed since the dispatch, less those that belong to writer dispatches in flight in the same working tree at
/// any moment of its window. Three returns read it: a worker's (<c>KW-ARB-DIFF-001</c>), a planner's
/// (<c>KW-ARB-PLANNER-DIFF-001</c>) and a read-only dispatch's (<c>KW-ARB-READONLY-001</c>).
/// </summary>
/// <remarks>
/// Workers dispatched in parallel share one run worktree, so the since-dispatch snapshot diff of one of them
/// holds what its siblings wrote. That diff is a machine fact, but it says nothing about who wrote each path.
/// A path a sibling's task declares is that sibling's. What this dispatch may legitimately write depends on what it is:
/// a worker its task files, a planner the planning directories, a read-only dispatch nothing. When a sibling declared
/// no files and a remaining path lies outside that, or the return is not in the ledger, the remaining paths cannot be
/// attributed: the fact stays absent, the rules that read it answer for that, and nothing blocks on it (ADR 0072).
/// </remarks>
internal static class AttributedChangeFacts
{
    private const string SinceDispatch = "git.changed-paths.since-dispatch";
    private const string Attributed = "git.changed-paths.attributed";
    private const string Concurrent = "ledger.concurrent.paths";
    private const string TaskFiles = "plan.task.files";
    private const string PlanningDirs = "config.planning-dirs";

    /// <summary>Adds the two facts when the trigger declares them and the ledger can place the dispatch.</summary>
    public static ArbiterFactSet Enrich(ArbiterFactSet facts, InFlightLedger ledger, DecisionLog decisions, string ledgerId)
    {
        if (!facts.Contains(Attributed)
            || !facts.TryGet(SinceDispatch, out ArbiterFact? since)
            || since.Value is not IEnumerable<string> changed)
        {
            return facts;
        }

        string? preId = ledger.ReadAll()
            .FirstOrDefault(e => string.Equals(e.Id, ledgerId, StringComparison.Ordinal))
            ?.PreId;
        if (ledger.ConcurrentWriters(preId, ledgerId, LedgerFacts.NotDispatchedLedgerIds(decisions)) is not { } overlap)
        {
            return facts;
        }

        facts = facts.With(Concurrent, overlap.Paths.ToList(), ArbiterFactLabel.Derived);
        List<string> own = [.. changed.Where(path => !Covered(overlap.Paths, path))];
        IReadOnlyList<string> mayWrite = OwnPatterns(facts);
        bool unknownWriter = overlap.AnyWithoutFiles && own.Any(path => !Covered(mayWrite, path));
        return unknownWriter ? facts : facts.With(Attributed, own, ArbiterFactLabel.Derived);
    }

    /// <summary>
    /// The paths the returning dispatch may write without anyone else being responsible: a worker's declared task files,
    /// else a planner's planning directories, else nothing (a read-only dispatch).
    /// </summary>
    private static IReadOnlyList<string> OwnPatterns(ArbiterFactSet facts)
    {
        string name = facts.Contains(TaskFiles) ? TaskFiles : PlanningDirs;
        return facts.TryGet(name, out ArbiterFact? fact) && fact.Value is IEnumerable<string> declared
            ? [.. declared]
            : [];
    }

    private static bool Covered(IEnumerable<string> patterns, string path) =>
        patterns.Any(pattern => PathGlob.IsMatch(pattern, path));
}
