using System.Collections.Frozen;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Testing.Squad;

/// <summary>Which instance link sync a crash-safety case runs.</summary>
public enum SquadLinkSyncFlow
{
    /// <summary>The first sync of an instance: every main home entry is linked and the links are recorded.</summary>
    FirstSync,

    /// <summary>A later sync after a main home file was removed: its recorded link is deleted.</summary>
    StaleLinkRemoved,

    /// <summary>A later sync after a harness rewrote a linked file with the same bytes: the copy is replaced by the link again.</summary>
    IdenticalSplitRelinked,

    /// <summary>A later sync after a harness rewrote a linked file with other bytes: both copies are kept.</summary>
    DifferingSplitKept,

    /// <summary>A sync of an instance that holds the person's own file where a link would go: the file is kept.</summary>
    UnmanagedCopyKept,

    /// <summary>A later sync after a recorded folder link was replaced by a real folder holding the person's files: the folder is kept.</summary>
    ReplacedFolderKept,

    /// <summary>A later sync after a harness rewrote a linked file and its main home source was removed: the only copy is kept.</summary>
    RewrittenFileWithoutSourceKept
}

/// <summary>
/// The crash-safety contract of the instance link sync, which acts on the person's own files: it deletes and re-creates
/// links in the instance root and rewrites the recorded links, and it must never change the main home or lose a file the
/// person owns, however the process is stopped.
/// </summary>
/// <remarks>
/// <para>
/// For every operation the clean run performs, the process is stopped there. After the stop the contract asserts that
/// the main home is exactly as it was, that every regular file the instance held is either unchanged or was replaced by
/// a link to a main home file with the same bytes, that each path the sync created holds its final content, that the
/// recorded links file is whole (the old one or the new one) and that no temporary file is left. After a restart and a
/// second sync the tree equals the clean run's tree exactly.
/// </para>
/// <para>
/// The suite is written once and run per file system: <see cref="InMemorySquadLinkSyncCrashWorkspace"/> in the unit
/// project and <see cref="PhysicalSquadLinkSyncCrashWorkspace"/> in the integration project. A symptom that fails today
/// is a known weakness, listed in <see cref="KnownWeaknesses"/> and reported with <see cref="Assert.Skip"/>.
/// </para>
/// </remarks>
public abstract class SquadInstanceLinkSyncCrashSafetyContractTests
{
    private const string ManagedMarker = "state/links.json";
    private const string PendingMarker = "state/links.pending.json";

    private static readonly SquadInstanceRow Row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);

    /// <summary>Creates the workspace one case runs in. The suite disposes it.</summary>
    /// <returns>A fresh workspace.</returns>
    protected abstract SquadLinkSyncCrashWorkspace CreateWorkspace();

    /// <summary>The symptoms that fail today, each an existing crash-safety weakness, by flow and symptom.</summary>
    protected virtual IReadOnlyList<SquadLinkSyncKnownWeakness> KnownWeaknesses => [];

    /// <summary>A first sync stopped at every operation recovers by running again.</summary>
    [Fact]
    public void FirstSyncStoppedAtEveryOperationCompletesWhenRunAgain() => Run(SquadLinkSyncFlow.FirstSync, changesTree: true);

    /// <summary>Removing a stale link stopped at every operation recovers by running again.</summary>
    [Fact]
    public void StaleLinkRemovalStoppedAtEveryOperationCompletesWhenRunAgain() => Run(SquadLinkSyncFlow.StaleLinkRemoved, changesTree: true);

    /// <summary>Re-linking an identical split file stopped at every operation recovers by running again.</summary>
    [Fact]
    public void IdenticalSplitRelinkStoppedAtEveryOperationCompletesWhenRunAgain() => Run(SquadLinkSyncFlow.IdenticalSplitRelinked, changesTree: true);

    /// <summary>A differing split file is never changed, at any stop.</summary>
    [Fact]
    public void DifferingSplitFileIsNeverChangedAtAnyStop() => Run(SquadLinkSyncFlow.DifferingSplitKept, changesTree: false);

    /// <summary>A person's own file where a link would go is never changed, at any stop.</summary>
    [Fact]
    public void UnmanagedCopyIsNeverChangedAtAnyStop() => Run(SquadLinkSyncFlow.UnmanagedCopyKept, changesTree: true);

    /// <summary>A recorded folder link replaced by a real folder of the person's files is never deleted, at any stop.</summary>
    [Fact]
    public void ReplacedFolderIsNeverDeletedAtAnyStop() => Run(SquadLinkSyncFlow.ReplacedFolderKept, changesTree: true);

    /// <summary>A rewritten linked file whose main home source is gone is never deleted, at any stop.</summary>
    [Fact]
    public void RewrittenFileWithoutSourceIsNeverDeletedAtAnyStop() => Run(SquadLinkSyncFlow.RewrittenFileWithoutSourceKept, changesTree: true);

    private static void Seed(SquadLinkSyncCrashWorkspace workspace, SquadLinkSyncFlow flow)
    {
        IWritableFileSystem disk = workspace.Disk;
        disk.CreateDirectory(workspace.InstanceRoot);
        disk.CreateDirectory(workspace.StateDirectory);
        disk.CreateDirectory(Path.Combine(workspace.MainHome, "logs"));
        disk.WriteAllText(Path.Combine(workspace.MainHome, "settings.json"), "{\"theme\":\"dark\"}");
        disk.WriteAllText(Path.Combine(workspace.MainHome, "history.sqlite"), "history");
        disk.WriteAllText(Path.Combine(workspace.MainHome, "logs", "session.log"), "log");

        switch (flow)
        {
            case SquadLinkSyncFlow.FirstSync:
                return;
            case SquadLinkSyncFlow.UnmanagedCopyKept:
                disk.WriteAllText(Path.Combine(workspace.InstanceRoot, "settings.json"), "{\"mine\":true}");
                return;
        }

        Execute(workspace);
        workspace.Restart();
        string instanceSettings = Path.Combine(workspace.InstanceRoot, "settings.json");
        switch (flow)
        {
            case SquadLinkSyncFlow.StaleLinkRemoved:
                disk.DeleteFile(Path.Combine(workspace.MainHome, "history.sqlite"));
                break;
            case SquadLinkSyncFlow.IdenticalSplitRelinked:
                disk.DeleteFile(instanceSettings);
                disk.WriteAllText(instanceSettings, "{\"theme\":\"dark\"}");
                break;
            case SquadLinkSyncFlow.DifferingSplitKept:
                disk.DeleteFile(instanceSettings);
                disk.WriteAllText(instanceSettings, "{\"theme\":\"light\"}");
                break;
            case SquadLinkSyncFlow.ReplacedFolderKept:
                disk.DeleteTreeNoFollow(Path.Combine(workspace.InstanceRoot, "logs"));
                disk.CreateDirectory(Path.Combine(workspace.InstanceRoot, "logs"));
                disk.WriteAllText(Path.Combine(workspace.InstanceRoot, "logs", "mine.txt"), "my notes");
                break;
            case SquadLinkSyncFlow.RewrittenFileWithoutSourceKept:
                string history = Path.Combine(workspace.InstanceRoot, "history.sqlite");
                disk.DeleteFile(history);
                disk.WriteAllText(history, "only copy");
                disk.DeleteFile(Path.Combine(workspace.MainHome, "history.sqlite"));
                break;
        }
    }

    private static void Execute(SquadLinkSyncCrashWorkspace workspace) =>
        workspace.Sync.Sync(
            workspace.InstanceRoot,
            workspace.MainHome,
            Row,
            FrozenSet<string>.Empty,
            workspace.Links,
            workspace.StateDirectory);

    private void Run(SquadLinkSyncFlow flow, bool changesTree)
    {
        IReadOnlyDictionary<string, SquadTreeEntry> before;
        IReadOnlyDictionary<string, SquadTreeEntry> after;
        int operations;
        using (SquadLinkSyncCrashWorkspace clean = CreateWorkspace())
        {
            Seed(clean, flow);
            before = clean.Capture();
            int start = clean.OperationCount;
            Execute(clean);
            operations = clean.OperationCount - start;
            after = clean.Capture();
        }

        if (changesTree && SquadTreeComparison.Difference(before, after) is null)
        {
            throw new InvalidOperationException(
                $"The clean {flow} sync left the tree unchanged, so an interrupted run could always pass as the old tree.");
        }

        if (operations == 0)
        {
            throw new InvalidOperationException($"The clean {flow} sync performed no file operation, so there is nothing to stop.");
        }

        List<(int Point, string Detail)> failures = [];
        for (int point = 1; point <= operations; point++)
        {
            using SquadLinkSyncCrashWorkspace workspace = CreateWorkspace();
            Seed(workspace, flow);
            IReadOnlyDictionary<string, SquadTreeEntry> baseline = workspace.Capture();
            workspace.KillAt(workspace.OperationCount + point);

            Exception? stop = Record.Exception(() => Execute(workspace));
            if (stop is null || !workspace.IsKill(stop))
            {
                failures.Add((point, stop is null
                    ? "the sync completed without reaching the stop"
                    : $"the sync reported the stop as {stop.GetType().Name}: {stop.Message}"));
                continue;
            }

            workspace.Restart();
            IReadOnlyDictionary<string, SquadTreeEntry> crashed = workspace.Capture();
            failures.AddRange(AfterStop(workspace, baseline, after, crashed).Select(detail => (point, detail)));

            Exception? rerun = Record.Exception(() => Execute(workspace));
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
        SquadLinkSyncCrashWorkspace workspace,
        IReadOnlyDictionary<string, SquadTreeEntry> baseline,
        IReadOnlyDictionary<string, SquadTreeEntry> final,
        IReadOnlyDictionary<string, SquadTreeEntry> crashed)
    {
        foreach ((string path, SquadTreeEntry entry) in baseline)
        {
            crashed.TryGetValue(path, out SquadTreeEntry? now);
            bool unchanged = now is not null && SquadTreeComparison.EntryDifference(path, entry, now) is null;
            if (unchanged || entry.Kind == SquadTreeEntryKind.Directory)
            {
                continue;
            }

            if (path.StartsWith("main/", StringComparison.Ordinal))
            {
                yield return $"the main home changed after the stop, and the sync must only read it: '{path}'";
            }
            else if (path == ManagedMarker)
            {
                if (now is null || (final.TryGetValue(path, out SquadTreeEntry? finalLinks) &&
                                    SquadTreeComparison.EntryDifference(path, finalLinks, now) is not null))
                {
                    yield return $"the recorded links are neither the old file nor the new one: {Describe(path, now)}";
                }
            }
            else if (entry.Kind == SquadTreeEntryKind.File && !HoldsBytesInMainHome(baseline, entry))
            {
                yield return $"a file the person owns changed or was lost and its bytes are not in the main home: '{path}'";
            }
            else if (entry.Kind == SquadTreeEntryKind.File && now is not null && now.Kind == SquadTreeEntryKind.File)
            {
                yield return $"a file the person owns changed: '{path}'";
            }
        }

        foreach ((string path, SquadTreeEntry entry) in crashed)
        {
            if (baseline.ContainsKey(path) || entry.Kind == SquadTreeEntryKind.Directory)
            {
                continue;
            }

            if (path == PendingMarker)
            {
                continue;
            }

            if (path.Contains(".tmp", StringComparison.Ordinal))
            {
                yield return $"a temporary file was left behind: '{path}'";
            }
            else if (!final.TryGetValue(path, out SquadTreeEntry? expected))
            {
                yield return $"the stop left '{path}', which the finished sync never writes";
            }
            else if (SquadTreeComparison.EntryDifference(path, expected, entry) is { } partial)
            {
                yield return $"the stop left a partly written path: {partial}";
            }
        }

        static string Describe(string path, SquadTreeEntry? entry) =>
            entry is null ? $"'{path}' is missing" : $"'{path}' is a {entry.Kind}";
    }

    private static bool HoldsBytesInMainHome(IReadOnlyDictionary<string, SquadTreeEntry> baseline, SquadTreeEntry entry) =>
        entry.Content is { } bytes &&
        baseline.Any(pair => pair.Key.StartsWith("main/", StringComparison.Ordinal) &&
                             pair.Value.Kind == SquadTreeEntryKind.File &&
                             pair.Value.Content is { } mainBytes &&
                             mainBytes.Span.SequenceEqual(bytes.Span));

    private void Report(SquadLinkSyncFlow flow, List<(int Point, string Detail)> failures, int operations)
    {
        IReadOnlyList<(int Point, string Detail)> unexplained =
        [
            .. failures.Where(failure => !KnownWeaknesses.Any(weakness =>
                weakness.Flow == flow && weakness.Points.Contains(failure.Point) &&
                failure.Detail.Contains(weakness.DetailContains, StringComparison.Ordinal)))
        ];
        IReadOnlyList<string> stale =
        [
            .. KnownWeaknesses
                .Where(weakness => weakness.Flow == flow)
                .SelectMany(weakness => weakness.Points
                    .Where(point => !failures.Any(failure => failure.Point == point &&
                                                             failure.Detail.Contains(weakness.DetailContains, StringComparison.Ordinal)))
                    .Select(point => $"'{weakness.DetailContains}' no longer fails at operation {point}"))
        ];
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

/// <summary>One crash-safety symptom of the instance link sync that is known and not yet fixed.</summary>
/// <param name="Flow">The sync that fails.</param>
/// <param name="DetailContains">The symptom this weakness records; another symptom stays unexplained.</param>
/// <param name="Points">The exact stop points (operation ordinals of the clean run) at which the symptom shows.</param>
/// <param name="Reason">One line saying what goes wrong and why.</param>
public sealed record SquadLinkSyncKnownWeakness(
    SquadLinkSyncFlow Flow,
    string DetailContains,
    IReadOnlyCollection<int> Points,
    string Reason);
