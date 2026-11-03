using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the one-time move of legacy Squad state into the home-level layout, and the import of an
/// in-repo project's state into the home registry. Each rule guards a way a migration could silently
/// lose, overwrite or relocate an operator's ownership record.
/// </summary>
/// <remarks>
/// Every fixture points <see cref="ISquadUserPaths.HomeDirectory"/> and
/// <see cref="ISquadUserPaths.ApplicationDataDirectory"/> beneath a temp directory, so no test touches
/// the real home, the real application data directory, or a harness directory. Receipts and locks are
/// written through the store's own serializers so the fixtures carry the shapes the reader accepts.
/// A legacy receipt carries the <c>"."</c> target root the store still writes, and migrated receipts
/// are read back as raw JSON, so these tests do not depend on how the reader validates a rewritten root.
/// </remarks>
public sealed class SquadStateMigrationTests
{
    private static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    /// <summary>
    /// A legacy per-target-roots receipt is copied into the home global store, and its target root is
    /// rewritten to the home anchor. A per-target-roots receipt is anchored on the home, not on the root
    /// that happened to install it, so keeping the install root would tie the global state back to one
    /// project. Before this, the global state stayed in the per-user application data directory.
    /// </summary>
    [Fact]
    public void LegacyPerTargetRootsReceiptCopiesToHomeGlobalWithHomeAnchorTargetRoot()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        SquadLock legacyLock = Lock("1.2.3");
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), legacyLock);
        string legacyDirectory = LegacyGlobalDirectory(scenario, root);

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(legacyDirectory, "squad.lock.yml")),
            File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml")));
        Assert.Equal(
            PhysicalPathOf(scenario.Home),
            ReadTargetRoot(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")));
    }

    /// <summary>
    /// The legacy receipt and lock are left exactly as they were. A migration that moved them, instead of
    /// copying them, would strand any older CLI that still reads the per-user location during a rollback.
    /// </summary>
    [Fact]
    public void LegacyGlobalFilesAreByteIdenticalAfterMigration()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));
        string legacyDirectory = LegacyGlobalDirectory(scenario, root);
        TreeSnapshot before = DirectoryTreeSnapshot.SnapshotTree(legacyDirectory);

        scenario.Migration.MigrateGlobal(invokingCwd: null);

        DirectoryTreeSnapshot.AssertTreeUnchanged(legacyDirectory, before);
    }

    /// <summary>
    /// A second migration after a successful first one changes nothing. The home copy already exists, so
    /// the run is a no-op, and a re-run must not rewrite a receipt the operator may have since verified.
    /// </summary>
    [Fact]
    public void SecondMigrationRunIsANoOpThatLeavesTheHomeCopyUnchanged()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));
        scenario.Migration.MigrateGlobal(invokingCwd: null);
        byte[] receiptAfterFirstRun = File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json"));
        byte[] lockAfterFirstRun = File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml"));

        SquadMigrationReport secondRun = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(secondRun.MigratedItems);
        Assert.Equal(
            receiptAfterFirstRun,
            File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")));
        Assert.Equal(
            lockAfterFirstRun,
            File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml")));
    }

    /// <summary>
    /// An empty directory under <c>roots/</c> is not a legacy receipt. Counting it would turn one real
    /// receipt into an ambiguous set of two and block a migration that is perfectly safe to run.
    /// </summary>
    [Fact]
    public void EmptyLegacyRootDirectoriesAreIgnoredWhenCountingLegacyReceipts()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(scenario.ApplicationData, "KyberWeave", "squad", "roots", "0f3a-empty"));
        Directory.CreateDirectory(Path.Combine(scenario.ApplicationData, "KyberWeave", "squad", "roots", "1b2c-empty"));
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.True(
            File.Exists(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")),
            "The one real legacy receipt must migrate even though empty root directories sit beside it.");
    }

    /// <summary>
    /// Empty legacy root directories on their own are nothing to migrate and nothing to report. Reporting
    /// them as unmigrated would send an operator to fix state that does not exist.
    /// </summary>
    [Fact]
    public void EmptyLegacyRootDirectoriesAloneMigrateNothingAndReportNothing()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        Directory.CreateDirectory(Path.Combine(scenario.ApplicationData, "KyberWeave", "squad", "roots", "0f3a-empty"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.False(
            File.Exists(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")),
            "No home global receipt may be written when there is no legacy receipt to copy.");
    }

    /// <summary>
    /// Two non-empty legacy receipts are ambiguous: copying either one could hand the home store the wrong
    /// ownership boundary. So neither moves, and each is reported with guidance an operator can act on.
    /// </summary>
    [Fact]
    public void TwoNonEmptyLegacyGlobalReceiptsMigrateNoneAndReportEachWithGuidance()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string firstRoot = Path.Combine(fixture.Path, "alpha-target");
        string secondRoot = Path.Combine(fixture.Path, "beta-target");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        WriteLegacyGlobal(scenario, firstRoot, PerTargetRootsReceipt(), Lock("1.2.3"));
        WriteLegacyGlobal(scenario, secondRoot, PerTargetRootsReceipt(), Lock("1.2.4"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        Assert.False(
            File.Exists(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")),
            "With two legacy receipts, none may be copied to the home global store.");
        Assert.Equal(2, report.UnmigratedLegacyRoots.Count);
        Assert.Contains(
            report.UnmigratedLegacyRoots,
            entry => IsSameLocation(LegacyGlobalDirectory(scenario, firstRoot), entry.Root));
        Assert.Contains(
            report.UnmigratedLegacyRoots,
            entry => IsSameLocation(LegacyGlobalDirectory(scenario, secondRoot), entry.Root));
        Assert.All(
            report.UnmigratedLegacyRoots,
            entry => Assert.False(
                string.IsNullOrWhiteSpace(entry.Guidance),
                $"The unmigrated legacy root '{entry.Root}' must say what the operator should do next."));
    }

    /// <summary>
    /// A single-root legacy receipt whose key matches the home directory is bound to the home, so it
    /// migrates with the home as its target root. Without the key match a pre-#91 install made from home
    /// would be stranded in application data forever.
    /// </summary>
    [Fact]
    public void SingleRootLegacyReceiptMigratesWhenItsKeyIsTheHomeDirectory()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        WriteLegacyGlobal(scenario, scenario.Home, SingleRootReceipt(), Lock("1.2.3"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(
            PhysicalPathOf(scenario.Home),
            ReadTargetRoot(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")));
    }

    /// <summary>
    /// A single-root legacy receipt whose key matches the invoking directory binds to that directory. The
    /// operator running the migration from their project is the one who can vouch for which root it was.
    /// </summary>
    [Fact]
    public void SingleRootLegacyReceiptMigratesWhenItsKeyIsTheInvokingDirectory()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string project = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(project);
        WriteLegacyGlobal(scenario, project, SingleRootReceipt(), Lock("1.2.3"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: project);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(
            PhysicalPathOf(project),
            ReadTargetRoot(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")));
    }

    /// <summary>
    /// A single-root legacy receipt with no absolute target root and no key match against the home or the
    /// invoking directory binds to the registry entry whose root key is the legacy directory's key. The
    /// registry is the record of which projects this home knows, so it can name the root a receipt was written for.
    /// </summary>
    [Fact]
    public void SingleRootLegacyReceiptMigratesToTheRegistryRootWhoseKeyMatchesItsDirectory()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string registered = Path.Combine(fixture.Path, "registered-target");
        Directory.CreateDirectory(registered);
        scenario.Store.RegisterProject(registered, ["codex"], InstalledAt);
        WriteLegacyGlobal(scenario, registered, SingleRootReceipt(), Lock("1.2.3"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(
            PhysicalPathOf(registered),
            ReadTargetRoot(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")));
    }

    /// <summary>
    /// A single-root legacy receipt that matches neither the home nor the invoking directory has no root it
    /// can be bound to. Guessing one would attach ownership to the wrong place, so it stays where it is and
    /// is reported.
    /// </summary>
    [Fact]
    public void SingleRootLegacyReceiptMatchingNeitherHomeNorInvokingDirectoryIsReported()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string orphan = Path.Combine(fixture.Path, "orphan-target");
        string elsewhere = Path.Combine(fixture.Path, "elsewhere");
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(elsewhere);
        WriteLegacyGlobal(scenario, orphan, SingleRootReceipt(), Lock("1.2.3"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: elsewhere);

        Assert.Empty(report.MigratedItems);
        Assert.False(
            File.Exists(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")),
            "A single-root receipt with no resolvable binding root must not be copied to the home store.");
        SquadUnmigratedLegacyRoot entry = Assert.Single(report.UnmigratedLegacyRoots);
        Assert.True(
            IsSameLocation(LegacyGlobalDirectory(scenario, orphan), entry.Root),
            $"The report must name the legacy root that was left in place, but named '{entry.Root}'.");
        Assert.False(
            string.IsNullOrWhiteSpace(entry.Guidance),
            "An unmigrated single-root receipt must say what the operator should do next.");
    }

    /// <summary>
    /// A single-root legacy receipt that already names an absolute target root binds to that root directly,
    /// without needing a key match. This is the first and strongest rule for resolving a binding root.
    /// </summary>
    [Fact]
    public void SingleRootLegacyReceiptWithAbsoluteTargetRootMigratesToThatRoot()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "explicit-target");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, SingleRootReceipt(), Lock("1.2.3"));
        string legacyReceipt = Path.Combine(LegacyGlobalDirectory(scenario, root), "squad.receipt.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(legacyReceipt, Encoding.UTF8))!.AsObject();
        document["targetRoot"] = PhysicalPathOf(root);
        WriteFile(legacyReceipt, document.ToJsonString());

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Equal(
            PhysicalPathOf(root),
            ReadTargetRoot(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")));
    }

    /// <summary>
    /// A home global receipt is the authority once it exists, so a migration must never replace it, even when
    /// a legacy receipt disagrees. Overwriting it would silently change what the operator's global install owns.
    /// </summary>
    [Fact]
    public void ExistingHomeGlobalReceiptIsNeverOverwritten()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteFile(
            Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json"),
            scenario.Store.SerializeReceipt(GlobalReceipt(SquadReceiptLayout.PerTargetRoots, OwnedFile("agents/beta.toml", "beta"))));
        WriteFile(
            Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml"),
            scenario.Store.SerializeLock(Lock("9.9.9")));
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));
        byte[] receiptBefore = File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json"));
        byte[] lockBefore = File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        Assert.Equal(
            receiptBefore,
            File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")));
        Assert.Equal(
            lockBefore,
            File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml")));
    }

    /// <summary>
    /// Project state that lives inside the repository is imported into the home store and registered, so the
    /// registry lists the project. The in-repo files are left byte-identical, because they are still the
    /// project's own record until the operator removes them.
    /// </summary>
    [Fact]
    public void InRepoProjectStateIsImportedIntoHomeRegistryAndLeftByteIdentical()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));
        string inRepoReceipt = Path.Combine(repo, ".kyber-weave", "squad.receipt.json");
        byte[] inRepoLock = File.ReadAllBytes(Path.Combine(repo, ".kyber-weave", "squad.lock.yml"));
        string inRepoFiles = ReadFilesJson(inRepoReceipt);
        TreeSnapshot repoBefore = DirectoryTreeSnapshot.SnapshotTree(repo);

        SquadMigrationReport report = scenario.Migration.ImportProject(repo);

        Assert.Single(report.ImportedProjects);
        string projectDirectory = HomeProjectDirectory(scenario, repo);
        Assert.Equal(inRepoLock, File.ReadAllBytes(Path.Combine(projectDirectory, "squad.lock.yml")));
        Assert.Equal(PhysicalPathOf(repo), ReadTargetRoot(Path.Combine(projectDirectory, "squad.receipt.json")));
        Assert.Equal(inRepoFiles, ReadFilesJson(Path.Combine(projectDirectory, "squad.receipt.json")));
        SquadProjectRegistryEntry entry = Assert.Single(scenario.Store.ListProjects());
        Assert.Equal(PhysicalPathOf(repo), entry.Path);
        Assert.Equal(SquadPhysicalRootIdentity.Resolve(repo).Key, entry.RootKey);
        DirectoryTreeSnapshot.AssertTreeUnchanged(repo, repoBefore);
    }

    /// <summary>
    /// Once a project has home state, the home copy is authoritative and an in-repo receipt that disagrees is
    /// ignored. Importing it again would let a stale file in the repository roll the home record back.
    /// </summary>
    [Fact]
    public void HomeProjectReceiptWinsOverInRepoState()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));
        string homeProject = HomeProjectDirectory(scenario, repo);
        WriteFile(
            Path.Combine(homeProject, "squad.receipt.json"),
            scenario.Store.SerializeReceipt(ProjectReceipt(OwnedFile(".codex/agents/alpha.toml", "home-alpha"))));
        WriteFile(
            Path.Combine(homeProject, "squad.lock.yml"),
            scenario.Store.SerializeLock(Lock("9.9.9")));
        byte[] receiptBefore = File.ReadAllBytes(Path.Combine(homeProject, "squad.receipt.json"));
        byte[] lockBefore = File.ReadAllBytes(Path.Combine(homeProject, "squad.lock.yml"));
        TreeSnapshot repoBefore = DirectoryTreeSnapshot.SnapshotTree(repo);

        SquadMigrationReport report = scenario.Migration.ImportProject(repo);

        Assert.Empty(report.ImportedProjects);
        Assert.Equal(receiptBefore, File.ReadAllBytes(Path.Combine(homeProject, "squad.receipt.json")));
        Assert.Equal(lockBefore, File.ReadAllBytes(Path.Combine(homeProject, "squad.lock.yml")));
        DirectoryTreeSnapshot.AssertTreeUnchanged(repo, repoBefore);
    }

    /// <summary>
    /// Migration and import touch only Squad's own state. Other files under the repository's
    /// <c>.kyber-weave/</c> and other application data are not read for rewriting and must come out identical.
    /// </summary>
    [Fact]
    public void UnrelatedFilesInRepoAndInAppDataAreUntouchedByMigration()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));
        WriteFile(Path.Combine(repo, ".kyber-weave", "kyber-weave.yml"), "docs-root: docs\n");
        WriteFile(Path.Combine(repo, ".kyber-weave", "notes", "operator.md"), "keep me");
        string legacyRoot = Path.Combine(fixture.Path, "legacy-target");
        Directory.CreateDirectory(legacyRoot);
        WriteLegacyGlobal(scenario, legacyRoot, PerTargetRootsReceipt(), Lock("1.2.3"));
        WriteFile(Path.Combine(scenario.ApplicationData, "KyberWeave", "settings.json"), "{}");
        WriteFile(Path.Combine(scenario.ApplicationData, "other-tool", "state.txt"), "other tool state");
        TreeSnapshot repoBefore = DirectoryTreeSnapshot.SnapshotTree(repo);
        TreeSnapshot appDataBefore = DirectoryTreeSnapshot.SnapshotTree(scenario.ApplicationData);

        scenario.Migration.MigrateGlobal(invokingCwd: repo);
        scenario.Migration.ImportProject(repo);

        DirectoryTreeSnapshot.AssertTreeUnchanged(repo, repoBefore);
        DirectoryTreeSnapshot.AssertTreeUnchanged(scenario.ApplicationData, appDataBefore);
    }

    /// <summary>
    /// A dry run reports the migration that would happen and writes nothing. It would let an operator preview
    /// a move of their global state before committing to it.
    /// </summary>
    /// <remarks>
    /// The whole fixture tree is snapshotted, so a write anywhere under it (the home store, the registry, a
    /// directory created for a project, or a change to the legacy or in-repo files) fails the test. The report
    /// must still name the legacy state at its path, so the operator can see what the next run will move.
    /// </remarks>
    [Fact]
    public void DryRunReportsPendingMigrationAndWritesNothing()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));
        string legacyDirectory = LegacyGlobalDirectory(scenario, root);
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));
        TreeSnapshot fixtureBefore = DirectoryTreeSnapshot.SnapshotTree(fixture.Path);

        SquadMigrationReport globalReport = scenario.Migration.MigrateGlobal(invokingCwd: null, dryRun: true);
        SquadMigrationReport projectReport = scenario.Migration.ImportProject(repo, dryRun: true);

        Assert.Contains(
            globalReport.MigratedItems,
            item => item.Contains(legacyDirectory, StringComparison.Ordinal));
        Assert.NotEmpty(projectReport.ImportedProjects);
        Assert.False(
            Directory.Exists(Path.Combine(scenario.Home, ".kyber-weave")),
            "A dry run must not create the home state directory.");
        DirectoryTreeSnapshot.AssertTreeUnchanged(fixture.Path, fixtureBefore);
    }

    /// <summary>
    /// A crash between the lock copy and the receipt copy leaves a home lock with no home receipt. The receipt
    /// is the completion marker, so that state is an interrupted copy, not a conflict. A home lock byte-identical
    /// to the legacy lock is the copy's own write, so the next run must finish the copy rather than block on it.
    /// </summary>
    [Fact]
    public void InterruptedGlobalCopyLeavingAnIdenticalHomeLockIsCompletedByTheNextRun()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));
        byte[] legacyLock = File.ReadAllBytes(Path.Combine(LegacyGlobalDirectory(scenario, root), "squad.lock.yml"));
        Directory.CreateDirectory(HomeGlobalDirectory(scenario));
        File.WriteAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml"), legacyLock);

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(
            PhysicalPathOf(scenario.Home),
            ReadTargetRoot(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")));
        Assert.Equal(legacyLock, File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml")));
    }

    /// <summary>
    /// A home lock that differs from the legacy lock, with no home receipt, is not an interrupted copy of this
    /// legacy root. It may belong to another deployment, so the copy stays blocked and the lock is left as it is.
    /// </summary>
    [Fact]
    public void HomeLockThatDiffersFromTheLegacyLockStillBlocksTheGlobalCopy()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));
        WriteFile(Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml"), scenario.Store.SerializeLock(Lock("9.9.9")));
        byte[] lockBefore = File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        Assert.False(
            File.Exists(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")),
            "A blocked copy must not write a home receipt.");
        SquadUnmigratedLegacyRoot entry = Assert.Single(report.UnmigratedLegacyRoots);
        Assert.False(string.IsNullOrWhiteSpace(entry.Guidance), "A blocked copy must say what the operator should do next.");
        Assert.Equal(lockBefore, File.ReadAllBytes(Path.Combine(HomeGlobalDirectory(scenario), "squad.lock.yml")));
    }

    /// <summary>
    /// The same crash window in the project import: a home lock identical to the in-repo lock, with no home
    /// receipt, is an interrupted import. The next run completes it instead of warning and stopping.
    /// </summary>
    [Fact]
    public void InterruptedProjectImportLeavingAnIdenticalHomeLockIsCompletedByTheNextRun()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));
        byte[] inRepoLock = File.ReadAllBytes(Path.Combine(repo, ".kyber-weave", "squad.lock.yml"));
        string projectDirectory = HomeProjectDirectory(scenario, repo);
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllBytes(Path.Combine(projectDirectory, "squad.lock.yml"), inRepoLock);
        TreeSnapshot repoBefore = DirectoryTreeSnapshot.SnapshotTree(repo);

        SquadMigrationReport report = scenario.Migration.ImportProject(repo);

        Assert.Single(report.ImportedProjects);
        Assert.Empty(report.Warnings);
        Assert.Equal(PhysicalPathOf(repo), ReadTargetRoot(Path.Combine(projectDirectory, "squad.receipt.json")));
        Assert.Equal(inRepoLock, File.ReadAllBytes(Path.Combine(projectDirectory, "squad.lock.yml")));
        DirectoryTreeSnapshot.AssertTreeUnchanged(repo, repoBefore);
    }

    /// <summary>
    /// The project import keeps its block when the home lock differs from the in-repo lock: the home record
    /// may belong to another state, so nothing is imported and the operator is told which lock to inspect.
    /// </summary>
    [Fact]
    public void ProjectImportStillWarnsWhenTheHomeLockDiffersFromTheInRepoLock()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));
        string projectDirectory = HomeProjectDirectory(scenario, repo);
        WriteFile(Path.Combine(projectDirectory, "squad.lock.yml"), scenario.Store.SerializeLock(Lock("9.9.9")));

        SquadMigrationReport report = scenario.Migration.ImportProject(repo);

        Assert.Empty(report.ImportedProjects);
        Assert.Contains(report.Warnings, warning => warning.Contains("home lock", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(projectDirectory, "squad.receipt.json")));
    }

    /// <summary>
    /// The global copy writes under the home deployment lease that installs take. While another transaction holds
    /// that lease, the copy must write nothing and report why, and the next run after release must complete it.
    /// </summary>
    [Fact]
    public void GlobalCopyWritesNothingWhileAnotherTransactionHoldsTheHomeLease()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));
        string leaseKey = scenario.Store.ResolveLeaseIdentity(scenario.Home, SquadDeploymentScope.Global);

        SquadMigrationReport whileHeld;
        using (SquadTransaction.TransactionLease held = SquadTransaction.AcquireLease(leaseKey))
        {
            whileHeld = scenario.Migration.MigrateGlobal(invokingCwd: null);
        }

        Assert.Empty(whileHeld.MigratedItems);
        Assert.False(
            File.Exists(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")),
            "A copy that cannot take the lease must not write the home receipt.");
        Assert.Single(whileHeld.UnmigratedLegacyRoots);

        SquadMigrationReport afterRelease = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(afterRelease.MigratedItems);
        Assert.True(File.Exists(Path.Combine(HomeGlobalDirectory(scenario), "squad.receipt.json")));
    }

    /// <summary>
    /// The project import writes under the same project lease an install takes. Held by another transaction, it
    /// imports nothing and warns, and it completes once the lease is released.
    /// </summary>
    [Fact]
    public void ProjectImportWritesNothingWhileAnotherTransactionHoldsTheProjectLease()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));
        string leaseKey = scenario.Store.ResolveLeaseIdentity(repo, SquadDeploymentScope.Project);

        SquadMigrationReport whileHeld;
        using (SquadTransaction.TransactionLease held = SquadTransaction.AcquireLease(leaseKey))
        {
            whileHeld = scenario.Migration.ImportProject(repo);
        }

        Assert.Empty(whileHeld.ImportedProjects);
        Assert.Contains(whileHeld.Warnings, warning => warning.Contains("transaction", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(HomeProjectDirectory(scenario, repo), "squad.receipt.json")));

        SquadMigrationReport afterRelease = scenario.Migration.ImportProject(repo);

        Assert.Single(afterRelease.ImportedProjects);
    }

    /// <summary>
    /// A successful global copy records the legacy root's key in the migration ledger beside the home state. The
    /// ledger is what keeps a later run from copying the same legacy root again once the home copy is uninstalled.
    /// </summary>
    [Fact]
    public void SuccessfulGlobalCopyRecordsTheLegacyKeyInTheLedger()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));

        scenario.Migration.MigrateGlobal(invokingCwd: null);

        JsonNode ledger = ReadLedger(scenario);
        Assert.Equal("kyber-squad.migration-ledger/v1", ledger["schema"]!.GetValue<string>());
        Assert.Equal(new[] { SquadPhysicalRootIdentity.Resolve(root).Key }, ReadStrings(ledger, "globalMigratedFrom"));
        Assert.Empty(ReadStrings(ledger, "projectsImported"));
        Assert.Empty(TemporaryFilesBeside(LedgerPath(scenario)));
    }

    /// <summary>
    /// A successful project import records the root key in the same ledger, so a later run cannot import the
    /// in-repo state a second time after the project's home state is uninstalled.
    /// </summary>
    [Fact]
    public void SuccessfulProjectImportRecordsTheRootKeyInTheLedger()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));

        scenario.Migration.ImportProject(repo);

        JsonNode ledger = ReadLedger(scenario);
        Assert.Equal(new[] { SquadPhysicalRootIdentity.Resolve(repo).Key }, ReadStrings(ledger, "projectsImported"));
        Assert.Empty(ReadStrings(ledger, "globalMigratedFrom"));
    }

    /// <summary>
    /// A dry run reports what it would do and must leave no ledger behind, because the ledger records work that
    /// was actually done.
    /// </summary>
    [Fact]
    public void DryRunNeverWritesTheLedger()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));

        scenario.Migration.MigrateGlobal(invokingCwd: null, dryRun: true);
        scenario.Migration.ImportProject(repo, dryRun: true);

        Assert.False(File.Exists(LedgerPath(scenario)), "A dry run must not write the migration ledger.");
    }

    /// <summary>
    /// A global copy whose legacy root is already in the ledger is not copied again, even though the home receipt
    /// is gone. The legacy root is still reported, so doctor can say it is on disk and no longer read.
    /// </summary>
    [Fact]
    public void GlobalCopyIsNotRepeatedAfterTheHomeGlobalReceiptIsRemoved()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));
        scenario.Migration.MigrateGlobal(invokingCwd: null);
        string homeDirectory = HomeGlobalDirectory(scenario);
        File.Delete(Path.Combine(homeDirectory, "squad.receipt.json"));
        File.Delete(Path.Combine(homeDirectory, "squad.lock.yml"));

        SquadMigrationReport again = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(again.MigratedItems);
        Assert.False(
            File.Exists(Path.Combine(homeDirectory, "squad.receipt.json")),
            "A migrated and then uninstalled global receipt must not come back from the legacy copy.");
        Assert.False(
            File.Exists(Path.Combine(homeDirectory, "squad.lock.yml")),
            "A migrated and then uninstalled global lock must not come back from the legacy copy.");
        Assert.Single(again.UnmigratedLegacyRoots);
    }

    /// <summary>
    /// A project import whose root is in the ledger is not repeated after the home project receipt is removed,
    /// so the in-repo state the operator kept cannot resurrect an uninstalled project.
    /// </summary>
    [Fact]
    public void ProjectImportIsNotRepeatedAfterTheHomeProjectReceiptIsRemoved()
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));
        scenario.Migration.ImportProject(repo);
        string projectDirectory = HomeProjectDirectory(scenario, repo);
        File.Delete(Path.Combine(projectDirectory, "squad.receipt.json"));
        File.Delete(Path.Combine(projectDirectory, "squad.lock.yml"));

        SquadMigrationReport again = scenario.Migration.ImportProject(repo);

        Assert.Empty(again.ImportedProjects);
        Assert.False(
            File.Exists(Path.Combine(projectDirectory, "squad.receipt.json")),
            "An imported and then uninstalled project receipt must not come back from the in-repo copy.");
    }

    /// <summary>
    /// A ledger that does not parse, or does not match its schema, is treated as empty and reported. It must never
    /// crash a command, and the next successful write replaces it with a valid ledger.
    /// </summary>
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("{\"schema\":\"kyber-squad.migration-ledger/v0\",\"globalMigratedFrom\":[],\"projectsImported\":[]}")]
    [InlineData("{\"schema\":\"kyber-squad.migration-ledger/v1\",\"globalMigratedFrom\":\"not-a-list\",\"projectsImported\":[]}")]
    public void MalformedLedgerIsTreatedAsEmptyWithAWarningAndNeverCrashes(string ledgerText)
    {
        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string root = Path.Combine(fixture.Path, "customer-alpha");
        Directory.CreateDirectory(root);
        WriteLegacyGlobal(scenario, root, PerTargetRootsReceipt(), Lock("1.2.3"));
        string repo = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(repo);
        WriteInRepoState(scenario, repo, Lock("1.2.3"));
        WriteFile(LedgerPath(scenario), ledgerText);

        SquadMigrationReport globalReport = scenario.Migration.MigrateGlobal(invokingCwd: null);
        SquadMigrationReport projectReport = scenario.Migration.ImportProject(repo);

        Assert.NotEmpty(globalReport.MigratedItems);
        Assert.Single(projectReport.ImportedProjects);
        Assert.Contains(
            globalReport.Warnings.Concat(projectReport.Warnings),
            warning => warning.Contains("migration ledger", StringComparison.OrdinalIgnoreCase));
        JsonNode ledger = ReadLedger(scenario);
        Assert.Equal(new[] { SquadPhysicalRootIdentity.Resolve(root).Key }, ReadStrings(ledger, "globalMigratedFrom"));
        Assert.Equal(new[] { SquadPhysicalRootIdentity.Resolve(repo).Key }, ReadStrings(ledger, "projectsImported"));
    }

    /// <summary>
    /// A ledger the process cannot open is the same situation as one it cannot parse: it is read as empty with a
    /// reason, so the migration reports it as a warning. An access denial must not escape as an unhandled exception.
    /// </summary>
    [Fact]
    public void LedgerThatCannotBeOpenedIsReadAsEmptyWithAReason()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempDirectory fixture = new TempDirectory();
        MigrationScenario scenario = CreateScenario(fixture);
        string path = LedgerPath(scenario);
        WriteFile(path, "{\"schema\":\"kyber-squad.migration-ledger/v1\",\"globalMigratedFrom\":[],\"projectsImported\":[]}");
        UnixFileMode original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            SquadMigrationLedger ledger = SquadMigrationLedger.Read(path, out string? unreadableReason);

            Assert.NotNull(unreadableReason);
            Assert.Empty(ledger.GlobalMigratedFrom);
            Assert.Empty(ledger.ProjectsImported);
        }
        finally
        {
            File.SetUnixFileMode(path, original);
        }
    }

    private sealed record MigrationScenario(
        string Home,
        string ApplicationData,
        SquadStateStore Store,
        SquadStateMigration Migration);

    private sealed class FixtureUserPaths(string homeDirectory, string applicationDataDirectory) : ISquadUserPaths
    {
        public string HomeDirectory { get; } = homeDirectory;

        public string ApplicationDataDirectory { get; } = applicationDataDirectory;
    }

    private static MigrationScenario CreateScenario(TempDirectory fixture)
    {
        string home = Path.Combine(fixture.Path, "home");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(home);
        FixtureUserPaths userPaths = new(home, applicationData);
        SquadStateStore store = new(userPaths);
        return new MigrationScenario(home, applicationData, store, new SquadStateMigration(store, userPaths));
    }

    private static string LegacyGlobalDirectory(MigrationScenario scenario, string root) =>
        Path.Combine(
            scenario.ApplicationData,
            "KyberWeave",
            "squad",
            "roots",
            SquadPhysicalRootIdentity.Resolve(root).Key);

    private static string HomeGlobalDirectory(MigrationScenario scenario) =>
        Path.Combine(PhysicalPathOf(scenario.Home), ".kyber-weave", "squad", "global");

    private static string HomeProjectDirectory(MigrationScenario scenario, string root) =>
        Path.Combine(
            PhysicalPathOf(scenario.Home),
            ".kyber-weave",
            "squad",
            "projects",
            SquadPhysicalRootIdentity.Resolve(root).Key);

    private static void WriteLegacyGlobal(
        MigrationScenario scenario,
        string root,
        SquadReceipt receipt,
        SquadLock squadLock)
    {
        string directory = LegacyGlobalDirectory(scenario, root);
        WriteFile(Path.Combine(directory, "squad.receipt.json"), scenario.Store.SerializeReceipt(receipt));
        WriteFile(Path.Combine(directory, "squad.lock.yml"), scenario.Store.SerializeLock(squadLock));
    }

    /// <summary>
    /// Writes the in-repo project state a pre-home install left behind: the receipt and lock under
    /// <c>.kyber-weave/</c>, and the one harness file the receipt owns.
    /// </summary>
    private static void WriteInRepoState(MigrationScenario scenario, string repo, SquadLock squadLock)
    {
        WriteFile(
            Path.Combine(repo, ".kyber-weave", "squad.lock.yml"),
            scenario.Store.SerializeLock(squadLock));
        WriteFile(
            Path.Combine(repo, ".kyber-weave", "squad.receipt.json"),
            scenario.Store.SerializeReceipt(ProjectReceipt(OwnedFile(".codex/agents/alpha.toml", "alpha"))));
        WriteFile(Path.Combine(repo, ".codex", "agents", "alpha.toml"), "alpha");
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>
    /// A global receipt whose owned files use bare, per-target paths, which is the layout every install has
    /// written since #91. The target is codex, so the file lives beneath codex's own global root.
    /// </summary>
    private static SquadReceipt PerTargetRootsReceipt() =>
        GlobalReceipt(SquadReceiptLayout.PerTargetRoots, OwnedFile("agents/alpha.toml", "alpha"));

    /// <summary>
    /// A global receipt whose owned file uses the legacy prefixed path, which is how a pre-#91 single-root
    /// install recorded it. The prefix is what makes the layout classify as single-root.
    /// </summary>
    private static SquadReceipt SingleRootReceipt() =>
        GlobalReceipt(SquadReceiptLayout.SingleRoot, OwnedFile(".codex/agents/alpha.toml", "alpha"));

    private static SquadReceipt GlobalReceipt(SquadReceiptLayout layout, params SquadOwnedFile[] files) =>
        new(
            SquadStateStore.ReceiptSchemaV1,
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            files)
        {
            Layout = layout
        };

    private static SquadReceipt ProjectReceipt(params SquadOwnedFile[] files) =>
        new(
            SquadStateStore.ReceiptSchemaV1,
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [],
            files);

    private static SquadOwnedFile OwnedFile(string relativePath, string content) =>
        new(relativePath, Digest(content), "codex", Adopted: false);

    private static SquadLock Lock(string version = "1.2.3") =>
        new(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["codex", "warp"],
            ["cursor"],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string ReadTargetRoot(string receiptPath) =>
        JsonNode.Parse(File.ReadAllText(receiptPath, Encoding.UTF8))!.AsObject()["targetRoot"]!.GetValue<string>();

    private static string LedgerPath(MigrationScenario scenario) =>
        Path.Combine(PhysicalPathOf(scenario.Home), ".kyber-weave", "squad", "migration-ledger.json");

    private static JsonNode ReadLedger(MigrationScenario scenario) =>
        JsonNode.Parse(File.ReadAllText(LedgerPath(scenario), Encoding.UTF8))!;

    private static string[] ReadStrings(JsonNode ledger, string property) =>
        ledger[property]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();

    private static string[] TemporaryFilesBeside(string path) =>
        Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").ToArray();

    private static string ReadFilesJson(string receiptPath) =>
        JsonNode.Parse(File.ReadAllText(receiptPath, Encoding.UTF8))!.AsObject()["files"]!.ToJsonString();

    private static string PhysicalPathOf(string path) =>
        SquadPhysicalRootIdentity.Resolve(path).PhysicalPath;

    /// <summary>
    /// Compares two locations by where they physically are. A temp path can be spelled through a symlinked
    /// root (<c>/tmp</c> resolving to <c>/private/tmp</c>) and still name one place, so the comparison is on
    /// the canonical form, not the spelling.
    /// </summary>
    private static bool IsSameLocation(string expected, string actual) =>
        CanonicalLocation(expected) == CanonicalLocation(actual);

    private static string CanonicalLocation(string path)
    {
        string existing = Path.GetFullPath(path);
        List<string> unresolved = [];
        while (!Directory.Exists(existing))
        {
            unresolved.Insert(0, Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing)!;
        }

        return unresolved.Aggregate(
            PhysicalPathOf(existing),
            (accumulated, part) => Path.Combine(accumulated, part));
    }
}
