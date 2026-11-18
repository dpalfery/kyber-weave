using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Testing.Squad;

/// <summary>Which state migration a crash-safety case runs.</summary>
public enum SquadStateMigrationFlow
{
    /// <summary>The legacy global receipt and lock are copied into the home.</summary>
    Global,

    /// <summary>One project's in-repo receipt and lock are imported into the home and its registry.</summary>
    Project
}

/// <summary>One crash-safety symptom of the state migration that is known and not yet fixed.</summary>
/// <param name="Flow">The migration that fails.</param>
/// <param name="DetailContains">The symptom this weakness records; another symptom stays unexplained.</param>
/// <param name="Points">The exact stop points (operation ordinals of the clean run) at which the symptom shows. A symptom at any other point stays unexplained, and a point listed here that no longer fails is a stale entry.</param>
/// <param name="Reason">One line saying what goes wrong and why.</param>
public sealed record SquadStateMigrationKnownWeakness(
    SquadStateMigrationFlow Flow,
    string DetailContains,
    IReadOnlyCollection<int> Points,
    string Reason);

/// <summary>Sorts the failures of a crash-safety run into those a known weakness explains exactly and those it does not.</summary>
public static class SquadStateMigrationFailureClassifier
{
    /// <summary>The failures no known weakness explains: its flow, its stop point and its symptom must all match.</summary>
    /// <param name="flow">The migration that ran.</param>
    /// <param name="failures">Every failure of the run, by stop point.</param>
    /// <param name="known">The listed weaknesses.</param>
    /// <returns>The unexplained failures.</returns>
    public static IReadOnlyList<(int Point, string Detail)> Unexplained(
        SquadStateMigrationFlow flow,
        IReadOnlyList<(int Point, string Detail)> failures,
        IReadOnlyList<SquadStateMigrationKnownWeakness> known)
    {
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(known);
        return [.. failures.Where(failure => !known.Any(weakness => Explains(weakness, flow, failure.Point, failure.Detail)))];
    }

    /// <summary>
    /// The stop points a listed weakness names that do not fail with its symptom: a fixed or narrowed weakness whose
    /// entry was left behind, which would let the symptom come back unnoticed at those points.
    /// </summary>
    /// <param name="flow">The migration that ran.</param>
    /// <param name="failures">Every failure of the run, by stop point.</param>
    /// <param name="known">The listed weaknesses.</param>
    /// <returns>One line per stale point.</returns>
    public static IReadOnlyList<string> Stale(
        SquadStateMigrationFlow flow,
        IReadOnlyList<(int Point, string Detail)> failures,
        IReadOnlyList<SquadStateMigrationKnownWeakness> known)
    {
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(known);
        return
        [
            .. known
                .Where(weakness => weakness.Flow == flow)
                .SelectMany(weakness => weakness.Points
                    .Where(point => !failures.Any(failure => failure.Point == point && Explains(weakness, flow, failure.Point, failure.Detail)))
                    .Select(point => $"'{weakness.DetailContains}' no longer fails at operation {point}")),
        ];
    }

    /// <summary>Whether a failure is exactly the symptom of a weakness at one of its stop points.</summary>
    /// <param name="weakness">The listed weakness.</param>
    /// <param name="flow">The migration that ran.</param>
    /// <param name="point">The stop point of the failure.</param>
    /// <param name="detail">What failed.</param>
    /// <returns>True when flow, point and symptom all match.</returns>
    public static bool Explains(SquadStateMigrationKnownWeakness weakness, SquadStateMigrationFlow flow, int point, string detail)
    {
        ArgumentNullException.ThrowIfNull(weakness);
        ArgumentNullException.ThrowIfNull(detail);
        return weakness.Flow == flow &&
            weakness.Points.Contains(point) &&
            detail.Contains(weakness.DetailContains, StringComparison.Ordinal);
    }
}

/// <summary>
/// The crash-safety contract of the Squad state migration: stop it at every file operation, restart the process and
/// run it again, and end with the same tree an uninterrupted run leaves.
/// </summary>
/// <remarks>
/// <para>
/// A migration writes a lock, a receipt and a ledger entry (and, for a project, a registry entry) in a fixed order, and
/// recovery is running it again. The contract therefore asserts, for every operation the clean run performs:
/// after the stop, nothing outside the new state changed, every path the run created holds its final content, no
/// receipt exists without its lock and no temporary file is left; after the restart and a second run, the tree equals
/// the clean run's tree exactly.
/// </para>
/// <para>
/// The suite is written once and run per file system: <see cref="InMemorySquadStateCrashWorkspace"/> in the unit
/// project and <see cref="PhysicalSquadStateCrashWorkspace"/> in the integration project. A symptom that fails today is
/// a known weakness, listed in <see cref="KnownWeaknesses"/> and reported with <see cref="Assert.Skip"/>, so the run
/// stays green and the weakness stays visible.
/// </para>
/// </remarks>
public abstract class SquadStateMigrationCrashSafetyContractTests
{
    /// <summary>Creates the workspace one case runs in. The suite disposes it.</summary>
    /// <returns>A fresh workspace.</returns>
    protected abstract SquadStateCrashWorkspace CreateWorkspace();

    /// <summary>The symptoms that fail today, each an existing crash-safety weakness.</summary>
    protected virtual IReadOnlyList<SquadStateMigrationKnownWeakness> KnownWeaknesses => [];

    /// <summary>The global migration stopped at every operation recovers by running again.</summary>
    [Fact]
    public void GlobalMigrationStoppedAtEveryOperationCompletesWhenRunAgain() =>
        Run(SquadStateMigrationFlow.Global);

    /// <summary>The project import stopped at every operation recovers by running again.</summary>
    [Fact]
    public void ProjectImportStoppedAtEveryOperationCompletesWhenRunAgain() =>
        Run(SquadStateMigrationFlow.Project);

    private static void Seed(SquadStateCrashWorkspace workspace, SquadStateMigrationFlow flow)
    {
        if (flow == SquadStateMigrationFlow.Global)
        {
            string root = workspace.CreateRoot("customer-alpha");
            workspace.Layout.WriteLegacyGlobal(
                root,
                SquadStateTestLayout.PerTargetRootsReceipt(),
                SquadStateTestLayout.Lock());
            return;
        }

        workspace.Layout.WriteInRepoState(workspace.CreateRoot("repo"), SquadStateTestLayout.Lock());
    }

    private static void Execute(SquadStateCrashWorkspace workspace, SquadStateMigrationFlow flow)
    {
        if (flow == SquadStateMigrationFlow.Global)
        {
            workspace.Migration.MigrateGlobal(invokingCwd: null);
            return;
        }

        workspace.Migration.ImportProject(workspace.CreateRoot("repo"));
    }

    private void Run(SquadStateMigrationFlow flow)
    {
        IReadOnlyDictionary<string, SquadTreeEntry> before;
        IReadOnlyDictionary<string, SquadTreeEntry> after;
        int operations;
        using (SquadStateCrashWorkspace clean = CreateWorkspace())
        {
            Seed(clean, flow);
            before = clean.Capture();
            int start = clean.OperationCount;
            Execute(clean, flow);
            operations = clean.OperationCount - start;
            after = clean.Capture();
        }

        if (SquadTreeComparison.Difference(before, after) is null)
        {
            throw new InvalidOperationException(
                $"The clean {flow} migration left the tree unchanged, so an interrupted run could always pass as the old tree.");
        }

        if (operations == 0)
        {
            throw new InvalidOperationException($"The clean {flow} migration performed no file operation, so there is nothing to stop.");
        }

        List<(int Point, string Detail)> failures = [];
        for (int point = 1; point <= operations; point++)
        {
            using SquadStateCrashWorkspace workspace = CreateWorkspace();
            Seed(workspace, flow);
            IReadOnlyDictionary<string, SquadTreeEntry> baseline = workspace.Capture();
            workspace.KillAt(workspace.OperationCount + point);

            Exception? stop = Record.Exception(() => Execute(workspace, flow));
            if (stop is null || !workspace.IsKill(stop))
            {
                failures.Add((point, stop is null
                    ? "the migration completed without reaching the stop"
                    : $"the migration reported the stop as {stop.GetType().Name}: {stop.Message}"));
                continue;
            }

            workspace.Restart();
            IReadOnlyDictionary<string, SquadTreeEntry> crashed = workspace.Capture();
            failures.AddRange(AfterStop(baseline, after, crashed).Select(detail => (point, detail)));

            Exception? rerun = Record.Exception(() => Execute(workspace, flow));
            if (rerun is not null)
            {
                failures.Add((point, $"running again threw {rerun.GetType().Name}: {rerun.Message}"));
                continue;
            }

            IReadOnlyList<string> differences = SquadTreeComparison.Differences(after, workspace.Capture());
            if (differences.Count > 0)
            {
                failures.Add((point, $"after running again the tree is not the clean run's tree: {string.Join("; ", differences)} (stopped at: {stop.Message})"));
            }
        }

        Report(flow, failures, operations);
    }

    private static IEnumerable<string> AfterStop(
        IReadOnlyDictionary<string, SquadTreeEntry> baseline,
        IReadOnlyDictionary<string, SquadTreeEntry> final,
        IReadOnlyDictionary<string, SquadTreeEntry> crashed)
    {
        foreach ((string path, SquadTreeEntry entry) in baseline)
        {
            if (!crashed.TryGetValue(path, out SquadTreeEntry? now))
            {
                yield return $"a file the migration never owned is missing after the stop: '{path}'";
            }
            else if (entry.Kind != SquadTreeEntryKind.Directory &&
                     SquadTreeComparison.EntryDifference(path, entry, now) is { } changed)
            {
                yield return $"a file the migration never owned changed: {changed}";
            }
        }

        foreach ((string path, SquadTreeEntry entry) in crashed)
        {
            if (baseline.ContainsKey(path))
            {
                continue;
            }

            if (path.EndsWith(".tmp", StringComparison.Ordinal))
            {
                yield return $"a temporary file was left behind: '{path}'";
            }
            else if (!final.TryGetValue(path, out SquadTreeEntry? expected))
            {
                yield return $"the stop left '{path}', which the finished migration never writes";
            }
            else if (entry.Kind != SquadTreeEntryKind.Directory &&
                     SquadTreeComparison.EntryDifference(path, expected, entry) is { } partial)
            {
                yield return $"the stop left a partly written file: {partial}";
            }

            if (path.EndsWith("/squad.receipt.json", StringComparison.Ordinal) &&
                !crashed.ContainsKey(path[..^"squad.receipt.json".Length] + "squad.lock.yml"))
            {
                yield return $"a receipt exists without its lock: '{path}'";
            }
        }
    }

    private void Report(SquadStateMigrationFlow flow, List<(int Point, string Detail)> failures, int operations)
    {
        IReadOnlyList<(int Point, string Detail)> unexplained =
            SquadStateMigrationFailureClassifier.Unexplained(flow, failures, KnownWeaknesses);
        IReadOnlyList<string> stale = SquadStateMigrationFailureClassifier.Stale(flow, failures, KnownWeaknesses);
        Assert.True(
            unexplained.Count == 0 && stale.Count == 0,
            $"{flow} has {unexplained.Count} crash-safety failure(s) that no known weakness explains and {stale.Count} stale known weakness point(s):\n" +
            string.Join("\n", unexplained.Select(failure => $"{flow} stopped at operation {failure.Point}: {failure.Detail}")) +
            string.Join("\n", stale));

        if (failures.Count == 0)
        {
            return;
        }

        Assert.Skip(
            "Known weakness: " +
            string.Join(
                " ",
                KnownWeaknesses
                    .Where(weakness => weakness.Flow == flow)
                    .Select(weakness => $"[{weakness.Flow}] {weakness.Reason} It shows at {weakness.Points.Count} of {operations} operations.")));
    }
}
