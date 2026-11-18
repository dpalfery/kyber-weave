using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A deployment recovers the unfinished transaction a killed run left, under its own lease and before it starts,
/// over the in-memory file system. Recovery that cannot finish refuses and leaves the tree and journal as found.
/// </summary>
public sealed class SquadTransactionAutoRecoveryTests
{
    private const string RecoveredMessage = "Recovered an unfinished Squad transaction";

    [Fact]
    public void AKilledInstallIsRecoveredByTheNextInstallWhichIsAskedToPlanAgainThenCompletes()
    {
        using Crashed crashed = Crashed.Start(SquadCrashFlow.Install, points => points[points.Count / 2]);

        SquadDeploymentConflictException refused = Assert.Throws<SquadDeploymentConflictException>(() =>
            crashed.Fixture.Transaction().Execute(crashed.Fixture.Plan(SquadCrashFlow.Install)));

        Assert.Contains(RecoveredMessage, refused.Message, StringComparison.Ordinal);
        Assert.Null(SquadTreeComparison.Difference(crashed.OldTree, crashed.Fixture.Capture()));

        crashed.Fixture.Transaction().Execute(crashed.Fixture.Plan(SquadCrashFlow.Install));

        Assert.Null(SquadTreeComparison.Difference(crashed.NewTree, crashed.Fixture.Capture()));
    }

    [Fact]
    public void AKilledUpdateIsRecoveredByTheNextUninstallWhichIsAskedToPlanAgainThenCompletes()
    {
        using Crashed crashed = Crashed.Start(SquadCrashFlow.Update, points => points[points.Count / 2]);
        IReadOnlyDictionary<string, SquadTreeEntry> uninstalled = UninstalledTree();

        _ = Assert.Throws<SquadDeploymentConflictException>(() =>
            crashed.Fixture.Transaction().Execute(crashed.Fixture.Plan(SquadCrashFlow.Uninstall)));
        Assert.Null(SquadTreeComparison.Difference(crashed.OldTree, crashed.Fixture.Capture()));

        crashed.Fixture.Transaction().Execute(crashed.Fixture.Plan(SquadCrashFlow.Uninstall));

        Assert.Null(SquadTreeComparison.Difference(uninstalled, crashed.Fixture.Capture()));
        Assert.False(crashed.JournalExists());
    }

    [Fact]
    public void ACommittedJournalIsFinishedForwardAndKeepsTheCommittedFiles()
    {
        using Crashed crashed = Crashed.Start(SquadCrashFlow.Install, points => points[^3]);
        Assert.True(crashed.JournalExists());

        _ = Assert.Throws<SquadDeploymentConflictException>(() =>
            crashed.Fixture.Transaction().Execute(crashed.Fixture.Plan(SquadCrashFlow.Install)));

        Assert.False(crashed.JournalExists());
        Assert.Null(SquadTreeComparison.Difference(crashed.NewTree, crashed.Fixture.Capture()));
    }

    [Fact]
    public void ACommittedJournalHoldingAFileItDoesNotOwnRefusesAndLeavesEveryEntryInPlace()
    {
        using Crashed crashed = Crashed.Start(SquadCrashFlow.Install, points => points[^3]);
        Assert.True(crashed.JournalExists());
        string journal = Path.GetDirectoryName(crashed.IntentPath)!;
        string operatorNotes = Path.Combine(journal, "operator-notes.md");
        crashed.Fixture.Files.WriteAllText(operatorNotes, "kept by the operator");
        IReadOnlyDictionary<string, SquadTreeEntry> crashedTree = crashed.Fixture.Capture();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            crashed.Fixture.Transaction().Execute(crashed.Fixture.Plan(SquadCrashFlow.Install)));

        Assert.Contains(journal, refused.Message, StringComparison.Ordinal);
        Assert.Contains("does not exactly describe its transaction tree", refused.Message, StringComparison.Ordinal);
        Assert.Equal("kept by the operator", crashed.Fixture.Files.ReadAllText(operatorNotes));
        Assert.Null(SquadTreeComparison.Difference(crashedTree, crashed.Fixture.Capture()));
    }

    [Fact]
    public void ACommittedJournalHoldingOnlyWhatTheTransactionOwnsIsFinishedForward()
    {
        using Crashed crashed = Crashed.Start(SquadCrashFlow.Install, points => points[^3]);
        string journal = Path.GetDirectoryName(crashed.IntentPath)!;
        Assert.True(crashed.Fixture.Files.DirectoryExists(journal));

        SquadTransaction transaction = crashed.Fixture.Transaction();
        transaction.Recover(crashed.Fixture.Root, SquadDeploymentScope.Project);

        Assert.False(crashed.JournalExists());
        Assert.Null(SquadTreeComparison.Difference(crashed.NewTree, crashed.Fixture.Capture()));
        Assert.Contains(transaction.Warnings, warning => warning.Contains("finished", StringComparison.Ordinal));
    }

    [Fact]
    public void AKillBeforeTheJournalExistsLeavesOnlyFoldersSoTheDeploymentContinuesAndSaysSo()
    {
        using Crashed crashed = Crashed.Start(SquadCrashFlow.Install, points => points[1]);
        Assert.False(crashed.Fixture.Files.FileExists(crashed.IntentPath));
        SquadTransaction transaction = crashed.Fixture.Transaction();

        transaction.Execute(crashed.Fixture.Plan(SquadCrashFlow.Install));

        Assert.Null(SquadTreeComparison.Difference(crashed.NewTree, crashed.Fixture.Capture()));
        Assert.DoesNotContain(transaction.Warnings, warning => warning.Contains("rolled back", StringComparison.Ordinal));
    }

    [Fact]
    public void ARecoveryThatFailsRefusesNamingTheCauseAndTheJournalAndChangesNothing()
    {
        using Crashed crashed = Crashed.Start(SquadCrashFlow.Install, points => points[points.Count / 2]);
        IReadOnlyDictionary<string, SquadTreeEntry> crashedTree = crashed.Fixture.Capture();
        SquadDeploymentPlan plan = crashed.Fixture.Plan(SquadCrashFlow.Install);
        crashed.Fixture.Files.FailWhen(
            InMemorySquadCrashSafetyFixture.IsMutating,
            new IOException("The disk refused the write."));

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            crashed.Fixture.Transaction().Execute(plan));
        crashed.Fixture.Files.ClearFaults();

        Assert.Contains("could not be recovered", refused.Message, StringComparison.Ordinal);
        Assert.Contains("The disk refused the write.", refused.Message, StringComparison.Ordinal);
        Assert.Contains(crashed.IntentPath, refused.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(refused.InnerException);
        Assert.Null(SquadTreeComparison.Difference(crashedTree, crashed.Fixture.Capture()));
        Assert.True(crashed.JournalExists());
    }

    [Fact]
    public void AJournalPhaseFromANewerBinaryRefusesAndIsNeverDiscarded()
    {
        using Crashed crashed = Crashed.Start(SquadCrashFlow.Install, points => points[points.Count / 2]);
        string journal = crashed.Fixture.Files.ReadAllText(crashed.IntentPath);
        Assert.Contains("\"phase\": \"prepared\"", journal, StringComparison.Ordinal);
        crashed.Fixture.Files.WriteAllText(
            crashed.IntentPath,
            journal.Replace("\"phase\": \"prepared\"", "\"phase\": \"verified\"", StringComparison.Ordinal));
        IReadOnlyDictionary<string, SquadTreeEntry> before = crashed.Fixture.Capture();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            crashed.Fixture.Transaction().Execute(crashed.Fixture.Plan(SquadCrashFlow.Install)));

        Assert.Contains("could not be recovered", refused.Message, StringComparison.Ordinal);
        Assert.Contains(crashed.IntentPath, refused.Message, StringComparison.Ordinal);
        Assert.NotNull(refused.InnerException);

        // A kill can leave an unpublished journal temporary; recovery discards it before it reads the journal, and
        // nothing else in the tree or the journal itself changes.
        Assert.Null(SquadTreeComparison.Difference(WithoutTemporaries(before), WithoutTemporaries(crashed.Fixture.Capture())));
    }

    [Fact]
    public void RecoverOnItsOwnStillRollsBackAndReportsWhatItDid()
    {
        using Crashed crashed = Crashed.Start(SquadCrashFlow.Install, points => points[points.Count / 2]);
        SquadTransaction transaction = crashed.Fixture.Transaction();

        transaction.Recover(crashed.Fixture.Root, SquadDeploymentScope.Project);

        Assert.Null(SquadTreeComparison.Difference(crashed.OldTree, crashed.Fixture.Capture()));
        Assert.Contains(transaction.Warnings, warning => warning.Contains("rolled back", StringComparison.Ordinal));
    }

    [Fact]
    public void ACleanTreeDeploysWithNoRecoveryNotice()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Install);
        SquadTransaction transaction = fixture.Transaction();

        transaction.Execute(fixture.Plan(SquadCrashFlow.Install));

        Assert.DoesNotContain(transaction.Warnings, warning => warning.Contains(RecoveredMessage, StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, SquadTreeEntry> WithoutTemporaries(
        IReadOnlyDictionary<string, SquadTreeEntry> tree) =>
        tree.Where(entry => !entry.Key.EndsWith(".tmp", StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, SquadTreeEntry> UninstalledTree()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Update);
        fixture.Transaction().Execute(fixture.Plan(SquadCrashFlow.Uninstall));
        return fixture.Capture();
    }

    private sealed class Crashed : IDisposable
    {
        private Crashed(
            InMemorySquadCrashSafetyFixture fixture,
            IReadOnlyDictionary<string, SquadTreeEntry> oldTree,
            IReadOnlyDictionary<string, SquadTreeEntry> newTree)
        {
            Fixture = fixture;
            OldTree = oldTree;
            NewTree = newTree;
        }

        public InMemorySquadCrashSafetyFixture Fixture { get; }

        public IReadOnlyDictionary<string, SquadTreeEntry> OldTree { get; }

        public IReadOnlyDictionary<string, SquadTreeEntry> NewTree { get; }

        public string IntentPath =>
            Path.Combine(Fixture.Root, ".kyber-weave", ".squad-transaction", "intent.json");

        public static Crashed Start(SquadCrashFlow flow, Func<IReadOnlyList<string>, string> pickFaultPoint)
        {
            InMemorySquadCrashSafetyFixture fixture = new();
            fixture.CreateBaseline(flow);
            IReadOnlyDictionary<string, SquadTreeEntry> oldTree = fixture.Capture();
            IReadOnlyList<string> points = fixture.ExecuteClean(flow);
            IReadOnlyDictionary<string, SquadTreeEntry> newTree = fixture.Capture();
            fixture.Restore(oldTree);

            Assert.Null(fixture.ExecuteUnderFault(flow, pickFaultPoint(points)));
            return new Crashed(fixture, oldTree, newTree);
        }

        public bool JournalExists() => Fixture.Files.FileExists(IntentPath);

        public void Dispose() => Fixture.Dispose();
    }
}
