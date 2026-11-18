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
/// Deployment-state contracts: journal, staging, checkpoints and the lease. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateTransactionTests : SquadDeploymentStateTestBase
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string UpdatedAgentOnePath = ".codex/agents/one.toml";
    private const string UpdatedAgentTwoPath = ".codex/agents/two.toml";
    private const string NewWarpRolePath = ".warp/roles/three.md";
    private const string AdoptedAgentPath = ".codex/agents/adopted.md";

    [Fact]
    public void ExecuteGlobalScopeKeepsIntentStagesAndBackupsInHomeJournalNotBesideTargetOrInState()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(Path.Combine(applicationData, "home"));
        string targetRoot = Path.Combine(fixture.Path, "global-target");
        Directory.CreateDirectory(targetRoot);
        Write(targetRoot, ".codex/agents/conductor.toml", "installed body");
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("installed body"),
                    "codex",
                    false)
            ]);
        SquadDeploymentPlan plan = SquadDeploymentPlanner.CreateUpdate(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated body")],
            receipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        SquadStateStore store = Store(applicationData);
        GlobalTransactionLocationObserver observer = new(
            targetRoot,
            store.ResolveStateDirectory(targetRoot, SquadDeploymentScope.Global),
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global));

        new SquadTransaction(store, observer).Execute(plan);

        Assert.True(observer.SawDurableIntentInJournal);
        Assert.True(observer.SawStagingInJournal);
        Assert.True(observer.SawBackupInJournal);
        Assert.False(observer.SawStagingOrBackupOnTargetRoot);
        Assert.False(observer.SawStagingOrBackupInState);
        Assert.Equal("updated body", Read(targetRoot, ".codex/agents/conductor.toml"));
    }

    [Fact]
    public async Task ExecuteActiveTransactionHoldsAtomicExclusiveLease()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadTransaction transaction = Transaction(fixture.Path, blocker);
        SquadDeploymentPlan plan = fixture.CreateUpdatePlan();
        Task execution = BlockedTransaction.Start(() => transaction.Execute(plan));
        BlockedTransaction.WaitForStep(
            blocker.Reached,
            execution,
            "The transaction did not reach its durable intent checkpoint.");

        bool heldExclusiveLease;
        try
        {
            // The lease lives in the project journal beside the root, not in the home-anchored
            // state directory, so that is where an exclusive hold must be observable.
            string leaseDirectory = Store(fixture.Path).ResolveTransactionDirectory(
                fixture.Path,
                SquadDeploymentScope.Project);
            heldExclusiveLease = Directory.EnumerateFiles(
                    leaseDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Any(IsHeldWithExclusiveFileShare);
        }
        finally
        {
            blocker.Release.Set();
        }

        await execution;
        Assert.True(
            heldExclusiveLease,
            "An active transaction must own an atomically acquired exclusive lease, not rely on a check-then-create directory race.");
    }

    [Fact]
    public async Task RecoverWhileExecuteOwnsLeaseRefusesToInterleaveWithActiveTransaction()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadTransaction activeTransaction = Transaction(fixturePath, blocker);
        SquadDeploymentPlan plan = fixture.CreateUpdatePlan();
        Task execution = BlockedTransaction.Start(() => activeTransaction.Execute(plan));
        BlockedTransaction.WaitForStep(
            blocker.Reached,
            execution,
            "The transaction did not reach its durable intent checkpoint.");

        Exception? recoveryException;
        try
        {
            SquadTransaction recoveryTransaction = Transaction(fixturePath);
            recoveryException = Record.Exception(() =>
                recoveryTransaction.Recover(
                    fixturePath,
                    SquadDeploymentScope.Project));
        }
        finally
        {
            blocker.Release.Set();
        }

        await execution;
        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(recoveryException);
        Assert.Contains("active", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("updated body", Read(fixturePath, fixture.RelativePath));
    }

    [Fact]
    public void ExecuteStagesBacksUpAndJournalsBeforeApplyingStateLast()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        RecordingObserver observer = new(fixture.Path);
        SquadTransaction transaction = Transaction(fixture.Path, observer);

        transaction.Execute(fixture.CreateUpdatePlan());

        Assert.Equal(
            [
                SquadTransactionStepKind.IntentWritten,
                SquadTransactionStepKind.FileStaged,
                SquadTransactionStepKind.FileBackedUp,
                SquadTransactionStepKind.FileApplied,
                SquadTransactionStepKind.LockApplied,
                SquadTransactionStepKind.ReceiptApplied
            ],
            observer.Steps.Select(step => step.Kind));
        Assert.True(observer.SawIntentJournal);
        Assert.True(observer.SawStagedFile);
        Assert.True(observer.SawBackupFile);
        Assert.Equal("updated body", Read(fixture.Path, fixture.RelativePath));
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Path,
            ".kyber-weave",
            ".squad-transaction")));
    }

    [Fact]
    public void ExecutePlainObserverReceivesOnlyLegacyLifecycleSteps()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        RecordingObserver observer = new(fixture.Path);

        Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan());

        SquadTransactionStepKind[] expected =
        [
            SquadTransactionStepKind.IntentWritten,
            SquadTransactionStepKind.FileStaged,
            SquadTransactionStepKind.FileBackedUp,
            SquadTransactionStepKind.FileApplied,
            SquadTransactionStepKind.LockApplied,
            SquadTransactionStepKind.ReceiptApplied
        ];
        Assert.Equal(expected, Enum.GetValues<SquadTransactionStepKind>());
        Assert.Equal(expected, observer.Steps.Select(step => step.Kind));
        Assert.Equal(
            Enumerable.Range(1, expected.Length),
            observer.Steps.Select(step => step.Sequence));
    }

    [Fact]
    public void ExecuteCheckpointObserverOptsIntoPreparedAndTransitionEvents()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        CheckpointRecordingObserver observer = new();

        Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan());

        Assert.Equal(
            [
                SquadTransactionStepKind.IntentWritten,
                SquadTransactionStepKind.FileStaged,
                SquadTransactionStepKind.FileBackedUp,
                SquadTransactionStepKind.FileApplied,
                SquadTransactionStepKind.LockApplied,
                SquadTransactionStepKind.ReceiptApplied
            ],
            observer.Steps.Select(step => step.Kind));
        Assert.Equal(
            Enumerable.Range(1, 6),
            observer.Steps.Select(step => step.Sequence));
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.Prepared,
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            Enum.GetValues<SquadTransactionCheckpointKind>());
        Assert.Equal(
            Enumerable.Range(1, observer.Checkpoints.Count),
            observer.Checkpoints.Select(checkpoint => checkpoint.Sequence));
        Assert.Equal(
            new SquadTransactionCheckpoint(
                1,
                SquadTransactionCheckpointKind.Prepared),
            observer.Checkpoints[0]);
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            observer.Checkpoints
                .Where(checkpoint => string.Equals(
                    checkpoint.RelativePath,
                    fixture.RelativePath,
                    StringComparison.Ordinal))
                .Select(checkpoint => checkpoint.Kind));
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            observer.Checkpoints
                .Where(checkpoint => string.Equals(
                    checkpoint.RelativePath,
                    "lock",
                    StringComparison.Ordinal))
                .Select(checkpoint => checkpoint.Kind));
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            observer.Checkpoints
                .Where(checkpoint => string.Equals(
                    checkpoint.RelativePath,
                    "receipt",
                    StringComparison.Ordinal))
                .Select(checkpoint => checkpoint.Kind));
    }

    [Fact]
    public void ExecuteFailureAfterEveryFilesystemStepRestoresOriginalTreeAndRecoveryIsIdempotent()
    {
        using TransactionFixture probeFixture = TransactionFixture.Create();
        RecordingObserver probe = new(probeFixture.Path);
        Transaction(probeFixture.Path, probe).Execute(probeFixture.CreateUpdatePlan());
        int stepCount = probe.Steps.Count;

        Assert.True(stepCount > 0);
        for (int failureSequence = 1; failureSequence <= stepCount; failureSequence++)
        {
            using TransactionFixture fixture = TransactionFixture.Create();
            SortedDictionary<string, byte[]> before = Snapshot(fixture.Path);
            FailingObserver failure = new(fixture.Path, failureSequence);
            SquadTransaction transaction = Transaction(
                fixture.Path,
                failure);

            InjectedSquadTransactionFailure exception = Assert.Throws<InjectedSquadTransactionFailure>(() =>
                transaction.Execute(fixture.CreateUpdatePlan()));

            Assert.Equal(failureSequence, exception.Sequence);
            AssertSnapshotsEqual(before, Snapshot(fixture.Path));

            // Rehydrate the exact tree visible at the injection boundary. This models a process
            // terminating before its catch block can run, while the first assertion above still
            // independently proves that an ordinary caught failure rolls back immediately.
            RestoreSnapshot(fixture.Path, Assert.IsType<SortedDictionary<string, byte[]>>(
                failure.InterruptedSnapshot));
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);
            SortedDictionary<string, byte[]> afterFirstRecovery = Snapshot(fixture.Path);
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);

            AssertSnapshotsEqual(before, afterFirstRecovery);
            AssertSnapshotsEqual(afterFirstRecovery, Snapshot(fixture.Path));
        }
    }

    [Theory]
    [InlineData(SquadTransactionStepKind.FileApplied)]
    [InlineData(SquadTransactionStepKind.LockApplied)]
    [InlineData(SquadTransactionStepKind.ReceiptApplied)]
    public void ExecutePostIntentExternalReplacementPreservesRaceAndRetainsRecoverableConflict(
        SquadTransactionStepKind replacementCheckpoint)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SquadStateStore store = Store(fixturePath);
        PostIntentReplacementObserver observer = new(
            fixturePath,
            store,
            SquadDeploymentScope.Project,
            fixture.RelativePath,
            replacementCheckpoint);
        SquadTransaction transaction = new(store, observer);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();

        Exception? exception = Record.Exception(() => transaction.Execute(updatePlan));

        SquadDeploymentConflictException
            conflict = Assert.IsAssignableFrom<SquadDeploymentConflictException>(exception);
        Assert.Contains("changed", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(observer.ExternalBytes, File.ReadAllBytes(observer.ReplacedPath));
        if (replacementCheckpoint != SquadTransactionStepKind.FileApplied)
            Assert.Equal("installed body", Read(fixturePath, fixture.RelativePath));
        if (replacementCheckpoint != SquadTransactionStepKind.LockApplied)
            AssertLockEqual(Lock(), Assert.IsType<SquadLock>(store.ReadLock(
                fixturePath,
                SquadDeploymentScope.Project)));
        if (replacementCheckpoint != SquadTransactionStepKind.ReceiptApplied)
            AssertReceiptEqual(fixture.Receipt, Assert.IsType<SquadReceipt>(store.ReadReceipt(
                fixturePath,
                SquadDeploymentScope.Project)));
        Assert.True(
            Directory.Exists(store.ResolveTransactionDirectory(
                fixturePath,
                SquadDeploymentScope.Project)),
            "A compare-and-restore conflict must retain its verified journal and backup authority.");

        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        Exception? recoveryException = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsAssignableFrom<SquadDeploymentConflictException>(recoveryException);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
        Assert.Equal(observer.ExternalBytes, File.ReadAllBytes(observer.ReplacedPath));

        Exception? repeatedRecoveryException = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Assert.IsAssignableFrom<SquadDeploymentConflictException>(repeatedRecoveryException);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Fact]
    public void ExecutePostIntentExternalChildInTransactionCreatedParentPreservesParentAndReportsConflict()
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        const string generatedPath = ".warp/roles/reviewer.md";
        const string externalPath = ".warp/roles/operator-note.md";
        SquadDeploymentPlan plan = SquadDeploymentPlanner.CreateInstall(
            fixturePath,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(generatedPath, "generated reviewer", "warp")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        ExternalChildObserver observer = new(fixturePath, generatedPath, externalPath);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(plan));

        Assert.IsAssignableFrom<SquadDeploymentConflictException>(exception);
        Assert.False(File.Exists(ToPlatformPath(fixturePath, generatedPath)));
        Assert.Equal("external operator note", Read(fixturePath, externalPath));
        Assert.True(Directory.Exists(ToPlatformPath(fixturePath, ".warp/roles")));
        Assert.True(Directory.Exists(Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction")));
    }

    [Fact]
    public void ExecutePreparedArtifactsAreAtomicAndDurableBeforeFirstTargetMutation()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        PreparedArtifactsObserver observer = new(
            fixture.Path,
            fixture.RelativePath);

        Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan());

        Assert.True(observer.ObservedPreparedCheckpoint);
    }

    [Theory]
    [InlineData("truncated-journal")]
    [InlineData("duplicate-journal-field")]
    [InlineData("partial-backup")]
    [InlineData("wrong-backup-digest")]
    public void RecoverTruncatedOrDigestMismatchedPreparedArtifactRefusesAuthority(
        string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfterCheckpoint(
            fixture,
            SquadTransactionCheckpointKind.Prepared);
        RestoreTree(fixturePath, interrupted);
        CorruptPreparedArtifact(fixturePath, fixture.RelativePath, corruption);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
        Assert.True(File.Exists(Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json")));
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("staging")]
    [InlineData("backup")]
    public void RecoverInterruptedInsideArtifactWriteRefusesPartialAuthority(string artifact)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfter(
            fixture,
            SquadTransactionStepKind.FileBackedUp);
        RestoreTree(fixturePath, interrupted);
        ReplacePublishedArtifactWithPartialTemporaryFile(
            fixturePath,
            fixture.RelativePath,
            artifact);
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
    // A project lease is keyed by the physical root, a global lease by the home. So a project and
    // a global transaction on one root hold independent leases, while two transactions of the same
    // scope contend when they reach the same key, through a symlink alias or directly.
    [InlineData(SquadDeploymentScope.Project, SquadDeploymentScope.Global, false, false)]
    [InlineData(SquadDeploymentScope.Global, SquadDeploymentScope.Project, false, false)]
    [InlineData(SquadDeploymentScope.Project, SquadDeploymentScope.Global, true, false)]
    [InlineData(SquadDeploymentScope.Global, SquadDeploymentScope.Project, true, false)]
    [InlineData(SquadDeploymentScope.Project, SquadDeploymentScope.Project, false, true)]
    [InlineData(SquadDeploymentScope.Project, SquadDeploymentScope.Project, true, true)]
    [InlineData(SquadDeploymentScope.Global, SquadDeploymentScope.Global, false, true)]
    [InlineData(SquadDeploymentScope.Global, SquadDeploymentScope.Global, true, true)]
    public async Task ExecuteActiveLeaseOnOnePhysicalRootContendsOnlyWithItsOwnScope(
        SquadDeploymentScope activeScope,
        SquadDeploymentScope contenderScope,
        bool useSymlinkAlias,
        bool expectContention)
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string aliasRoot = Path.Combine(fixture.Path, "target-alias");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateSymbolicLink(aliasRoot, targetRoot);
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan activePlan = SquadDeploymentPlanner.CreateInstall(
            targetRoot,
            activeScope,
            Lock(),
            [Rendered(AgentRelativePath(activeScope, "active.toml"), "active")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        string contenderRoot = useSymlinkAlias ? aliasRoot : targetRoot;
        string contenderFile = AgentRelativePath(contenderScope, "contender.toml");
        SquadDeploymentPlan contenderPlan = SquadDeploymentPlanner.CreateInstall(
            contenderRoot,
            contenderScope,
            Lock(),
            [Rendered(contenderFile, "contender")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadTransaction activeTransaction = new(store, blocker);
        Task activeExecution = BlockedTransaction.Start(() =>
            activeTransaction.Execute(activePlan));
        BlockedTransaction.WaitForStep(
            blocker.Reached,
            activeExecution,
            "The active transaction did not acquire its lease before contention was tested.");
        SortedDictionary<string, TreeEntry> beforeContention = CaptureTree(fixture.Path);

        Exception? recoveryException = null;
        Exception? executeException;
        IReadOnlyDictionary<string, TreeEntry>? afterContention;
        try
        {
            SquadTransaction storeTransaction = new(store);
            if (expectContention)
                recoveryException = Record.Exception(() => storeTransaction.Recover(
                    contenderRoot,
                    contenderScope));

            executeException = Record.Exception(() =>
                storeTransaction.Execute(contenderPlan));
            afterContention = CaptureTree(fixture.Path);
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        if (expectContention)
        {
            AssertActiveLeaseConflict(recoveryException);
            AssertActiveLeaseConflict(executeException);
            AssertTreesEqual(
                beforeContention,
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, TreeEntry>>(afterContention));
            Assert.False(File.Exists(ToPlatformPath(targetRoot, contenderFile)));
        }
        else
        {
            Assert.Null(executeException);
            Assert.Equal("contender", Read(targetRoot, contenderFile));
        }
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("lock-state")]
    [InlineData("receipt-state")]
    public void ExecuteStagedArtifactChangedAfterPreparedBeforeApplyIsRejected(string artifact)
    {
        using TempDirectory fixture = new();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlanner.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(relativePath, "generated conductor")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        StagedArtifactTamperingObserver observer = new(
            fixture.Path,
            relativePath,
            artifact);

        Exception? exception = Record.Exception(() => new SquadTransaction(store, observer).Execute(plan));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        string reportedArtifact = artifact == "target-file"
            ? relativePath
            : artifact.Split('-')[0];
        Assert.Contains(reportedArtifact, invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(ToPlatformPath(fixture.Path, relativePath)));
        Assert.False(File.Exists(store.ResolveLockPath(fixture.Path, SquadDeploymentScope.Project)));
        Assert.False(File.Exists(store.ResolveReceiptPath(fixture.Path, SquadDeploymentScope.Project)));
        Assert.False(observer.SawAffectedApplyCheckpoint);
        Assert.True(
            Directory.Exists(store.ResolveTransactionDirectory(
                fixture.Path,
                SquadDeploymentScope.Project)),
            "A changed prepared stage is non-authoritative evidence and must be retained.");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mutated")]
    [InlineData("extra")]
    public void ExecuteClosedArtifactVerificationOccursBeforeFirstLeafMutation(
        string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        PreparedBoundaryCorruptionObserver observer = new(
            fixturePath,
            fixture.RelativePath,
            corruption);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(updatePlan));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(observer.CorruptedPreparedArtifacts);
        Assert.False(
            observer.SawLeafOrStateMutation,
            "Closed artifact verification must run before an active transition, leaf claim, or state mutation.");
        // The retained journal creates <root>/.kyber-weave before verification runs. Its evidence is
        // excluded from the comparison, so the directory itself is the only expected addition, and no
        // leaf artifact may differ.
        SortedDictionary<string, TreeEntry> expectedAfterRejection = new(before, StringComparer.Ordinal)
        {
            [".kyber-weave"] = new TreeEntry(TreeEntryKind.Directory, null, null)
        };
        AssertTreesEqual(expectedAfterRejection, CaptureTreeWithoutTransactionEvidence(fixturePath));
        Assert.True(
            Directory.Exists(Path.Combine(
                fixturePath,
                ".kyber-weave",
                ".squad-transaction")),
            "A rejected prepared generation must be retained as diagnostic evidence.");
    }

    [Theory]
    [InlineData("remove", "target-stage")]
    [InlineData("remove", "target-backup")]
    [InlineData("remove", "target-link-metadata")]
    [InlineData("remove", "state-stage")]
    [InlineData("remove", "state-original")]
    [InlineData("add", "target-stage")]
    [InlineData("add", "target-backup")]
    [InlineData("add", "target-link-metadata")]
    [InlineData("add", "state-stage")]
    [InlineData("add", "state-original")]
    [InlineData("add", "claimed-original")]
    [InlineData("add", "discarded-after-image")]
    public void ExecuteSemanticArtifactSetCannotBeChangedByEditingManifestAndTreeTogether(
        string edit,
        string semanticRole)
    {
        using RichTransactionFixture fixture = RichTransactionFixture.Create();
        string fixturePath = fixture.Path;
        CheckpointFailingObserver observer = new(
            fixturePath,
            SquadTransactionCheckpointKind.Prepared);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(updatePlan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        string transactionDirectory = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction");
        string intentPath = Path.Combine(transactionDirectory, "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        EditSemanticArtifactSet(
            intent,
            transactionDirectory,
            edit,
            semanticRole);
        WriteJsonObject(intentPath, intent);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData("target", "edit-bytes")]
    [InlineData("target", "replacement-link")]
    [InlineData("target", "replacement-directory")]
    [InlineData("target", "unchanged")]
    [InlineData("lock", "edit-bytes")]
    [InlineData("lock", "replacement-link")]
    [InlineData("lock", "replacement-directory")]
    [InlineData("lock", "unchanged")]
    [InlineData("receipt", "edit-bytes")]
    [InlineData("receipt", "replacement-link")]
    [InlineData("receipt", "replacement-directory")]
    [InlineData("receipt", "unchanged")]
    public void ExecuteTargetAndStateStageChangedAtOriginalClaimedCheckpointIsRejectedBeforePublish(
        string subject,
        string mutation)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        SquadStateStore store = Store(fixture.Path);
        string liveRelativePath = subject switch
        {
            "target" => fixture.RelativePath,
            "lock" => StateRelativePath(
                fixture.Path,
                store.ResolveLockPath(fixture.Path, SquadDeploymentScope.Project)),
            "receipt" => StateRelativePath(
                fixture.Path,
                store.ResolveReceiptPath(fixture.Path, SquadDeploymentScope.Project)),
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, null)
        };
        TreeEntry originalLiveEntry = CaptureTree(fixture.Path)[liveRelativePath];
        OriginalClaimStageTamperingObserver observer = new(
            fixture.Path,
            fixture.RelativePath,
            subject,
            mutation);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = new(store, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(updatePlan));

        Assert.True(observer.SawOriginalClaimed);
        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            Assert.Null(exception);
            Assert.True(observer.SawAffectedAfterImage);
            Assert.True(observer.SawAffectedPublicApply);
            Assert.False(Directory.Exists(store.ResolveTransactionDirectory(
                fixture.Path,
                SquadDeploymentScope.Project)));
            return;
        }

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains(
            subject == "target" ? Path.GetFileName(fixture.RelativePath) : subject,
            invalid.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(
            observer.SawAffectedAfterImage,
            "A changed stage must be rejected before its after-image checkpoint.");
        Assert.False(
            observer.SawAffectedPublicApply,
            "A changed stage must be rejected before its public apply event.");
        AssertTreeEntryEqual(originalLiveEntry, CaptureTree(fixture.Path)[liveRelativePath]);
        Assert.True(File.Exists(observer.IntentPath));
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.MutatedStageEntry),
            CaptureTree(fixture.Path)[observer.StageRelativePath]);
    }

    [Fact]
    public void PublishStageNoOverwriteVerifiesAuthorityAtAdjacentMoveBoundary()
    {
        string source = Corpus.ReadText("src/KyberWeave.Data/Squad/Deployment/SquadTransaction.cs");
        string declaration = ExtractMethodDeclaration(source, "PublishStageNoOverwrite");
        string method = ExtractMethod(source, "PublishStageNoOverwrite");

        Assert.Contains("IntentDocument", declaration, StringComparison.Ordinal);
        Assert.Contains("ArtifactArea", declaration, StringComparison.Ordinal);
        Assert.Contains("ArtifactRole", declaration, StringComparison.Ordinal);
        Assert.Contains("ArtifactFingerprintMatches", method, StringComparison.Ordinal);
        Assert.Contains("MoveEntryNoOverwrite", method, StringComparison.Ordinal);

        int fingerprint = method.LastIndexOf("ArtifactFingerprintMatches", StringComparison.Ordinal);
        int move = method.IndexOf("MoveEntryNoOverwrite", fingerprint, StringComparison.Ordinal);
        Assert.True(fingerprint >= 0 && move > fingerprint);
        string adjacentBoundary = method[fingerprint..move];
        Assert.DoesNotContain("Notify", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("await", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishIntent", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", adjacentBoundary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("target", "file", "edit-bytes")]
    [InlineData("target", "file-link", "retarget-file-link")]
    [InlineData("target", "file", "replace-directory")]
    [InlineData("target", "file", "unchanged")]
    [InlineData("lock", "file", "edit-bytes")]
    [InlineData("lock", "directory-link", "retarget-directory-link")]
    [InlineData("lock", "directory", "replace-file")]
    [InlineData("lock", "file", "unchanged")]
    [InlineData("receipt", "file", "edit-bytes")]
    [InlineData("receipt", "file-link", "retarget-file-link")]
    [InlineData("receipt", "directory", "replace-file")]
    [InlineData("receipt", "file", "unchanged")]
    public void RecoverClaimChangedAtOriginalClaimedCheckpointIsNeverMovedLive(
        string subject,
        string originalKind,
        string mutation)
    {
        AssertCaughtClaimMutation(subject, originalKind, mutation);
        AssertRehydratedClaimMutation(subject, originalKind, mutation);
    }

    [Fact]
    public void ExecuteLeavesExactlyTheDirectoriesTheDeploymentNeeds()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SquadReceipt receipt = SeedDirectoryCleanupDeployment(root);

        Transaction(root).Execute(DirectoryCleanupUpdatePlan(root, receipt));

        string[] expectedDirectories =
        [
            .. AncestorDirectories(StateRelativePath(
                root,
                Store(root).ResolveStateDirectory(root, SquadDeploymentScope.Project))),
            ".codex",
            ".codex/agents",
            ".warp",
            ".warp/roles"
        ];
        string[] actualDirectories = CaptureTree(root)
            .Where(pair => pair.Value.Kind == TreeEntryKind.Directory)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedDirectories.Order(StringComparer.Ordinal), actualDirectories);
        Assert.False(Directory.Exists(Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction")));
        Assert.Equal("one v2", Read(root, UpdatedAgentOnePath));
        Assert.Equal("two v2", Read(root, UpdatedAgentTwoPath));
        Assert.Equal("three v1", Read(root, NewWarpRolePath));
        Assert.Equal("adopted body", Read(root, AdoptedAgentPath));
    }

    [Fact]
    public void ExecuteMidApplyFailureRestoresTheExactPriorTree()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SquadReceipt receipt = SeedDirectoryCleanupDeployment(root);
        SortedDictionary<string, TreeEntry> original = CaptureTree(root);
        LifecycleStepFailingObserver observer = new(root, SquadTransactionStepKind.FileApplied);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(root, observer).Execute(DirectoryCleanupUpdatePlan(root, receipt)));

        AssertTreesEqual(original, CaptureTree(root));
    }

    [Fact]
    public void RecoverMidApplyFailureRestoresTheExactPriorTree()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SquadReceipt receipt = SeedDirectoryCleanupDeployment(root);
        SortedDictionary<string, TreeEntry> original = CaptureTree(root);
        LifecycleStepFailingObserver observer = new(root, SquadTransactionStepKind.FileApplied);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(root, observer).Execute(DirectoryCleanupUpdatePlan(root, receipt)));
        RestoreTree(
            root,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(observer.InterruptedTreeSnapshot));
        Assert.NotEqual(original.Keys, CaptureTree(root).Keys);

        Transaction(root).Recover(root, SquadDeploymentScope.Project);

        AssertTreesEqual(original, CaptureTree(root));
    }

    [Fact]
    public void RecoverInterruptedAfterTheLastPublicationRestoresTheExactPriorTree()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SquadReceipt receipt = SeedDirectoryCleanupDeployment(root);
        SortedDictionary<string, TreeEntry> original = CaptureTree(root);
        LastFileAppliedFailingObserver observer = new(root, NewWarpRolePath);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(root, observer).Execute(DirectoryCleanupUpdatePlan(root, receipt)));
        RestoreTree(
            root,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(observer.InterruptedTreeSnapshot));

        // A process that died here left every published stage out of the work tree, so the retained
        // journal must still describe that tree well enough to roll it back.
        Transaction(root).Recover(root, SquadDeploymentScope.Project);

        AssertTreesEqual(original, CaptureTree(root));
    }

    [Fact]
    public void ExecuteNeverRemovesADirectoryHoldingAForeignStageFileOrAnAdoptedFile()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SquadReceipt receipt = SeedDirectoryCleanupDeployment(root);
        ForeignStageFileObserver observer = new(root);

        Transaction(root, observer).Execute(DirectoryCleanupUpdatePlan(root, receipt));

        Assert.True(observer.Injected);
        Assert.Empty(observer.Violations);
        Assert.True(
            observer.SawPendingStageOfAnotherTarget,
            "The cleanup must leave a pending stage of another target alone.");
        Assert.True(
            observer.SawAppliedFilePrunedOtherBranches is true,
            "The cleanup must still prune the applied files' now-empty staging branches.");
        Assert.False(Directory.Exists(Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction")));
    }

    private static SquadReceipt SeedDirectoryCleanupDeployment(string root)
    {
        Write(root, UpdatedAgentOnePath, "one v1");
        Write(root, UpdatedAgentTwoPath, "two v1");
        Write(root, AdoptedAgentPath, "adopted body");
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(UpdatedAgentOnePath, Digest("one v1"), "codex", false),
            new SquadOwnedFile(UpdatedAgentTwoPath, Digest("two v1"), "codex", false),
            new SquadOwnedFile(NewWarpRolePath, Digest("three v1"), "warp", false),
            new SquadOwnedFile(AdoptedAgentPath, Digest("adopted body"), "codex", true));
        WriteState(root, Lock(), receipt);
        return receipt;
    }

    private static SquadDeploymentPlan DirectoryCleanupUpdatePlan(
        string root,
        SquadReceipt receipt)
    {
        return SquadDeploymentPlanner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [
                Rendered(UpdatedAgentOnePath, "one v2"),
                Rendered(UpdatedAgentTwoPath, "two v2"),
                Rendered(NewWarpRolePath, "three v1", "warp"),
                Rendered(AdoptedAgentPath, "adopted body")
            ],
            receipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
    }

    private static IReadOnlyList<string> AncestorDirectories(string relativePath)
    {
        string[] segments = relativePath.Split('/');
        return
        [
            .. Enumerable.Range(1, segments.Length)
                .Select(count => string.Join('/', segments[..count]))
        ];
    }

    /// <summary>
    ///     Fails after the publication of one named file, which models a process ending once the last
    ///     artifact of the apply has left the work tree.
    /// </summary>
    private sealed class LastFileAppliedFailingObserver(
        string root,
        string relativePath) : ISquadTransactionObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != SquadTransactionStepKind.FileApplied ||
                !string.Equals(step.RelativePath, relativePath, StringComparison.Ordinal))
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    /// <summary>
    ///     Drops a file no part of this deployment owns into a staging directory once the first
    ///     target file has been applied, and records whether the staging tree kept it from there on.
    /// </summary>
    private sealed class ForeignStageFileObserver(string root) : ISquadTransactionObserver
    {
        private readonly string _stagingDirectory = Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction",
            "staging");

        public bool Injected { get; private set; }

        public bool SawPendingStageOfAnotherTarget { get; private set; }

        public bool? SawAppliedFilePrunedOtherBranches { get; private set; }

        public List<string> Violations { get; } = [];

        public void AfterStep(SquadTransactionStep step)
        {
            if (!Injected)
            {
                if (step.Kind != SquadTransactionStepKind.FileApplied)
                    return;

                Directory.CreateDirectory(Staging("codex"));
                File.WriteAllText(Staging("codex", "foreign.toml"), "foreign");
                Injected = true;
                return;
            }

            SawPendingStageOfAnotherTarget |= File.Exists(
                Staging("warp", ".warp", "roles", "three.md"));
            if (!File.Exists(Staging("codex", "foreign.toml")) ||
                !Directory.Exists(Staging("codex")))
                Violations.Add($"the foreign stage file's directory was removed by {step.Kind}");

            string adoptedPath = Path.Combine(
                root,
                AdoptedAgentPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(adoptedPath) ||
                !Directory.Exists(Path.GetDirectoryName(adoptedPath)!))
                Violations.Add($"the adopted file's directory was removed by {step.Kind}");

            if (step.Kind == SquadTransactionStepKind.LockApplied)
            {
                bool prunedAppliedBranches =
                    !Directory.Exists(Staging("codex", ".codex", "agents")) &&
                    !Directory.Exists(Staging("warp"));
                bool keptBlockedBranch =
                    Directory.Exists(Staging()) && Directory.Exists(Staging("codex"));
                SawAppliedFilePrunedOtherBranches = prunedAppliedBranches && keptBlockedBranch;
            }
        }

        private string Staging(params string[] relativeSegments)
        {
            string path = _stagingDirectory;
            foreach (string segment in relativeSegments)
                path = Path.Combine(path, segment);

            return path;
        }
    }
}
