using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// Deployment-state contracts: install, adopt, collision and removal plans. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateInstallTests : InMemoryDeploymentStateTestBase
{
    [Fact]
    public void ExecuteWithDurableWritesEnabledDeploysWhatItWrote()
    {
        // The test assembly turns SyncToDisk off for speed (SquadTestDurability), so the branch
        // that ships would otherwise never run. Exercise it once in an isolated async flow scope.
        using (SquadTransaction.UseSyncToDiskScope(true))
        {
            string targetRoot = Root;
            SquadStateStore store = StoreAt(targetRoot);
            SquadDeploymentPlan plan = Planner.CreateInstall(
                targetRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/durable.toml", "durable")],
                [],
                false,
                new FixedTimeProvider(InstalledAt));

            TransactionAt(targetRoot).Execute(plan);

            Assert.Equal(
                "durable",
                Read(targetRoot, ".codex/agents/durable.toml"));
        }
    }

    [Fact]
    public void CreateInstallIdenticalUnmanagedFileWithAdoptRecordsOwnershipWithoutRewriting()
    {
        Write(Root, ".codex/agents/conductor.toml", "same bytes");
        DateTime before = Files.GetLastWriteTimeUtc(
            Path.Combine(Root, ".codex/agents/conductor.toml"));
        SquadDeploymentPlan plan = Planner.CreateInstall(
            Root,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "same bytes")],
            [],
            true,
            new FixedTimeProvider(InstalledAt));

        TransactionAt(Root).Execute(plan);

        SquadOwnedFile owned = Assert.Single(plan.Receipt.Files);
        Assert.True(owned.Adopted);
        Assert.Equal(Digest("same bytes"), owned.Sha256);
        Assert.Equal(before, Files.GetLastWriteTimeUtc(
            Path.Combine(Root, owned.RelativePath)));
    }

    [Fact]
    public void ExecuteInstallPathCreatedAfterPreflightRefusesToOverwriteRacedUnmanagedFile()
    {
        const string relativePath = ".codex/agents/conductor.toml";
        SquadDeploymentPlan plan = Planner.CreateInstall(
            Root,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(relativePath, "generated body")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        Write(Root, relativePath, "raced unmanaged body");
        SortedDictionary<string, TreeEntry> before = CaptureTree(Root);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            TransactionAt(Root).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced unmanaged body", Read(Root, relativePath));
        AssertTreesEqual(before, CaptureTree(Root));
    }

    [Theory]
    [InlineData("same bytes", false)]
    [InlineData("different bytes", true)]
    [InlineData("different bytes", false)]
    public void CreateInstallUnmanagedCollisionRefusesWithoutChangingTree(
        string existingContent,
        bool adopt)
    {
        Write(Root, ".codex/agents/conductor.toml", existingContent);
        SortedDictionary<string, byte[]> before = Snapshot(Root);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Planner.CreateInstall(
                Root,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/conductor.toml", "same bytes")],
                [],
                adopt,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(
            ".codex/agents/conductor.toml",
            exception.Message,
            StringComparison.Ordinal);
        AssertSnapshotsEqual(before, Snapshot(Root));
    }

    [Fact]
    public void CreateUninstallUnchangedOwnedFileIsRemovedAndEditedOwnedFileIsRetained()
    {
        SquadOwnedFile unchanged = new(
            ".codex/agents/conductor.toml",
            Digest("installed conductor"),
            "codex",
            false);
        SquadOwnedFile edited = new(
            ".codex/agents/reviewer.toml",
            Digest("installed reviewer"),
            "codex",
            false);
        SquadReceipt receipt = Receipt(unchanged, edited);
        Write(Root, unchanged.RelativePath, "installed conductor");
        Write(Root, edited.RelativePath, "operator reviewer edit");
        WriteState(Root, Lock(), receipt);
        SquadDeploymentPlan plan = Planner.CreateUninstall(
            Root,
            SquadDeploymentScope.Project,
            receipt);

        TransactionAt(Root).Execute(plan);

        Assert.False(Files.FileExists(Path.Combine(Root, unchanged.RelativePath)));
        Assert.Equal("operator reviewer edit", Read(Root, edited.RelativePath));
        SquadReceipt? retainedReceipt = StoreAt(Root).ReadReceipt(
            Root,
            SquadDeploymentScope.Project);
        SquadOwnedFile retained = Assert.Single(Assert.IsType<SquadReceipt>(retainedReceipt).Files);
        Assert.Equal(edited, retained);
    }

    [Fact]
    public void CreateUninstallOnlyTargetRemovesThatTargetAndKeepsEveryOtherTargetsFiles()
    {
        Write(Root, CodexOwned.RelativePath, "codex body");
        Write(Root, CursorOwned.RelativePath, "cursor body");
        SquadReceipt receipt = Receipt(CodexOwned, CursorOwned);
        WriteState(Root, Lock(), receipt);

        SquadDeploymentPlan plan = Planner.CreateUninstall(
            Root,
            SquadDeploymentScope.Project,
            receipt,
            onlyTarget: SquadTarget.Cursor);

        SquadFileMutation removal = Assert.Single(plan.FileMutations);
        Assert.Equal(CursorOwned.RelativePath, removal.RelativePath);
        Assert.Equal(CodexOwned, Assert.Single(plan.Receipt.Files));
        Assert.Equal(SquadStateMutation.Write, plan.ReceiptMutation);

        TransactionAt(Root).Execute(plan);

        Assert.True(Files.FileExists(Path.Combine(Root, CodexOwned.RelativePath)));
        Assert.False(Files.FileExists(Path.Combine(Root, CursorOwned.RelativePath)));
        SquadReceipt kept = Assert.IsType<SquadReceipt>(
            StoreAt(Root).ReadReceipt(Root, SquadDeploymentScope.Project));
        Assert.Equal(CodexOwned, Assert.Single(kept.Files));
    }

    [Fact]
    public void CreateUninstallOnlyTargetDeletesTheReceiptOnlyWhenNothingRemains()
    {
        Write(Root, CursorOwned.RelativePath, "cursor body");
        SquadReceipt receipt = Receipt(CursorOwned);
        WriteState(Root, Lock(), receipt);

        SquadDeploymentPlan plan = Planner.CreateUninstall(
            Root,
            SquadDeploymentScope.Project,
            receipt,
            onlyTarget: SquadTarget.Cursor);

        Assert.Equal(SquadStateMutation.Delete, plan.ReceiptMutation);
        Assert.Equal(SquadStateMutation.Delete, plan.LockMutation);
        Assert.Empty(plan.Receipt.Files);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateUninstallOnlyTargetKeepsItsDegradationsWhileAFileIsRetainedAndDropsThemOtherwise(
        bool cursorFileEdited)
    {
        Write(Root, CodexOwned.RelativePath, "codex body");
        Write(Root, CursorOwned.RelativePath, cursorFileEdited ? "operator edit" : "cursor body");
        SquadReceipt receipt = Receipt(CodexOwned, CursorOwned) with { Degradations = CodexAndCursorDegradations };

        SquadDeploymentPlan plan = Planner.CreateUninstall(
            Root,
            SquadDeploymentScope.Project,
            receipt,
            onlyTarget: SquadTarget.Cursor);

        Assert.Equal(
            cursorFileEdited ? CodexAndCursorDegradations : [CodexAndCursorDegradations[0]],
            plan.Receipt.Degradations);
        Assert.Equal(cursorFileEdited, plan.Receipt.Files.Contains(CursorOwned));
    }

    [Fact]
    public void CreateUninstallWithoutOnlyTargetDropsEveryDegradationWithTheReceipt()
    {
        Write(Root, CodexOwned.RelativePath, "codex body");
        Write(Root, CursorOwned.RelativePath, "cursor body");
        SquadReceipt receipt = Receipt(CodexOwned, CursorOwned) with { Degradations = CodexAndCursorDegradations };

        SquadDeploymentPlan plan = Planner.CreateUninstall(
            Root,
            SquadDeploymentScope.Project,
            receipt);

        Assert.Equal(SquadStateMutation.Delete, plan.ReceiptMutation);
        Assert.Equal(2, plan.FileMutations.Count);
    }

    [Fact]
    public void CreateUninstallOnlyTargetDropsASiblingOwnedFileInsteadOfRetainingItAndKeepsHomeFiles()
    {
        SquadGlobalRoots roots = new(_ => null, Root);
        Write(roots.ResolveGlobalRoot(SquadTarget.Codex), "agents/conductor.toml", "codex body");
        Write(roots.ResolveGlobalRoot(SquadTarget.Cursor), "agents/reviewer.md", "cursor body");
        SquadOwnedFile codex = new("agents/conductor.toml", Digest("codex body"), "codex", false);
        SquadOwnedFile cursor = new("agents/reviewer.md", Digest("cursor body"), "cursor", false);
        SquadHomeFile homeFile = GuidanceHomeFile();
        SquadReceipt receipt = GlobalReceiptWithHomeFiles(homeFile) with { Files = [codex, cursor] };
        SquadReceipt sibling = GlobalReceiptWithHomeFiles() with { Files = [codex] };

        SquadDeploymentPlan plan = Planner.CreateUninstall(
            Root,
            SquadDeploymentScope.Global,
            receipt,
            roots,
            [sibling],
            SquadTarget.Cursor);

        Assert.Equal(cursor.RelativePath, Assert.Single(plan.FileMutations).RelativePath);
        Assert.DoesNotContain(codex, plan.Receipt.Files);
        Assert.Equal([homeFile], plan.Receipt.HomeFiles);
    }

    [Fact]
    public void ExecuteUninstallFileChangedAfterExactHashPreflightRefusesToDeleteRacedEdit()
    {
        const string relativePath = ".codex/agents/conductor.toml";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            relativePath,
            Digest("installed body"),
            "codex",
            false));
        Write(Root, relativePath, "installed body");
        WriteState(Root, Lock(), receipt);
        SquadDeploymentPlan plan = Planner.CreateUninstall(
            Root,
            SquadDeploymentScope.Project,
            receipt);
        Write(Root, relativePath, "raced operator edit");
        SortedDictionary<string, TreeEntry> before = CaptureTree(Root);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            TransactionAt(Root).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced operator edit", Read(Root, relativePath));
        AssertTreesEqual(before, CaptureTree(Root));
    }

    [Fact]
    public void ExecuteFailureAfterEachFileMutationRestoresFilesDirectoriesSymlinksAndFreshParents()
    {
        RichTransactionFixture probeFixture = RichTransactionFixture.Create(this);
        RecordingObserver probe = new(Files, probeFixture.Path);
        TransactionAt(probeFixture.Path, probe).Execute(probeFixture.CreateUpdatePlan());
        int stepCount = probe.Steps.Count;

        Assert.True(stepCount >= 10, "The fixture must exercise multiple stage, backup, apply, and state mutations.");
        for (int failureSequence = 1; failureSequence <= stepCount; failureSequence++)
        {
            RichTransactionFixture fixture = RichTransactionFixture.Create(this);
            SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
            FailingObserver failure = new(Files, fixture.Path, failureSequence);
            SquadTransaction transaction = TransactionAt(fixture.Path, failure);

            InjectedSquadTransactionFailure exception = Assert.Throws<InjectedSquadTransactionFailure>(() =>
                transaction.Execute(fixture.CreateUpdatePlan()));

            Assert.Equal(failureSequence, exception.Sequence);
            AssertTreesEqual(before, CaptureTree(fixture.Path));

            RestoreTree(
                fixture.Path,
                Assert.IsType<SortedDictionary<string, TreeEntry>>(
                    failure.InterruptedTreeSnapshot));
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);
            SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixture.Path);
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);

            AssertTreesEqual(before, afterFirstRecovery);
            AssertTreesEqual(afterFirstRecovery, CaptureTree(fixture.Path));
        }
    }
}
