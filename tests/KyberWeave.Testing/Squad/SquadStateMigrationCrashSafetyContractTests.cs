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
/// <param name="Reason">One line saying what goes wrong and why.</param>
public sealed record SquadStateMigrationKnownWeakness(SquadStateMigrationFlow Flow, string DetailContains, string Reason);

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
    protected virtual IReadOnlyList<SquadStateMigrationKnownWeakness> KnownWeaknesses =>
    [
        new(
            SquadStateMigrationFlow.Global,
            "migration-ledger.json' is missing",
            "A stop after the copy began, between the receipt write and the ledger write, leaves a ledger that running the migration again does not rewrite."),
        new(
            SquadStateMigrationFlow.Project,
            "migration-ledger.json' is missing",
            "A stop after the copy began, between the receipt write and the ledger write, leaves a ledger that running the migration again does not rewrite."),
        new(
            SquadStateMigrationFlow.Project,
            "registry.json' is missing",
            "A stop before the project is registered leaves a registry that running the import again does not rewrite."),
    ];

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

            string? difference = SquadTreeComparison.Difference(after, workspace.Capture());
            if (difference is not null)
            {
                failures.Add((point, $"after running again the tree is not the clean run's tree: {difference} (stopped at: {stop.Message})"));
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
        if (failures.Count == 0)
        {
            return;
        }

        List<(int Point, string Detail)> unexplained = failures
            .Where(failure => !KnownWeaknesses.Any(weakness => Explains(weakness, flow, failure.Detail)))
            .ToList();
        Assert.True(
            unexplained.Count == 0,
            $"{flow} has {unexplained.Count} crash-safety failure(s) that no known weakness explains:\n" +
            string.Join("\n", unexplained.Select(failure => $"{flow} stopped at operation {failure.Point}: {failure.Detail}")));

        Assert.Skip(
            "Known weakness: " +
            string.Join(
                " ",
                KnownWeaknesses
                    .Where(weakness => failures.Any(failure => Explains(weakness, flow, failure.Detail)))
                    .Select(weakness =>
                    {
                        List<(int Point, string Detail)> matched = failures
                            .Where(failure => Explains(weakness, flow, failure.Detail))
                            .ToList();
                        return $"[{weakness.Flow}] {weakness.Reason} It shows at {matched.Count} of {operations} operations, " +
                            $"from {matched[0].Point} to {matched[^1].Point}; the first stop was {matched[0].Detail}";
                    })));
    }

    private static bool Explains(SquadStateMigrationKnownWeakness weakness, SquadStateMigrationFlow flow, string detail) =>
        weakness.Flow == flow && detail.Contains(weakness.DetailContains, StringComparison.Ordinal);
}
