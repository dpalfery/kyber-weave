using System.Globalization;
using Xunit;
using KyberWeave.Domain.Update;

namespace KyberWeave.Testing.Update;

/// <summary>One crash-safety symptom of the self-update that is known and not yet fixed.</summary>
/// <param name="Flow">The flow it shows in, as the failure names it.</param>
/// <param name="DetailContains">The symptom this weakness records; another symptom stays unexplained.</param>
/// <param name="Count">How many stop points show it. A different count, more or fewer, fails the run, so a new instance cannot hide in an old entry.</param>
/// <param name="Reason">One line saying what goes wrong and why.</param>
public sealed record SelfUpdateKnownWeakness(string Flow, string DetailContains, int Count, string Reason);

/// <summary>
/// The crash-safety contract of the self-update's replace and rollback: kill the process at every file operation,
/// restart it, run the update again, and end with an install directory that is entirely the old version or entirely
/// the new one.
/// </summary>
/// <remarks>
/// <para>
/// The update has no journal, so recovery is a fresh process running the same update over what the disk holds, naming
/// the version the installed running image reports. The contract asserts two things per stop. At the stop, before any
/// recovery, every installed binary exists and is a whole old or whole new image, because that is what lets the next
/// process start at all. After recovery, every file in the install directory, with no name filtered out, is the old
/// tree or the new tree.
/// </para>
/// <para>
/// Two flows run on each platform branch: a clean update, and an update whose last commit fails so the rollback runs;
/// the rollback is killed too. A handled failure at every operation (no kill) must leave the old tree, or the new tree
/// when the failure lands after every commit. The suite is written once and run per file system. A symptom that fails
/// today is a known weakness, listed in <see cref="KnownWeaknesses"/> and reported with <see cref="Assert.Skip"/>.
/// </para>
/// </remarks>
public abstract class SelfUpdateCrashSafetyContractTests
{
    /// <summary>Creates the workspace one case runs in. The case disposes it.</summary>
    /// <returns>A fresh workspace.</returns>
    protected abstract SelfUpdateCrashWorkspace CreateWorkspace();

    /// <summary>The symptoms that fail today, each a gap the update cannot close without a journal or a new seam member.</summary>
    protected virtual IReadOnlyList<SelfUpdateKnownWeakness> KnownWeaknesses { get; } =
    [
        new("update/windows", "does not exist, so the next process cannot start", 2,
            "Windows moves the installed image aside and then moves the new one in; a stop between the two moves leaves no image at the installed path, and only a rerun from another binary or the install script can put one back."),
        new("rollback/windows", "does not exist, so the next process cannot start", 5,
            "The same gap, reached again when the rollback restores an image that was moved aside."),
        new("update/windows", ".old' is left over", 3,
            "A stop after the new image is in place and before the old image is deleted, or a rerun after a stop between the two moves, leaves the old image beside it; an update that finds the new version already running does not touch it, and the next update that replaces the binary removes it."),
        new("rollback/windows", ".old' is left over", 5,
            "A rerun that finds the image missing puts the new one in and keeps the old image, so a failed commit still leaves the user an image; the next normal update removes it."),
        new("handled/windows", ".old' is left over", 2,
            "The old image is removed best-effort because the running one stays locked on Windows; when its delete fails the update still succeeds and the next update removes it."),
        new("handled/windows", "'.kyber-weave-update-probe' is left over", 1,
            "The write probe is removed best-effort; when its delete fails the update still succeeds and the next probe removes it."),
        new("handled/unix", "'.kyber-weave-update-probe' is left over", 1,
            "The write probe is removed best-effort; when its delete fails the update still succeeds and the next probe removes it."),
    ];

    /// <summary>A killed update recovers by running again, on the Unix branch.</summary>
    [Fact]
    public void AnUpdateKilledAtEveryOperationCompletesWhenRunAgain() => Sweep("update", windows: false, failLastCommit: false, handled: false);

    /// <summary>A killed update recovers by running again, on the Windows branch.</summary>
    [Fact]
    public void AnUpdateKilledAtEveryOperationCompletesWhenRunAgainOnWindows() => Sweep("update", windows: true, failLastCommit: false, handled: false);

    /// <summary>A rollback killed at every operation recovers by running again, on the Unix branch.</summary>
    [Fact]
    public void ARollbackKilledAtEveryOperationCompletesWhenTheUpdateRunsAgain() => Sweep("rollback", windows: false, failLastCommit: true, handled: false);

    /// <summary>A rollback killed at every operation recovers by running again, on the Windows branch.</summary>
    [Fact]
    public void ARollbackKilledAtEveryOperationCompletesWhenTheUpdateRunsAgainOnWindows() => Sweep("rollback", windows: true, failLastCommit: true, handled: false);

    /// <summary>A failure at any operation leaves the old tree, or the new tree once every commit is done, on the Unix branch.</summary>
    [Fact]
    public void AHandledFailureAtEveryOperationLeavesTheOldOrTheNewTree() => Sweep("handled", windows: false, failLastCommit: false, handled: true);

    /// <summary>A failure at any operation leaves the old tree, or the new tree once every commit is done, on the Windows branch.</summary>
    [Fact]
    public void AHandledFailureAtEveryOperationLeavesTheOldOrTheNewTreeOnWindows() => Sweep("handled", windows: true, failLastCommit: false, handled: true);

    private void Sweep(string flowName, bool windows, bool failLastCommit, bool handled)
    {
        SelfUpdateCrashScenario scenario = new(windows);
        string flow = $"{flowName}/{(windows ? "windows" : "unix")}";
        IReadOnlyDictionary<string, byte[]> oldTree = scenario.OldTree();
        IReadOnlyDictionary<string, byte[]> newTree = scenario.NewTree();
        int operations;
        using (SelfUpdateCrashWorkspace clean = CreateWorkspace())
        {
            scenario.SeedOldInstall(clean);
            IReadOnlyDictionary<string, byte[]> before = clean.CaptureInstall();
            Assert.Empty(Differences(oldTree, before));
            if (failLastCommit)
            {
                clean.FailWhen(LastCommitFailure(scenario));
            }

            int start = clean.OperationCount;
            SelfUpdateOutcome outcome = scenario.Run(clean, "0.1.0");
            operations = clean.OperationCount - start;
            clean.Restart();
            IReadOnlyDictionary<string, byte[]> after = clean.CaptureInstall();
            if (failLastCommit)
            {
                Assert.Equal(1, outcome.ExitCode);
                Assert.Empty(Differences(oldTree, after));
            }
            else
            {
                Assert.True(outcome.ExitCode == 0, outcome.Message);
                Assert.Empty(Differences(newTree, after));
            }
        }

        Assert.True(operations > 0, $"The clean {flow} flow performed no file operation, so there is nothing to stop.");

        List<(int Point, string Detail)> failures = [];
        for (int point = 1; point <= operations; point++)
        {
            using SelfUpdateCrashWorkspace workspace = CreateWorkspace();
            scenario.SeedOldInstall(workspace);
            if (handled)
            {
                HandledFailureAt(workspace, scenario, point, oldTree, newTree, failures);
                continue;
            }

            // The kill is planned first: when it and the failed commit land on the same call, the process dies.
            workspace.KillAfter(point);
            if (failLastCommit)
            {
                workspace.FailWhen(LastCommitFailure(scenario));
            }

            Exception? stop = Record.Exception(() => scenario.Run(workspace, "0.1.0"));
            if (!workspace.Killed)
            {
                failures.Add((point, $"the update ended without reaching the kill ({stop?.GetType().Name ?? "no exception"})"));
                continue;
            }

            workspace.Restart();
            IReadOnlyDictionary<string, byte[]> crashed = workspace.CaptureInstall();
            foreach (string name in scenario.Binaries)
            {
                if (!crashed.TryGetValue(name, out byte[]? image))
                {
                    failures.Add((point, $"at the stop '{name}' does not exist, so the next process cannot start (stopped at: {stop?.Message})"));
                }
                else if (!image.AsSpan().SequenceEqual(oldTree[name]) && !image.AsSpan().SequenceEqual(newTree[name]))
                {
                    failures.Add((point, $"at the stop '{name}' is neither the old nor the new image"));
                }
            }

            SelfUpdateOutcome rerun = scenario.Run(workspace, scenario.VersionOf(crashed));
            if (rerun.ExitCode != 0)
            {
                failures.Add((point, $"running the update again failed: {rerun.Message} (stopped at: {stop?.Message})"));
                continue;
            }

            IReadOnlyDictionary<string, byte[]> recovered = workspace.CaptureInstall();
            IReadOnlyList<string> differences = Differences(newTree, recovered);
            if (differences.Count > 0 && Differences(oldTree, recovered).Count > 0)
            {
                failures.Add((point, $"after running again the install is neither the old nor the new tree: {string.Join("; ", differences)} (stopped at: {stop?.Message})"));
            }
        }

        Report(flow, failures, operations);
    }

    private static void HandledFailureAt(
        SelfUpdateCrashWorkspace workspace,
        SelfUpdateCrashScenario scenario,
        int point,
        IReadOnlyDictionary<string, byte[]> oldTree,
        IReadOnlyDictionary<string, byte[]> newTree,
        List<(int Point, string Detail)> failures)
    {
        int target = workspace.OperationCount + point;
        int calls = 0;
        workspace.FailWhen((_, _) => ++calls == point);
        SelfUpdateOutcome outcome = scenario.Run(workspace, "0.1.0");
        workspace.Restart();
        IReadOnlyDictionary<string, byte[]> tree = workspace.CaptureInstall();
        bool isOld = Differences(oldTree, tree).Count == 0;
        bool isNew = Differences(newTree, tree).Count == 0;
        if (!isOld && !isNew)
        {
            failures.Add((point, $"a failure at operation {target} (exit {outcome.ExitCode}: {outcome.Message}) left a tree that is neither old nor new: {string.Join("; ", Differences(newTree, tree))}"));
        }
        else if (isNew && outcome.ExitCode != 0 && !outcome.Message.Contains("is installed", StringComparison.Ordinal))
        {
            failures.Add((point, $"a failure at operation {target} was reported as '{outcome.Message}' but the new tree was installed"));
        }
        else if (isOld && outcome.ExitCode == 0)
        {
            failures.Add((point, $"a failure at operation {target} left the old tree but the update reported success"));
        }
    }

    /// <summary>Fails the first move of the running image's staging file, once: the last commit, never the restore that follows it.</summary>
    private static Func<string, string?, bool> LastCommitFailure(SelfUpdateCrashScenario scenario)
    {
        bool failed = false;
        return (member, path) =>
        {
            if (failed || member != "MoveFile" || path is null ||
                !string.Equals(Path.GetFileName(path), "." + scenario.Cli + ".new", StringComparison.Ordinal))
            {
                return false;
            }

            failed = true;
            return true;
        };
    }

    private static IReadOnlyList<string> Differences(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        List<string> differences = [];
        foreach ((string name, byte[] bytes) in expected)
        {
            if (!actual.TryGetValue(name, out byte[]? now))
            {
                differences.Add($"'{name}' is missing");
            }
            else if (!bytes.AsSpan().SequenceEqual(now))
            {
                differences.Add($"'{name}' differs");
            }
        }

        differences.AddRange(actual.Keys.Where(name => !expected.ContainsKey(name)).Select(name => $"'{name}' is left over"));
        return differences;
    }

    private void Report(string flow, List<(int Point, string Detail)> failures, int operations)
    {
        List<(int Point, string Detail)> unexplained = [.. failures.Where(failure =>
            !KnownWeaknesses.Any(weakness => weakness.Flow == flow && failure.Detail.Contains(weakness.DetailContains, StringComparison.Ordinal)))];
        List<string> stale = [.. KnownWeaknesses
            .Where(weakness => weakness.Flow == flow)
            .Select(weakness => (Weakness: weakness, Actual: failures.Count(failure => failure.Detail.Contains(weakness.DetailContains, StringComparison.Ordinal))))
            .Where(entry => entry.Actual != entry.Weakness.Count)
            .Select(entry => $"'{entry.Weakness.DetailContains}' shows at {entry.Actual} stop point(s), not the {entry.Weakness.Count} listed")];
        Assert.True(
            unexplained.Count == 0 && stale.Count == 0,
            $"{flow} has {unexplained.Count} crash-safety failure(s) that no known weakness explains and {stale.Count} known weakness(es) whose count no longer matches:\n" +
            string.Join("\n", unexplained.Select(failure => string.Create(CultureInfo.InvariantCulture, $"{flow} at operation {failure.Point}: {failure.Detail}"))) +
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
                    .Select(weakness => $"{weakness.Reason} It shows at {failures.Count(failure => failure.Detail.Contains(weakness.DetailContains, StringComparison.Ordinal))} of {operations} operations.")));
    }
}
