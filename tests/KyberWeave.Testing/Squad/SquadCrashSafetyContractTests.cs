using Xunit;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// The crash-safety contract every Squad deployment flow must meet: stop the flow at any fault
/// point, recover, and end with either the whole old tree or the whole new one, with the files the
/// flow never owned unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The suite is written once and run per file system: the real disk today, the in-memory file system
/// once tasks 3.2 to 3.4 put the deployment classes behind that seam. A derived class supplies only
/// the workspace.
/// </para>
/// <para>
/// "Every fault point" means every distinct crash state the workspace can be stopped at, as the
/// fixture defines it: the real disk can stop only at the durable steps and checkpoints the
/// transaction reports, while the in-memory run will stop at every file-system operation.
/// </para>
/// <para>
/// A case that fails today is a known crash-safety weakness, recorded in
/// <see cref="KnownWeaknesses"/> and reported with <see cref="Assert.Skip"/>, so the run stays green
/// and the weakness stays visible instead of being forgotten.
/// </para>
/// </remarks>
public abstract class SquadCrashSafetyContractTests
{
    /// <summary>Creates the workspace the run uses.</summary>
    /// <returns>A fresh workspace. The suite disposes it.</returns>
    protected abstract SquadCrashSafetyFixture CreateFixture();

    /// <summary>
    /// The fault points that fail today, each an existing crash-safety weakness that task 3.4 fixes.
    /// </summary>
    protected virtual IReadOnlyList<SquadCrashKnownWeakness> KnownWeaknesses => [];

    /// <summary>Install interrupted at every fault point recovers to the old or the complete new tree.</summary>
    [Fact]
    public void InstallRecoveredFromAFaultAtEveryPointIsTheOldOrTheCompleteNewTree() =>
        RunFlow(SquadCrashFlow.Install);

    /// <summary>Update interrupted at every fault point recovers to the old or the complete new tree.</summary>
    [Fact]
    public void UpdateRecoveredFromAFaultAtEveryPointIsTheOldOrTheCompleteNewTree() =>
        RunFlow(SquadCrashFlow.Update);

    /// <summary>Uninstall interrupted at every fault point recovers to the old or the complete new tree.</summary>
    [Fact]
    public void UninstallRecoveredFromAFaultAtEveryPointIsTheOldOrTheCompleteNewTree() =>
        RunFlow(SquadCrashFlow.Uninstall);

    /// <summary>Install rolled back at every fault point leaves the whole old tree.</summary>
    [Fact]
    public void InstallRolledBackAtEveryPointLeavesTheWholeOldTree() =>
        RunRollback(SquadCrashFlow.Install);

    /// <summary>Update rolled back at every fault point leaves the whole old tree.</summary>
    [Fact]
    public void UpdateRolledBackAtEveryPointLeavesTheWholeOldTree() =>
        RunRollback(SquadCrashFlow.Update);

    /// <summary>Uninstall rolled back at every fault point leaves the whole old tree.</summary>
    [Fact]
    public void UninstallRolledBackAtEveryPointLeavesTheWholeOldTree() =>
        RunRollback(SquadCrashFlow.Uninstall);

    private static string Describe(SquadCrashCase crashCase, string detail) =>
        $"{crashCase.Flow} at {crashCase.FaultPoint}: {detail}";

    private void RunRollback(SquadCrashFlow flow)
    {
        using SquadCrashSafetyFixture fixture = CreateFixture();
        fixture.CreateBaseline(flow);

        IReadOnlyDictionary<string, SquadTreeEntry> oldTree = fixture.Capture();
        IReadOnlyList<string> faultPoints = fixture.ExecuteClean(flow);
        fixture.Restore(oldTree);

        List<(SquadCrashCase Case, string Detail)> failures = [];
        foreach (string faultPoint in faultPoints)
        {
            SquadCrashCase current = new(flow, faultPoint);
            fixture.Restore(oldTree);

            string? rollbackProblem = fixture.ExecuteToRollback(flow, faultPoint);
            if (rollbackProblem is not null)
            {
                failures.Add((current, rollbackProblem));
                continue;
            }

            IReadOnlyDictionary<string, SquadTreeEntry> rolledBack = fixture.Capture();
            string? difference = TreeDifference(oldTree, rolledBack);
            if (difference is not null)
            {
                failures.Add((current, $"rollback did not restore the whole old tree: {difference}"));
                continue;
            }

            failures.AddRange(MarkerProblems(fixture, flow, oldTree, rolledBack)
                .Select(problem => (current, problem)));
        }

        Report(flow, failures);
    }

    private void RunFlow(SquadCrashFlow flow)
    {
        using SquadCrashSafetyFixture fixture = CreateFixture();
        fixture.CreateBaseline(flow);

        IReadOnlyDictionary<string, SquadTreeEntry> oldTree = fixture.Capture();
        IReadOnlyList<string> faultPoints = fixture.ExecuteClean(flow);
        IReadOnlyDictionary<string, SquadTreeEntry> newTree = fixture.Capture();

        Assert.True(
            faultPoints.Count > 0,
            $"{flow} reported no fault point, so the run would prove nothing. The flow must report one per durable step it takes.");
        Assert.True(
            TreeDifference(oldTree, newTree) is not null,
            "The clean flow left the tree unchanged.");

        List<(SquadCrashCase Case, string Detail)> failures = [];
        foreach (string faultPoint in faultPoints)
        {
            SquadCrashCase current = new(flow, faultPoint);
            fixture.Restore(oldTree);

            string? executionProblem = fixture.ExecuteUnderFault(flow, faultPoint);
            if (executionProblem is not null)
            {
                failures.Add((current, executionProblem));
                continue;
            }

            Exception? recoveryProblem = Record.Exception(fixture.Recover);
            if (recoveryProblem is not null)
            {
                failures.Add((current, $"recovery threw {recoveryProblem.GetType().Name}: {recoveryProblem.Message}"));
                continue;
            }

            IReadOnlyDictionary<string, SquadTreeEntry> recovered = fixture.Capture();
            string? oldDifference = TreeDifference(oldTree, recovered);
            string? newDifference = TreeDifference(newTree, recovered);
            if (oldDifference is not null && newDifference is not null)
            {
                failures.Add((current, $"the tree is neither the whole old tree ({oldDifference}) nor the whole new tree ({newDifference})"));
                continue;
            }

            failures.AddRange(MarkerProblems(fixture, flow, oldTree, recovered)
                .Select(problem => (current, problem)));
        }

        Report(flow, failures);
    }

    private static IEnumerable<string> MarkerProblems(
        SquadCrashSafetyFixture fixture,
        SquadCrashFlow flow,
        IReadOnlyDictionary<string, SquadTreeEntry> oldTree,
        IReadOnlyDictionary<string, SquadTreeEntry> actualTree)
    {
        foreach (string marker in fixture.UntouchedPaths(flow))
        {
            if (!actualTree.TryGetValue(marker, out SquadTreeEntry? actual))
            {
                yield return $"a file the flow must not touch is missing: '{marker}'";
                continue;
            }

            string? problem = EntryDifference(marker, oldTree[marker], actual);
            if (problem is not null)
            {
                yield return $"a file the flow must not touch changed: {problem}";
            }
        }
    }

    private void Report(SquadCrashFlow flow, List<(SquadCrashCase Case, string Detail)> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        string list = string.Join("\n", failures.Select(failure => Describe(failure.Case, failure.Detail)));
        List<SquadCrashFailure> recorded = failures
            .Select(failure => new SquadCrashFailure(failure.Case.Flow, failure.Case.FaultPoint, failure.Detail))
            .ToList();
        IReadOnlyList<SquadCrashFailure> unexplained =
            SquadCrashFailureClassifier.Unexplained(recorded, KnownWeaknesses);
        Assert.True(
            unexplained.Count == 0,
            $"{flow} has {unexplained.Count} crash-safety failure(s) that no known weakness explains:\n{list}");

        IReadOnlyList<SquadCrashKnownWeakness> matched =
            SquadCrashFailureClassifier.Matched(recorded, KnownWeaknesses);
        Assert.Skip(
            "Known weakness, fixed by task 3.4: " +
            string.Join(
                " ",
                matched.Select(weakness =>
                    $"[{weakness.Flow} at {weakness.FaultPoint}] {weakness.Reason}")));
    }

    private static string? TreeDifference(
        IReadOnlyDictionary<string, SquadTreeEntry> expected,
        IReadOnlyDictionary<string, SquadTreeEntry> actual)
    {
        foreach (string path in expected.Keys.Where(path => !actual.ContainsKey(path)).Order(StringComparer.Ordinal))
        {
            return $"'{path}' is missing";
        }

        foreach (string path in actual.Keys.Where(path => !expected.ContainsKey(path)).Order(StringComparer.Ordinal))
        {
            return $"'{path}' is unexpected";
        }

        foreach ((string path, SquadTreeEntry expectedEntry) in expected.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string? difference = EntryDifference(path, expectedEntry, actual[path]);
            if (difference is not null)
            {
                return difference;
            }
        }

        return null;
    }

    private static string? EntryDifference(string path, SquadTreeEntry expected, SquadTreeEntry actual)
    {
        if (expected.Kind != actual.Kind)
        {
            return $"'{path}' is a {actual.Kind} where a {expected.Kind} was expected";
        }

        if (!string.Equals(expected.LinkTarget, actual.LinkTarget, StringComparison.Ordinal))
        {
            return $"'{path}' names '{actual.LinkTarget}' where it named '{expected.LinkTarget}'";
        }

        ReadOnlyMemory<byte>? expectedBytes = expected.Content;
        ReadOnlyMemory<byte>? actualBytes = actual.Content;
        if (expectedBytes.HasValue != actualBytes.HasValue)
        {
            return $"'{path}' has different content";
        }

        if (expectedBytes is { } left && actualBytes is { } right && !left.Span.SequenceEqual(right.Span))
        {
            return $"'{path}' changed from {Digest(left)} to {Digest(right)}";
        }

        return null;
    }

    private static string Digest(ReadOnlyMemory<byte> content) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(content.Span))[..12];

    private sealed record SquadCrashCase(SquadCrashFlow Flow, string FaultPoint);
}

