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

namespace KyberWeave.Tests;

/// <summary>
/// Deployment-state contracts: claim and authority recovery. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateRecoveryTests : SquadDeploymentStateTestBase
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("null")]
    public void RecoverInvalidPreparedJournalIsNonAuthoritative(string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfter(
            fixture,
            SquadTransactionStepKind.FileBackedUp);
        RestoreTree(fixturePath, interrupted);
        string intentPath = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");
        string journal = File.ReadAllText(intentPath, Encoding.UTF8);
        File.WriteAllText(
            intentPath,
            CorruptJournalDocument(journal, corruption),
            new UTF8Encoding(false));
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData("missing-write", "file")]
    [InlineData("missing-write", "directory")]
    [InlineData("missing-write", "link")]
    [InlineData("existing-write", "file")]
    [InlineData("existing-write", "directory")]
    [InlineData("existing-write", "link")]
    [InlineData("existing-delete", "file")]
    [InlineData("existing-delete", "directory")]
    [InlineData("existing-delete", "link")]
    public void LeafClaimChangedPreconditionIsNeverOverwrittenOrDeleted(
        string operation,
        string racedNodeKind)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        const string relativePath = ".codex/agents/conductor.toml";
        SquadDeploymentPlan plan;
        if (operation == "missing-write")
        {
            plan = SquadDeploymentPlanner.CreateInstall(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(relativePath, "generated")],
                [],
                false,
                new FixedTimeProvider(InstalledAt));
        }
        else
        {
            Write(fixturePath, relativePath, "installed");
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                relativePath,
                Digest("installed"),
                "codex",
                false));
            WriteState(fixturePath, Lock(), receipt);
            plan = operation == "existing-write"
                ? SquadDeploymentPlanner.CreateUpdate(
                    fixturePath,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    [Rendered(relativePath, "updated")],
                    receipt,
                    [],
                    false,
                    new FixedTimeProvider(InstalledAt.AddDays(1)))
                : SquadDeploymentPlanner.CreateUninstall(
                    fixturePath,
                    SquadDeploymentScope.Project,
                    receipt);
        }

        LeafRaceObserver observer = new(fixturePath, relativePath, racedNodeKind);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() => transaction.Execute(plan));

        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains(Path.GetFileName(relativePath), conflict.Message, StringComparison.Ordinal);
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.RacedEntry),
            CaptureTree(fixturePath)[relativePath]);
        Assert.True(File.Exists(observer.ExternalCanaryPath));
        Assert.True(Directory.Exists(Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction")));
    }

    [Fact]
    public void ExecuteClaimPublicationUsesNoOverwrite()
    {
        string sourcePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Data",
            "Squad",
            "Deployment",
            "SquadTransaction.cs");
        string source = File.ReadAllText(sourcePath, Encoding.UTF8);
        int applyStart = source.IndexOf("private void ApplyFiles", StringComparison.Ordinal);
        int restoreStart =
            source.IndexOf("private static IReadOnlyList<string> RestoreIntent", StringComparison.Ordinal);
        Assert.True(applyStart >= 0 && restoreStart > applyStart);
        string liveApplySource = source[applyStart..restoreStart];

        Assert.Contains("ClaimExistingNoOverwrite", liveApplySource, StringComparison.Ordinal);
        Assert.Contains("PublishStageNoOverwrite", liveApplySource, StringComparison.Ordinal);
        Assert.Contains("VerifyAfterImage", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("overwrite: true", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteEntry(targetPath", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify(", ExtractMethod(source, "ClaimExistingNoOverwrite"), StringComparison.Ordinal);
        Assert.DoesNotContain("Notify(", ExtractMethod(source, "PublishStageNoOverwrite"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-required")]
    [InlineData("undeclared-file")]
    [InlineData("undeclared-link")]
    [InlineData("undeclared-directory")]
    [InlineData("duplicate-identity")]
    [InlineData("swapped-area")]
    [InlineData("swapped-role")]
    [InlineData("swapped-lifecycle")]
    [InlineData("invalid-transition")]
    [InlineData("well-digested-unexpected")]
    public void RecoverPreparedArtifactAuthorityMustExactlyDescribeTransactionTree(
        string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfterCheckpoint(
            fixture,
            SquadTransactionCheckpointKind.Prepared);
        RestoreTree(fixturePath, interrupted);
        string transactionDirectory = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction");
        string intentPath = Path.Combine(transactionDirectory, "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
        Assert.NotEmpty(artifacts);
        AssertPreparedArtifactAuthority(artifacts);
        CorruptPreparedAuthority(intent, transactionDirectory, corruption);
        WriteJsonObject(intentPath, intent);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Fact]
    public void RecoverValidCreatedDirectoryAuthorityRemovesOnlyDeclaredEmptyDirectoriesDeepestFirst()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(targetRoot);
        Write(fixture.Path, "beside-target/canary.txt", "sibling canary");
        Write(applicationData, "shared/canary.txt", "application canary");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
        SquadStateStore store = Store(applicationData);
        CheckpointFailingObserver observer = new(
            fixture.Path,
            SquadTransactionCheckpointKind.Prepared);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            new SquadTransaction(store, observer).Execute(CreateGlobalInstallPlan(targetRoot)));
        RestoreTree(
            fixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        string intentPath = Path.Combine(
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global),
            "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        Assert.False(intent.ContainsKey("missingJournalDirectoryCount"));
        JsonArray createdDirectories = Assert.IsType<JsonArray>(intent["createdDirectories"]);
        AssertCreatedDirectoryAuthorityIsCanonical(createdDirectories);

        new SquadTransaction(store).Recover(targetRoot, SquadDeploymentScope.Global);

        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("file-write", "ActiveTransitionWritten")]
    [InlineData("file-write", "OriginalClaimed")]
    [InlineData("file-write", "AfterImagePublished")]
    [InlineData("link-write", "ActiveTransitionWritten")]
    [InlineData("link-write", "OriginalClaimed")]
    [InlineData("link-write", "AfterImagePublished")]
    [InlineData("file-delete", "ActiveTransitionWritten")]
    [InlineData("file-delete", "OriginalClaimed")]
    [InlineData("file-delete", "AfterImagePublished")]
    [InlineData("link-delete", "ActiveTransitionWritten")]
    [InlineData("link-delete", "OriginalClaimed")]
    [InlineData("link-delete", "AfterImagePublished")]
    [InlineData("lock-write", "ActiveTransitionWritten")]
    [InlineData("lock-write", "OriginalClaimed")]
    [InlineData("lock-write", "AfterImagePublished")]
    [InlineData("lock-delete", "ActiveTransitionWritten")]
    [InlineData("lock-delete", "OriginalClaimed")]
    [InlineData("lock-delete", "AfterImagePublished")]
    [InlineData("receipt-write", "ActiveTransitionWritten")]
    [InlineData("receipt-write", "OriginalClaimed")]
    [InlineData("receipt-write", "AfterImagePublished")]
    [InlineData("receipt-delete", "ActiveTransitionWritten")]
    [InlineData("receipt-delete", "OriginalClaimed")]
    [InlineData("receipt-delete", "AfterImagePublished")]
    public void RecoverActiveClaimTransitionRestoresOriginalWithoutConflict(
        string scenario,
        string crashPoint)
    {
        Assert.True(
            Enum.TryParse(crashPoint, out SquadTransactionCheckpointKind checkpoint),
            $"K4q requires the opt-in '{crashPoint}' crash boundary.");
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        ActiveTransitionScenario recoveryScenario = CreateActiveTransitionScenario(
            fixture.Path,
            scenario);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
        ActiveTransitionCrashObserver observer = new(
            fixture.Path,
            checkpoint,
            recoveryScenario.Subject);
        SquadTransaction transaction = new(store, observer);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(recoveryScenario.Plan));
        RestoreTree(
            fixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        transaction.Recover(fixture.Path, SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixture.Path);
        transaction.Recover(fixture.Path, SquadDeploymentScope.Project);

        AssertTreesEqual(before, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixture.Path));
    }
}
