using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using Xunit.Sdk;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Deployment-state contracts: restore intent and after-image payloads. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateRestoreTests : SquadDeploymentStateTestBase
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Theory]
    [InlineData("target-file")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("receipt-original")]
    public void RestoreIntentBackupChangedAfterObserverBoundaryIsNeverConsumed(
        string artifactRole)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        RestoreArtifactTamperingObserver observer = new(
            scenario.ArtifactPath,
            scenario.MutationBoundary);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(scenario.Plan));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("artifact", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(observer.MutatedArtifact);
        Assert.True(File.Exists(scenario.ArtifactPath));
        Assert.Equal(observer.CorruptBytes, File.ReadAllBytes(scenario.ArtifactPath));
        Assert.True(File.Exists(scenario.IntentPath));
        AssertTreeEntryEqual(
            scenario.ExpectedLiveAfterImage,
            CaptureTree(fixturePath)[scenario.LiveRelativePath]);

        SortedDictionary<string, TreeEntry> retainedEvidence = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = Transaction(fixturePath);
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
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        LifecycleStepFailingObserver observer = new(
            fixturePath,
            scenario.MutationBoundary);
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        File.WriteAllBytes(scenario.ArtifactPath, "corrupt restore evidence"u8.ToArray());
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = Transaction(fixturePath);

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
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        LifecycleStepFailingObserver observer = new(
            fixturePath,
            scenario.MutationBoundary);
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        Transaction(fixturePath).Recover(fixturePath, SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixturePath);
        Transaction(fixturePath).Recover(fixturePath, SquadDeploymentScope.Project);

        AssertTreesEqual(before, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixturePath));
    }

    [Fact]
    public void RestoreIntentRestoreSourcesAreReverifiedAtAdjacentUseBoundary()
    {
        string transactionSource = Corpus.ReadText("src/KyberWeave.Data/Squad/Deployment/SquadTransaction.cs");
        string restoreIntent = ExtractMethod(transactionSource, "RestoreIntent");
        string restoreEntry = ExtractMethod(transactionSource, "RestoreEntry");

        Assert.Contains("VerifyRestore", restoreIntent, StringComparison.Ordinal);
        Assert.Contains("VerifyRestore", restoreEntry, StringComparison.Ordinal);
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
        using TempDirectory caughtFixture = new();
        AfterImageRestoreScenario caughtScenario = CreateAfterImageRestoreScenario(caughtFixture.Path, artifactRole);
        SortedDictionary<string, TreeEntry> caughtBefore = CaptureTree(caughtFixture.Path);
        AfterImageCrashObserver caughtObserver = new(
            caughtFixture.Path,
            caughtScenario.Subject);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(caughtFixture.Path, caughtObserver).Execute(caughtScenario.Plan));

        AssertTreesEqual(caughtBefore, CaptureTree(caughtFixture.Path));

        using TempDirectory recoveryFixture = new();
        AfterImageRestoreScenario recoveryScenario =
            CreateAfterImageRestoreScenario(recoveryFixture.Path, artifactRole);
        SortedDictionary<string, TreeEntry> recoveryBefore = CaptureTree(recoveryFixture.Path);
        AfterImageCrashObserver recoveryObserver = new(
            recoveryFixture.Path,
            recoveryScenario.Subject);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(recoveryFixture.Path, recoveryObserver).Execute(recoveryScenario.Plan));
        RestoreTree(
            recoveryFixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                recoveryObserver.InterruptedTreeSnapshot));

        Transaction(recoveryFixture.Path).Recover(
            recoveryFixture.Path,
            SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(recoveryFixture.Path);
        Transaction(recoveryFixture.Path).Recover(
            recoveryFixture.Path,
            SquadDeploymentScope.Project);

        AssertTreesEqual(recoveryBefore, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(recoveryFixture.Path));
    }

    [Fact]
    public void RestoreIntentUsesSingleVerifiedImmutablePayload()
    {
        string source = Corpus.ReadText("src/KyberWeave.Data/Squad/Deployment/SquadTransaction.cs");
        string capture = ExtractMethod(source, "CaptureVerifiedRestorePayload");
        string compare = ExtractMethod(source, "CompareAndRestoreEntry");
        string restore = ExtractMethod(source, "RestoreEntry");

        Assert.Contains("ReadOnlyMemory<byte>", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(compare, "CaptureVerifiedRestorePayload("));
        Assert.DoesNotContain("File.ReadAll", compare, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAll", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifyRestoreArtifact", restore, StringComparison.Ordinal);
        Assert.Contains("payload", restore, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetEntryKind", capture, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(capture, "GetEntryKind") >= 2,
            "Restore payload capture must verify the source node kind before and after its single read.");
        Assert.Equal(1, CountOccurrences(capture, "File.ReadAllBytes(backupPath)"));
        Assert.Equal(1, CountOccurrences(capture, "File.ReadAllBytes(linkMetadataPath)"));
        Assert.Contains("FileMode.CreateNew", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create,", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create)", restore, StringComparison.Ordinal);
        Assert.Contains("CreateSymbolicLink", restore, StringComparison.Ordinal);
        Assert.Contains("GetEntryKind(targetPath)", restore, StringComparison.Ordinal);
        int liveRecheck = restore.LastIndexOf(
            "GetEntryKind(targetPath)",
            StringComparison.Ordinal);
        int liveRemoval = restore.IndexOf(
            "DeleteEntry(targetPath",
            liveRecheck,
            StringComparison.Ordinal);
        Assert.True(
            liveRecheck >= 0 && liveRemoval > liveRecheck,
            "The live after-image must be revalidated immediately before removal.");
        string adjacentRemoval = restore[liveRecheck..liveRemoval];
        Assert.DoesNotContain("File.ReadAll", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("await", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishIntent", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("await", restore, StringComparison.Ordinal);
    }
}
