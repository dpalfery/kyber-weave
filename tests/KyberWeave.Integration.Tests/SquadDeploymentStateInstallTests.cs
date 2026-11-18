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

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Deployment-state contracts: install, adopt, collision and removal plans. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateInstallTests : SquadDeploymentStateTestBase
{
    [Fact]
    public void ExecuteWithDurableWritesEnabledDeploysWhatItWrote()
    {
        // The test assembly turns SyncToDisk off for speed (SquadTestDurability), so the branch
        // that ships would otherwise never run. Exercise it once in an isolated async flow scope.
        using (SquadTransaction.UseSyncToDiskScope(true))
        {
            using TempDirectory fixture = new();
            string targetRoot = Path.Combine(fixture.Path, "target");
            Directory.CreateDirectory(targetRoot);
            SquadStateStore store = Store(Path.Combine(fixture.Path, "application-data"));
            SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateInstall(
                targetRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/durable.toml", "durable")],
                [],
                false,
                new FixedTimeProvider(InstalledAt));

            new SquadTransaction(store).Execute(plan);

            Assert.Equal(
                "durable",
                File.ReadAllText(Path.Combine(targetRoot, ".codex", "agents", "durable.toml")));
        }
    }

    /// <summary>
    ///     An uninstall plan's receipt carries only retained files; the dry-run report must list
    ///     the planned removals with their target, and remove nothing.
    /// </summary>
    [Fact]
    public void UninstallDryRunListsPlannedRemovalsWithTargetsAndRemovesNothing()
    {
        using TempDirectory fixture = new();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            relativePath,
            Digest("installed body"),
            "codex",
            false));
        Write(fixture.Path, relativePath, "installed body");
        WriteState(fixture.Path, Lock(), receipt);
        SquadStateStore store = Store(fixture.Path);
        SquadUninstallCommand command = SquadTestCommands.Uninstall(
            stateStore: store,
            lifecycleService: SquadHostDefaults.CreateLifecycleService(stateStore: store));

        CapturedConsoleExecution<int> dryRun = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                PathOption = fixture.Path,
                DryRun = true
            }));

        Assert.Equal(0, dryRun.Result);
        Assert.Contains("would uninstall 1 files", dryRun.Output, StringComparison.Ordinal);
        Assert.Contains(relativePath, dryRun.Output, StringComparison.Ordinal);
        Assert.Contains("(codex)", dryRun.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(ToPlatformPath(fixture.Path, relativePath)));
    }

    [Fact]
    public void CreateInstallIdenticalUnmanagedFileWithAdoptRecordsOwnershipWithoutRewriting()
    {
        using TempDirectory fixture = new();
        Write(fixture.Path, ".codex/agents/conductor.toml", "same bytes");
        DateTime before = File.GetLastWriteTimeUtc(
            Path.Combine(fixture.Path, ".codex/agents/conductor.toml"));
        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "same bytes")],
            [],
            true,
            new FixedTimeProvider(InstalledAt));

        Transaction(fixture.Path).Execute(plan);

        SquadOwnedFile owned = Assert.Single(plan.Receipt.Files);
        Assert.True(owned.Adopted);
        Assert.Equal(Digest("same bytes"), owned.Sha256);
        Assert.Equal(before, File.GetLastWriteTimeUtc(
            Path.Combine(fixture.Path, owned.RelativePath)));
    }

    [Fact]
    public void ExecuteInstallPathCreatedAfterPreflightRefusesToOverwriteRacedUnmanagedFile()
    {
        using TempDirectory fixture = new();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(relativePath, "generated body")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        Write(fixture.Path, relativePath, "raced unmanaged body");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Transaction(fixture.Path).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced unmanaged body", Read(fixture.Path, relativePath));
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("same bytes", false)]
    [InlineData("different bytes", true)]
    [InlineData("different bytes", false)]
    public void CreateInstallUnmanagedCollisionRefusesWithoutChangingTree(
        string existingContent,
        bool adopt)
    {
        using TempDirectory fixture = new();
        Write(fixture.Path, ".codex/agents/conductor.toml", existingContent);
        SortedDictionary<string, byte[]> before = Snapshot(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            RealSquadDeployment.Planner.CreateInstall(
                fixture.Path,
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
        AssertSnapshotsEqual(before, Snapshot(fixture.Path));
    }

    [Fact]
    public void CreateUninstallUnchangedOwnedFileIsRemovedAndEditedOwnedFileIsRetained()
    {
        using TempDirectory fixture = new();
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
        Write(fixture.Path, unchanged.RelativePath, "installed conductor");
        Write(fixture.Path, edited.RelativePath, "operator reviewer edit");
        WriteState(fixture.Path, Lock(), receipt);
        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateUninstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            receipt);

        Transaction(fixture.Path).Execute(plan);

        Assert.False(File.Exists(Path.Combine(fixture.Path, unchanged.RelativePath)));
        Assert.Equal("operator reviewer edit", Read(fixture.Path, edited.RelativePath));
        SquadReceipt? retainedReceipt = Store(fixture.Path).ReadReceipt(
            fixture.Path,
            SquadDeploymentScope.Project);
        SquadOwnedFile retained = Assert.Single(Assert.IsType<SquadReceipt>(retainedReceipt).Files);
        Assert.Equal(edited, retained);
    }

    [Fact]
    public void CreateUninstallOnlyTargetRemovesThatTargetAndKeepsEveryOtherTargetsFiles()
    {
        using TempDirectory fixture = new();
        Write(fixture.Path, CodexOwned.RelativePath, "codex body");
        Write(fixture.Path, CursorOwned.RelativePath, "cursor body");
        SquadReceipt receipt = Receipt(CodexOwned, CursorOwned);
        WriteState(fixture.Path, Lock(), receipt);

        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateUninstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            receipt,
            onlyTarget: SquadTarget.Cursor);

        SquadFileMutation removal = Assert.Single(plan.FileMutations);
        Assert.Equal(CursorOwned.RelativePath, removal.RelativePath);
        Assert.Equal(CodexOwned, Assert.Single(plan.Receipt.Files));
        Assert.Equal(SquadStateMutation.Write, plan.ReceiptMutation);

        Transaction(fixture.Path).Execute(plan);

        Assert.True(File.Exists(Path.Combine(fixture.Path, CodexOwned.RelativePath)));
        Assert.False(File.Exists(Path.Combine(fixture.Path, CursorOwned.RelativePath)));
        SquadReceipt kept = Assert.IsType<SquadReceipt>(
            Store(fixture.Path).ReadReceipt(fixture.Path, SquadDeploymentScope.Project));
        Assert.Equal(CodexOwned, Assert.Single(kept.Files));
    }

    [Fact]
    public void CreateUninstallOnlyTargetDeletesTheReceiptOnlyWhenNothingRemains()
    {
        using TempDirectory fixture = new();
        Write(fixture.Path, CursorOwned.RelativePath, "cursor body");
        SquadReceipt receipt = Receipt(CursorOwned);
        WriteState(fixture.Path, Lock(), receipt);

        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateUninstall(
            fixture.Path,
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
        using TempDirectory fixture = new();
        Write(fixture.Path, CodexOwned.RelativePath, "codex body");
        Write(fixture.Path, CursorOwned.RelativePath, cursorFileEdited ? "operator edit" : "cursor body");
        SquadReceipt receipt = Receipt(CodexOwned, CursorOwned) with { Degradations = CodexAndCursorDegradations };

        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateUninstall(
            fixture.Path,
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
        using TempDirectory fixture = new();
        Write(fixture.Path, CodexOwned.RelativePath, "codex body");
        Write(fixture.Path, CursorOwned.RelativePath, "cursor body");
        SquadReceipt receipt = Receipt(CodexOwned, CursorOwned) with { Degradations = CodexAndCursorDegradations };

        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateUninstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            receipt);

        Assert.Equal(SquadStateMutation.Delete, plan.ReceiptMutation);
        Assert.Equal(2, plan.FileMutations.Count);
    }

    [Fact]
    public void CreateUninstallOnlyTargetDropsASiblingOwnedFileInsteadOfRetainingItAndKeepsHomeFiles()
    {
        using TempDirectory fixture = new();
        SquadGlobalRoots roots = new(_ => null, fixture.Path);
        Write(roots.ResolveGlobalRoot(SquadTarget.Codex), "agents/conductor.toml", "codex body");
        Write(roots.ResolveGlobalRoot(SquadTarget.Cursor), "agents/reviewer.md", "cursor body");
        SquadOwnedFile codex = new("agents/conductor.toml", Digest("codex body"), "codex", false);
        SquadOwnedFile cursor = new("agents/reviewer.md", Digest("cursor body"), "cursor", false);
        SquadHomeFile homeFile = GuidanceHomeFile();
        SquadReceipt receipt = GlobalReceiptWithHomeFiles(homeFile) with { Files = [codex, cursor] };
        SquadReceipt sibling = GlobalReceiptWithHomeFiles() with { Files = [codex] };

        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateUninstall(
            fixture.Path,
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
        using TempDirectory fixture = new();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            relativePath,
            Digest("installed body"),
            "codex",
            false));
        Write(fixture.Path, relativePath, "installed body");
        WriteState(fixture.Path, Lock(), receipt);
        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateUninstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            receipt);
        Write(fixture.Path, relativePath, "raced operator edit");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Transaction(fixture.Path).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced operator edit", Read(fixture.Path, relativePath));
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Fact]
    public void ExecuteFailureAfterEachFileMutationRestoresFilesDirectoriesSymlinksAndFreshParents()
    {
        using RichTransactionFixture probeFixture = RichTransactionFixture.Create();
        RecordingObserver probe = new(probeFixture.Path);
        Transaction(probeFixture.Path, probe).Execute(probeFixture.CreateUpdatePlan());
        int stepCount = probe.Steps.Count;

        Assert.True(stepCount >= 10, "The fixture must exercise multiple stage, backup, apply, and state mutations.");
        for (int failureSequence = 1; failureSequence <= stepCount; failureSequence++)
        {
            using RichTransactionFixture fixture = RichTransactionFixture.Create();
            SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
            FailingObserver failure = new(fixture.Path, failureSequence);
            SquadTransaction transaction = Transaction(fixture.Path, failure);

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
