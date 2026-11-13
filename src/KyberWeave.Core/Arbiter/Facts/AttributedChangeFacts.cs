using KyberWeave.Core.Review;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// Supplies <c>git.changed-paths.attributed</c> and <c>ledger.concurrent.paths</c> on a return: the paths that
/// changed since the dispatch, less those that belong to writer dispatches in flight in the same working tree at
/// any moment of its window.
/// </summary>
/// <remarks>
/// Workers dispatched in parallel share one run worktree, so the since-dispatch snapshot diff of one of them
/// holds what its siblings wrote. That diff is a machine fact, but it says nothing about who wrote each path.
/// A path a sibling's task declares is that sibling's. When a sibling declared no files and a remaining path lies
/// outside this dispatch's own task files, or the return is not in the ledger, the remaining paths cannot be attributed: the fact stays absent, the rules that read it answer for
/// that, and nothing blocks on it (ADR 0072).
/// </remarks>
internal static class AttributedChangeFacts
{
    private const string SinceDispatch = "git.changed-paths.since-dispatch";
    private const string Attributed = "git.changed-paths.attributed";
    private const string Concurrent = "ledger.concurrent.paths";

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
        IReadOnlyList<string> taskFiles = facts.TryGet("plan.task.files", out ArbiterFact? files)
            && files.Value is IEnumerable<string> declared
                ? [.. declared]
                : [];
        bool unknownWriter = overlap.AnyWithoutFiles && own.Any(path => !Covered(taskFiles, path));
        return unknownWriter ? facts : facts.With(Attributed, own, ArbiterFactLabel.Derived);
    }

    private static bool Covered(IEnumerable<string> patterns, string path) =>
        patterns.Any(pattern => PathGlob.IsMatch(pattern, path));
}
