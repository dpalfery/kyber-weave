using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// Deployment-state contracts: restore intent and after-image payloads. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateRestoreTests : InMemoryDeploymentStateTestBase
{
    [Theory]
    [InlineData("target-file")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("receipt-original")]
    public void RestoreIntentBackupChangedAfterObserverBoundaryIsNeverConsumed(
        string artifactRole)
    {
        string fixturePath = Root;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        RestoreArtifactTamperingObserver observer = new(
            Files,
            scenario.ArtifactPath,
            scenario.MutationBoundary);
        SquadTransaction transaction = TransactionAt(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(scenario.Plan));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("artifact", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(observer.MutatedArtifact);
        Assert.True(Files.FileExists(scenario.ArtifactPath));
        Assert.Equal(observer.CorruptBytes, Files.ReadAllBytes(scenario.ArtifactPath));
        Assert.True(Files.FileExists(scenario.IntentPath));
        AssertTreeEntryEqual(
            scenario.ExpectedLiveAfterImage,
            CaptureTree(fixturePath)[scenario.LiveRelativePath]);

        SortedDictionary<string, TreeEntry> retainedEvidence = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = TransactionAt(fixturePath);
        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Assert.IsType<InvalidDataException>(firstRecovery);
        AssertTreesEqual(retainedEvidence, CaptureTree(fixturePath));
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(retainedEvidence, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("receipt-original")]
    public void RecoverRestoreArtifactChangedBeforeRehydratedConsumptionIsNonAuthoritative(
        string artifactRole)
    {
        string fixturePath = Root;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        LifecycleStepFailingObserver observer = new(
            Files,
            fixturePath,
            scenario.MutationBoundary);
        SquadTransaction transaction = TransactionAt(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        Files.WriteAllBytes(scenario.ArtifactPath, "corrupt restore evidence"u8.ToArray());
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = TransactionAt(fixturePath);

        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsType<InvalidDataException>(firstRecovery);
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
        Assert.False(TreesEqual(before, CaptureTree(fixturePath)));
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("receipt-original")]
    public void RecoverUnchangedRestoreArtifactRestoresExactly(string artifactRole)
    {
        string fixturePath = Root;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        LifecycleStepFailingObserver observer = new(
            Files,
            fixturePath,
            scenario.MutationBoundary);
        SquadTransaction transaction = TransactionAt(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        TransactionAt(fixturePath).Recover(fixturePath, SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixturePath);
        TransactionAt(fixturePath).Recover(fixturePath, SquadDeploymentScope.Project);

        AssertTreesEqual(before, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData("target-backup")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("lock-link-metadata")]
    [InlineData("receipt-original")]
    [InlineData("receipt-link-metadata")]
    public void RestoreIntentAfterImagePublishedFailureRestoresFilesAndLinksFromVerifiedPayload(
        string artifactRole)
    {
        DeploymentScope caughtFixture = NewScope();
        AfterImageRestoreScenario caughtScenario = CreateAfterImageRestoreScenario(caughtFixture.Path, artifactRole);
        SortedDictionary<string, TreeEntry> caughtBefore = CaptureTree(caughtFixture.Path);
        AfterImageCrashObserver caughtObserver = new(
            Files,
            caughtFixture.Path,
            caughtScenario.Subject);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            TransactionAt(caughtFixture.Path, caughtObserver).Execute(caughtScenario.Plan));

        AssertTreesEqual(caughtBefore, CaptureTree(caughtFixture.Path));

        DeploymentScope recoveryFixture = NewScope();
        AfterImageRestoreScenario recoveryScenario =
            CreateAfterImageRestoreScenario(recoveryFixture.Path, artifactRole);
        SortedDictionary<string, TreeEntry> recoveryBefore = CaptureTree(recoveryFixture.Path);
        AfterImageCrashObserver recoveryObserver = new(
            Files,
            recoveryFixture.Path,
            recoveryScenario.Subject);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            TransactionAt(recoveryFixture.Path, recoveryObserver).Execute(recoveryScenario.Plan));
        RestoreTree(
            recoveryFixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                recoveryObserver.InterruptedTreeSnapshot));

        TransactionAt(recoveryFixture.Path).Recover(
            recoveryFixture.Path,
            SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(recoveryFixture.Path);
        TransactionAt(recoveryFixture.Path).Recover(
            recoveryFixture.Path,
            SquadDeploymentScope.Project);

        AssertTreesEqual(recoveryBefore, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(recoveryFixture.Path));
    }

    private static bool TreesEqual(
        IReadOnlyDictionary<string, TreeEntry> expected,
        IReadOnlyDictionary<string, TreeEntry> actual) =>
        expected.Count == actual.Count &&
        expected.All(pair =>
            actual.TryGetValue(pair.Key, out TreeEntry? entry) &&
            entry.Kind == pair.Value.Kind &&
            string.Equals(entry.LinkTarget, pair.Value.LinkTarget, StringComparison.Ordinal) &&
            (pair.Value.Content is null
                ? entry.Content is null
                : entry.Content is not null && entry.Content.SequenceEqual(pair.Value.Content)));
}
