using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// Deployment-state contracts: claim and authority recovery. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateRecoveryTests : InMemoryDeploymentStateTestBase
{
    [Fact]
    public async Task UninstallRecoversAnInterruptedUpdateFirstThenUninstallsInTheSameRun()
    {
        TransactionFixture fixture = TransactionFixture.Create(this);
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfter(
            fixture,
            SquadTransactionStepKind.FileBackedUp);
        RestoreTree(fixture.Path, interrupted);
        string journal = Path.Combine(fixture.Path, ".kyber-weave", ".squad-transaction", "intent.json");
        Assert.True(Files.FileExists(journal));
        SquadLifecycleService lifecycle = LifecycleAt(fixture.Path);

        SquadLifecycleResult result = await lifecycle.UninstallAsync(
            new SquadUninstallRequest(fixture.Path, SquadDeploymentScope.Project),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(Files.FileExists(journal));
        Assert.False(Files.FileExists(ToPlatformPath(fixture.Path, fixture.RelativePath)));
        Assert.Contains(
            result.Warnings!,
            warning => warning.StartsWith("Recovered an unfinished Squad transaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UninstallRefusesWhenTheInterruptedTransactionCannotBeRecoveredAndChangesNothing()
    {
        TransactionFixture fixture = TransactionFixture.Create(this);
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfter(
            fixture,
            SquadTransactionStepKind.FileBackedUp);
        RestoreTree(fixture.Path, interrupted);
        string journal = Path.Combine(fixture.Path, ".kyber-weave", ".squad-transaction", "intent.json");
        string document = Files.ReadAllText(journal);
        Files.WriteAllText(journal, CorruptJournalDocument(document, "unknown"));
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
        SquadLifecycleService lifecycle = LifecycleAt(fixture.Path);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.UninstallAsync(
                new SquadUninstallRequest(fixture.Path, SquadDeploymentScope.Project),
                TestContext.Current.CancellationToken));

        Assert.Contains("could not be recovered", refused.Message, StringComparison.Ordinal);
        Assert.Contains(".squad-transaction", refused.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidDataException>(refused.InnerException);
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("null")]
    public void RecoverInvalidPreparedJournalIsNonAuthoritative(string corruption)
    {
        TransactionFixture fixture = TransactionFixture.Create(this);
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
        string journal = Files.ReadAllText(intentPath);
        Files.WriteAllText(
            intentPath,
            CorruptJournalDocument(journal, corruption));
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = TransactionAt(fixturePath);

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
        DeploymentScope fixture = NewScope();
        string fixturePath = fixture.Path;
        const string relativePath = ".codex/agents/conductor.toml";
        SquadDeploymentPlan plan;
        if (operation == "missing-write")
        {
            plan = Planner.CreateInstall(
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
                ? Planner.CreateUpdate(
                    fixturePath,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    [Rendered(relativePath, "updated")],
                    receipt,
                    [],
                    false,
                    new FixedTimeProvider(InstalledAt.AddDays(1)))
                : Planner.CreateUninstall(
                    fixturePath,
                    SquadDeploymentScope.Project,
                    receipt);
        }

        LeafRaceObserver observer = new(Files, fixturePath, relativePath, racedNodeKind);
        SquadTransaction transaction = TransactionAt(fixturePath, observer);

        Exception? exception = Record.Exception(() => transaction.Execute(plan));

        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains(Path.GetFileName(relativePath), conflict.Message, StringComparison.Ordinal);
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.RacedEntry),
            CaptureTree(fixturePath)[relativePath]);
        Assert.True(Files.FileExists(observer.ExternalCanaryPath));
        Assert.True(Files.DirectoryExists(Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction")));
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
        TransactionFixture fixture = TransactionFixture.Create(this);
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
        SquadTransaction transaction = TransactionAt(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Fact]
    public void RecoverToleratesAnEmptyUndescribedFolderInTheTransactionTree()
    {
        TransactionFixture fixture = TransactionFixture.Create(this);
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfterCheckpoint(
            fixture,
            SquadTransactionCheckpointKind.Prepared);
        RestoreTree(fixturePath, interrupted);
        string transactionDirectory = Path.Combine(fixturePath, ".kyber-weave", ".squad-transaction");

        // What a kill between a stage move and the removal of its now-empty folders leaves.
        Files.CreateDirectory(Path.Combine(transactionDirectory, "staging", "emptied-by-a-kill"));

        Exception? exception = Record.Exception(() => TransactionAt(fixturePath).Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.Null(exception);
        Assert.False(Files.DirectoryExists(transactionDirectory));
    }

    [Fact]
    public void RecoverValidCreatedDirectoryAuthorityRemovesOnlyDeclaredEmptyDirectoriesDeepestFirst()
    {
        DeploymentScope fixture = NewScope();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Files.CreateDirectory(targetRoot);
        Write(fixture.Path, "beside-target/canary.txt", "sibling canary");
        Write(applicationData, "shared/canary.txt", "application canary");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
        SquadStateStore store = StoreAt(applicationData);
        CheckpointFailingObserver observer = new(
            Files,
            fixture.Path,
            SquadTransactionCheckpointKind.Prepared);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            TransactionAt(applicationData, observer).Execute(CreateGlobalInstallPlan(targetRoot)));
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

        TransactionAt(applicationData).Recover(targetRoot, SquadDeploymentScope.Global);

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
        DeploymentScope fixture = NewScope();
        ActiveTransitionScenario recoveryScenario = CreateActiveTransitionScenario(
            fixture.Path,
            scenario);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
        ActiveTransitionCrashObserver observer = new(
            Files,
            fixture.Path,
            checkpoint,
            recoveryScenario.Subject);
        SquadTransaction transaction = TransactionAt(fixture.Path, observer);

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

    /// <summary>Builds a lifecycle service over the in-memory state anchored on one project root.</summary>
    /// <param name="root">The project root, which is also its application-data directory.</param>
    /// <returns>The lifecycle service.</returns>
    private SquadLifecycleService LifecycleAt(string root)
    {
        InMemorySquadState fixtureState = new(stateRoot: root, existingFiles: Files);
        using FakeSquadReleaseSource releaseSource = FakeSquadReleaseSource.Failing(
            new NotImplementedException("A recovery test never downloads a release."));
        return SquadTestCommands.InMemoryLifecycle(
            fixtureState,
            releaseSource,
            FakeSquadRenderer.Empty());
    }
}
