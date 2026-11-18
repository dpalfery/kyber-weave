using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The crash-safety catalogue must not repeat a point whose tree is identical to an earlier one: a
/// bulk notification loop produces several callbacks with nothing written between them, and counting
/// each as its own fault point overstates what the run proves.
/// </summary>
public sealed class PhysicalSquadCrashSafetyFixtureTests
{
    [Fact]
    public void EveryReportedFaultPointLeavesADistinctCrashState()
    {
        using PhysicalSquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Update);
        IReadOnlyDictionary<string, SquadTreeEntry> baseline = fixture.Capture();
        IReadOnlyList<string> faultPoints = fixture.ExecuteClean(SquadCrashFlow.Update);

        HashSet<string> states = new(StringComparer.Ordinal);
        List<string> repeated = [];
        foreach (string faultPoint in faultPoints)
        {
            fixture.Restore(baseline);
            Assert.Null(fixture.ExecuteUnderFault(SquadCrashFlow.Update, faultPoint));
            if (!states.Add(SquadTreeFingerprint.Of(fixture.Capture())))
            {
                repeated.Add(faultPoint);
            }
        }

        Assert.True(
            repeated.Count == 0,
            $"These fault points leave a tree an earlier point already left: {string.Join(", ", repeated)}");
    }

    [Fact]
    public void TheWorkspaceRefusesAScopeWhoseStateWouldEscapeTheKillSnapshot()
    {
        using PhysicalSquadCrashSafetyFixture fixture = new();

        // CreateBaseline runs the guard: it resolves the lock, receipt, journal and work directory
        // and refuses a scope whose state sits outside Root, which a kill snapshot could not restore.
        fixture.CreateBaseline(SquadCrashFlow.Update);

        Assert.Equal(KyberWeave.Core.Squad.Deployment.SquadDeploymentScope.Project, fixture.Scope);
        Assert.True(Directory.Exists(fixture.Root));
    }

    [Fact]
    public void RecoveryThatFailsPartwayIsRepeatable()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A read-only directory attribute does not stop a file being created on Windows, so the partial recovery cannot be injected there.");
            return;
        }

        using PhysicalSquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Update);
        IReadOnlyDictionary<string, SquadTreeEntry> oldTree = fixture.Capture();
        IReadOnlyList<string> faultPoints = fixture.ExecuteClean(SquadCrashFlow.Update);
        fixture.Restore(oldTree);
        Assert.Null(fixture.ExecuteUnderFault(SquadCrashFlow.Update, faultPoints[^1]));

        string blocked = Path.Combine(fixture.Root, ".codex", "agents");
        UnixFileMode original = File.GetUnixFileMode(blocked);
        File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<Exception>(fixture.Recover);
            Assert.NotNull(SquadTreeComparison.Difference(oldTree, fixture.Capture()));
        }
        finally
        {
            File.SetUnixFileMode(blocked, original);
        }

        // A restarted process retries: the recovery that died partway must finish the prior tree.
        fixture.Recover();

        Assert.Null(SquadTreeComparison.Difference(oldTree, fixture.Capture()));
    }

    [Fact]
    public void RestoringASnapshotReproducesItExactly()
    {
        using PhysicalSquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Update);
        IReadOnlyDictionary<string, SquadTreeEntry> baseline = fixture.Capture();

        File.Delete(Path.Combine(fixture.Root, "notes", "operator.md"));
        fixture.Restore(baseline);

        Assert.Equal(
            baseline.Keys.Order(StringComparer.Ordinal),
            fixture.Capture().Keys.Order(StringComparer.Ordinal));
    }
}
