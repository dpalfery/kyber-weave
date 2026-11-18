using System.Text.Json.Nodes;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the one-time move of legacy Squad state into the home-level layout, and the import of an
/// in-repo project's state into the home registry. Each rule guards a way a migration could silently
/// lose, overwrite or relocate an operator's ownership record.
/// </summary>
/// <remarks>
/// Every fixture runs over an in-memory file system whose home and application data directories sit in it,
/// so no test touches a disk, a mutex or the wall clock. Receipts and locks are
/// written through the store's own serializers so the fixtures carry the shapes the reader accepts.
/// A legacy receipt carries the <c>"."</c> target root the store still writes, and migrated receipts
/// are read back as raw JSON, so these tests do not depend on how the reader validates a rewritten root.
/// </remarks>
public sealed class SquadStateMigrationTests
{
    private readonly InMemorySquadState _state = new();

    private InMemoryFileSystem Files => _state.Files;

    /// <summary>
    /// A legacy per-target-roots receipt is copied into the home global store, and its target root is
    /// rewritten to the home anchor. A per-target-roots receipt is anchored on the home, not on the root
    /// that happened to install it, so keeping the install root would tie the global state back to one
    /// project. Before this, the global state stayed in the per-user application data directory.
    /// </summary>
    [Fact]
    public void LegacyPerTargetRootsReceiptCopiesToHomeGlobalWithHomeAnchorTargetRoot()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        SquadLock legacyLock = SquadStateTestLayout.Lock("1.2.3");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), legacyLock);
        string legacyDirectory = Layout.LegacyGlobalDirectory(root);

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(
            Files.ReadAllBytes(Path.Combine(legacyDirectory, "squad.lock.yml")),
            Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml")));
        Assert.Equal(
            Layout.PhysicalOf(scenario.Home),
            Layout.ReadTargetRoot(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
    }

    /// <summary>
    /// The legacy receipt and lock are left exactly as they were. A migration that moved them, instead of
    /// copying them, would strand any older CLI that still reads the per-user location during a rollback.
    /// </summary>
    [Fact]
    public void LegacyGlobalFilesAreByteIdenticalAfterMigration()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        string legacyDirectory = Layout.LegacyGlobalDirectory(root);
        IReadOnlyDictionary<string, SquadTreeEntry> before = Snapshot(legacyDirectory);

        scenario.Migration.MigrateGlobal(invokingCwd: null);

        AssertTreeUnchanged(legacyDirectory, before);
    }

    /// <summary>
    /// A second migration after a successful first one changes nothing. The home copy already exists, so
    /// the run is a no-op, and a re-run must not rewrite a receipt the operator may have since verified.
    /// </summary>
    [Fact]
    public void SecondMigrationRunIsANoOpThatLeavesTheHomeCopyUnchanged()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        scenario.Migration.MigrateGlobal(invokingCwd: null);
        byte[] receiptAfterFirstRun = Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json"));
        byte[] lockAfterFirstRun = Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml"));

        SquadMigrationReport secondRun = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(secondRun.MigratedItems);
        Assert.Equal(
            receiptAfterFirstRun,
            Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
        Assert.Equal(
            lockAfterFirstRun,
            Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml")));
    }

    /// <summary>
    /// An empty directory under <c>roots/</c> is not a legacy receipt. Counting it would turn one real
    /// receipt into an ambiguous set of two and block a migration that is perfectly safe to run.
    /// </summary>
    [Fact]
    public void EmptyLegacyRootDirectoriesAreIgnoredWhenCountingLegacyReceipts()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Files.CreateDirectory(Path.Combine(scenario.ApplicationData, "KyberWeave", "squad", "roots", "0f3a-empty"));
        Files.CreateDirectory(Path.Combine(scenario.ApplicationData, "KyberWeave", "squad", "roots", "1b2c-empty"));
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.True(
            Files.FileExists(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")),
            "The one real legacy receipt must migrate even though empty root directories sit beside it.");
    }

    /// <summary>
    /// Empty legacy root directories on their own are nothing to migrate and nothing to report. Reporting
    /// them as unmigrated would send an operator to fix state that does not exist.
    /// </summary>
    [Fact]
    public void EmptyLegacyRootDirectoriesAloneMigrateNothingAndReportNothing()
    {
        MigrationScenario scenario = CreateScenario();
        Files.CreateDirectory(Path.Combine(scenario.ApplicationData, "KyberWeave", "squad", "roots", "0f3a-empty"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.False(
            Files.FileExists(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")),
            "No home global receipt may be written when there is no legacy receipt to copy.");
    }

    /// <summary>
    /// Two non-empty legacy receipts are ambiguous: copying either one could hand the home store the wrong
    /// ownership boundary. So neither moves, and each is reported with guidance an operator can act on.
    /// </summary>
    [Fact]
    public void TwoNonEmptyLegacyGlobalReceiptsMigrateNoneAndReportEachWithGuidance()
    {
        MigrationScenario scenario = CreateScenario();
        string firstRoot = _state.CreateRoot("alpha-target");
        string secondRoot = _state.CreateRoot("beta-target");
        Layout.WriteLegacyGlobal(firstRoot, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        Layout.WriteLegacyGlobal(secondRoot, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.4"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        Assert.False(
            Files.FileExists(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")),
            "With two legacy receipts, none may be copied to the home global store.");
        Assert.Equal(2, report.UnmigratedLegacyRoots.Count);
        Assert.Contains(
            report.UnmigratedLegacyRoots,
            entry => IsSameLocation(Layout.LegacyGlobalDirectory(firstRoot), entry.Root));
        Assert.Contains(
            report.UnmigratedLegacyRoots,
            entry => IsSameLocation(Layout.LegacyGlobalDirectory(secondRoot), entry.Root));
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
        MigrationScenario scenario = CreateScenario();
        Layout.WriteLegacyGlobal(scenario.Home, SquadStateTestLayout.SingleRootReceipt(), SquadStateTestLayout.Lock("1.2.3"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(
            Layout.PhysicalOf(scenario.Home),
            Layout.ReadTargetRoot(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
    }

    /// <summary>
    /// A single-root legacy receipt whose key matches the invoking directory binds to that directory. The
    /// operator running the migration from their project is the one who can vouch for which root it was.
    /// </summary>
    [Fact]
    public void SingleRootLegacyReceiptMigratesWhenItsKeyIsTheInvokingDirectory()
    {
        MigrationScenario scenario = CreateScenario();
        string project = _state.CreateRoot("project");
        Layout.WriteLegacyGlobal(project, SquadStateTestLayout.SingleRootReceipt(), SquadStateTestLayout.Lock("1.2.3"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: project);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(
            Layout.PhysicalOf(project),
            Layout.ReadTargetRoot(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
    }

    /// <summary>
    /// A single-root legacy receipt with no absolute target root and no key match against the home or the
    /// invoking directory binds to the registry entry whose root key is the legacy directory's key. The
    /// registry is the record of which projects this home knows, so it can name the root a receipt was written for.
    /// </summary>
    [Fact]
    public void SingleRootLegacyReceiptMigratesToTheRegistryRootWhoseKeyMatchesItsDirectory()
    {
        MigrationScenario scenario = CreateScenario();
        string registered = _state.CreateRoot("registered-target");
        scenario.Store.RegisterProject(registered, ["codex"], SquadStateTestLayout.InstalledAt);
        Layout.WriteLegacyGlobal(registered, SquadStateTestLayout.SingleRootReceipt(), SquadStateTestLayout.Lock("1.2.3"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(
            Layout.PhysicalOf(registered),
            Layout.ReadTargetRoot(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
    }

    /// <summary>
    /// A single-root legacy receipt that matches neither the home nor the invoking directory has no root it
    /// can be bound to. Guessing one would attach ownership to the wrong place, so it stays where it is and
    /// is reported.
    /// </summary>
    [Fact]
    public void SingleRootLegacyReceiptMatchingNeitherHomeNorInvokingDirectoryIsReported()
    {
        MigrationScenario scenario = CreateScenario();
        string orphan = _state.CreateRoot("orphan-target");
        string elsewhere = _state.CreateRoot("elsewhere");
        Layout.WriteLegacyGlobal(orphan, SquadStateTestLayout.SingleRootReceipt(), SquadStateTestLayout.Lock("1.2.3"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: elsewhere);

        Assert.Empty(report.MigratedItems);
        Assert.False(
            Files.FileExists(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")),
            "A single-root receipt with no resolvable binding root must not be copied to the home store.");
        SquadUnmigratedLegacyRoot entry = Assert.Single(report.UnmigratedLegacyRoots);
        Assert.True(
            IsSameLocation(Layout.LegacyGlobalDirectory(orphan), entry.Root),
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
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("explicit-target");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.SingleRootReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        string legacyReceipt = Path.Combine(Layout.LegacyGlobalDirectory(root), "squad.receipt.json");
        JsonObject document = JsonNode.Parse(Files.ReadAllText(legacyReceipt))!.AsObject();
        document["targetRoot"] = Layout.PhysicalOf(root);
        Layout.WriteText(legacyReceipt, document.ToJsonString());

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Equal(
            Layout.PhysicalOf(root),
            Layout.ReadTargetRoot(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
    }

    /// <summary>
    /// A home global receipt is the authority once it exists, so a migration must never replace it, even when
    /// a legacy receipt disagrees. Overwriting it would silently change what the operator's global install owns.
    /// </summary>
    [Fact]
    public void ExistingHomeGlobalReceiptIsNeverOverwritten()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteText(
            Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json"),
            scenario.Store.SerializeReceipt(SquadStateTestLayout.GlobalReceipt(SquadReceiptLayout.PerTargetRoots, SquadStateTestLayout.OwnedFile("agents/beta.toml", "beta"))));
        Layout.WriteText(
            Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml"),
            scenario.Store.SerializeLock(SquadStateTestLayout.Lock("9.9.9")));
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        byte[] receiptBefore = Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json"));
        byte[] lockBefore = Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        Assert.Equal(
            receiptBefore,
            Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
        Assert.Equal(
            lockBefore,
            Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml")));
    }

    /// <summary>
    /// Project state that lives inside the repository is imported into the home store and registered, so the
    /// registry lists the project. The in-repo files are left byte-identical, because they are still the
    /// project's own record until the operator removes them.
    /// </summary>
    [Fact]
    public void InRepoProjectStateIsImportedIntoHomeRegistryAndLeftByteIdentical()
    {
        MigrationScenario scenario = CreateScenario();
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));
        string inRepoReceipt = Path.Combine(repo, ".kyber-weave", "squad.receipt.json");
        byte[] inRepoLock = Files.ReadAllBytes(Path.Combine(repo, ".kyber-weave", "squad.lock.yml"));
        string inRepoFiles = ReadFilesJson(inRepoReceipt);
        IReadOnlyDictionary<string, SquadTreeEntry> repoBefore = Snapshot(repo);

        SquadMigrationReport report = scenario.Migration.ImportProject(repo);

        Assert.Single(report.ImportedProjects);
        string projectDirectory = Layout.HomeProjectDirectory(repo);
        Assert.Equal(inRepoLock, Files.ReadAllBytes(Path.Combine(projectDirectory, "squad.lock.yml")));
        Assert.Equal(Layout.PhysicalOf(repo), Layout.ReadTargetRoot(Path.Combine(projectDirectory, "squad.receipt.json")));
        Assert.Equal(inRepoFiles, ReadFilesJson(Path.Combine(projectDirectory, "squad.receipt.json")));
        SquadProjectRegistryEntry entry = Assert.Single(scenario.Store.ListProjects());
        Assert.Equal(Layout.PhysicalOf(repo), entry.Path);
        Assert.Equal(Layout.KeyOf(repo), entry.RootKey);
        AssertTreeUnchanged(repo, repoBefore);
    }

    /// <summary>
    /// Once a project has home state, the home copy is authoritative and an in-repo receipt that disagrees is
    /// ignored. Importing it again would let a stale file in the repository roll the home record back.
    /// </summary>
    [Fact]
    public void HomeProjectReceiptWinsOverInRepoState()
    {
        MigrationScenario scenario = CreateScenario();
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));
        string homeProject = Layout.HomeProjectDirectory(repo);
        Layout.WriteText(
            Path.Combine(homeProject, "squad.receipt.json"),
            scenario.Store.SerializeReceipt(SquadStateTestLayout.ProjectReceipt(SquadStateTestLayout.OwnedFile(".codex/agents/alpha.toml", "home-alpha"))));
        Layout.WriteText(
            Path.Combine(homeProject, "squad.lock.yml"),
            scenario.Store.SerializeLock(SquadStateTestLayout.Lock("9.9.9")));
        byte[] receiptBefore = Files.ReadAllBytes(Path.Combine(homeProject, "squad.receipt.json"));
        byte[] lockBefore = Files.ReadAllBytes(Path.Combine(homeProject, "squad.lock.yml"));
        IReadOnlyDictionary<string, SquadTreeEntry> repoBefore = Snapshot(repo);

        SquadMigrationReport report = scenario.Migration.ImportProject(repo);

        Assert.Empty(report.ImportedProjects);
        Assert.Equal(receiptBefore, Files.ReadAllBytes(Path.Combine(homeProject, "squad.receipt.json")));
        Assert.Equal(lockBefore, Files.ReadAllBytes(Path.Combine(homeProject, "squad.lock.yml")));
        AssertTreeUnchanged(repo, repoBefore);
    }

    /// <summary>
    /// Migration and import touch only Squad's own state. Other files under the repository's
    /// <c>.kyber-weave/</c> and other application data are not read for rewriting and must come out identical.
    /// </summary>
    [Fact]
    public void UnrelatedFilesInRepoAndInAppDataAreUntouchedByMigration()
    {
        MigrationScenario scenario = CreateScenario();
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));
        Layout.WriteText(Path.Combine(repo, ".kyber-weave", "kyber-weave.yml"), "docs-root: docs\n");
        Layout.WriteText(Path.Combine(repo, ".kyber-weave", "notes", "operator.md"), "keep me");
        string legacyRoot = _state.CreateRoot("legacy-target");
        Layout.WriteLegacyGlobal(legacyRoot, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        Layout.WriteText(Path.Combine(scenario.ApplicationData, "KyberWeave", "settings.json"), "{}");
        Layout.WriteText(Path.Combine(scenario.ApplicationData, "other-tool", "state.txt"), "other tool state");
        IReadOnlyDictionary<string, SquadTreeEntry> repoBefore = Snapshot(repo);
        IReadOnlyDictionary<string, SquadTreeEntry> appDataBefore = Snapshot(scenario.ApplicationData);

        scenario.Migration.MigrateGlobal(invokingCwd: repo);
        scenario.Migration.ImportProject(repo);

        AssertTreeUnchanged(repo, repoBefore);
        AssertTreeUnchanged(scenario.ApplicationData, appDataBefore);
    }

    /// <summary>
    /// A dry run reports the migration that would happen and writes nothing. It would let an operator preview
    /// a move of their global state before committing to it.
    /// </summary>
    /// <remarks>
    /// The whole in-memory tree is snapshotted, so a write anywhere under it (the home store, the registry, a
    /// directory created for a project, or a change to the legacy or in-repo files) fails the test. The report
    /// must still name the legacy state at its path, so the operator can see what the next run will move.
    /// </remarks>
    [Fact]
    public void DryRunReportsPendingMigrationAndWritesNothing()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        string legacyDirectory = Layout.LegacyGlobalDirectory(root);
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));
        IReadOnlyDictionary<string, SquadTreeEntry> fixtureBefore = _state.Capture();

        SquadMigrationReport globalReport = scenario.Migration.MigrateGlobal(invokingCwd: null, dryRun: true);
        SquadMigrationReport projectReport = scenario.Migration.ImportProject(repo, dryRun: true);

        Assert.Contains(
            globalReport.MigratedItems,
            item => item.Contains(legacyDirectory, StringComparison.Ordinal));
        Assert.NotEmpty(projectReport.ImportedProjects);
        Assert.False(
            Files.DirectoryExists(Path.Combine(scenario.Home, ".kyber-weave")),
            "A dry run must not create the home state directory.");
        Assert.Null(SquadTreeComparison.Difference(fixtureBefore, _state.Capture()));
    }

    /// <summary>
    /// A crash between the lock copy and the receipt copy leaves a home lock with no home receipt. The receipt
    /// is the completion marker, so that state is an interrupted copy, not a conflict. A home lock byte-identical
    /// to the legacy lock is the copy's own write, so the next run must finish the copy rather than block on it.
    /// </summary>
    [Fact]
    public void InterruptedGlobalCopyLeavingAnIdenticalHomeLockIsCompletedByTheNextRun()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        byte[] legacyLock = Files.ReadAllBytes(Path.Combine(Layout.LegacyGlobalDirectory(root), "squad.lock.yml"));
        Files.CreateDirectory(Layout.HomeGlobalDirectory());
        Files.WriteAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml"), legacyLock);

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(
            Layout.PhysicalOf(scenario.Home),
            Layout.ReadTargetRoot(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
        Assert.Equal(legacyLock, Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml")));
    }

    /// <summary>
    /// A home lock that differs from the legacy lock, with no home receipt, is not an interrupted copy of this
    /// legacy root. It may belong to another deployment, so the copy stays blocked and the lock is left as it is.
    /// </summary>
    [Fact]
    public void HomeLockThatDiffersFromTheLegacyLockStillBlocksTheGlobalCopy()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        Layout.WriteText(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml"), scenario.Store.SerializeLock(SquadStateTestLayout.Lock("9.9.9")));
        byte[] lockBefore = Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml"));

        SquadMigrationReport report = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        Assert.False(
            Files.FileExists(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")),
            "A blocked copy must not write a home receipt.");
        SquadUnmigratedLegacyRoot entry = Assert.Single(report.UnmigratedLegacyRoots);
        Assert.False(string.IsNullOrWhiteSpace(entry.Guidance), "A blocked copy must say what the operator should do next.");
        Assert.Equal(lockBefore, Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml")));
    }

    /// <summary>
    /// The same crash window in the project import: a home lock identical to the in-repo lock, with no home
    /// receipt, is an interrupted import. The next run completes it instead of warning and stopping.
    /// </summary>
    [Fact]
    public void InterruptedProjectImportLeavingAnIdenticalHomeLockIsCompletedByTheNextRun()
    {
        MigrationScenario scenario = CreateScenario();
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));
        byte[] inRepoLock = Files.ReadAllBytes(Path.Combine(repo, ".kyber-weave", "squad.lock.yml"));
        string projectDirectory = Layout.HomeProjectDirectory(repo);
        Files.CreateDirectory(projectDirectory);
        Files.WriteAllBytes(Path.Combine(projectDirectory, "squad.lock.yml"), inRepoLock);
        IReadOnlyDictionary<string, SquadTreeEntry> repoBefore = Snapshot(repo);

        SquadMigrationReport report = scenario.Migration.ImportProject(repo);

        Assert.Single(report.ImportedProjects);
        Assert.Empty(report.Warnings);
        Assert.Equal(Layout.PhysicalOf(repo), Layout.ReadTargetRoot(Path.Combine(projectDirectory, "squad.receipt.json")));
        Assert.Equal(inRepoLock, Files.ReadAllBytes(Path.Combine(projectDirectory, "squad.lock.yml")));
        AssertTreeUnchanged(repo, repoBefore);
    }

    /// <summary>
    /// The project import keeps its block when the home lock differs from the in-repo lock: the home record
    /// may belong to another state, so nothing is imported and the operator is told which lock to inspect.
    /// </summary>
    [Fact]
    public void ProjectImportStillWarnsWhenTheHomeLockDiffersFromTheInRepoLock()
    {
        MigrationScenario scenario = CreateScenario();
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));
        string projectDirectory = Layout.HomeProjectDirectory(repo);
        Layout.WriteText(Path.Combine(projectDirectory, "squad.lock.yml"), scenario.Store.SerializeLock(SquadStateTestLayout.Lock("9.9.9")));

        SquadMigrationReport report = scenario.Migration.ImportProject(repo);

        Assert.Empty(report.ImportedProjects);
        Assert.Contains(report.Warnings, warning => warning.Contains("home lock", StringComparison.Ordinal));
        Assert.False(Files.FileExists(Path.Combine(projectDirectory, "squad.receipt.json")));
    }

    /// <summary>
    /// The global copy writes under the home deployment lease that installs take. While another transaction holds
    /// that lease, the copy must write nothing and report why, and the next run after release must complete it.
    /// </summary>
    [Fact]
    public void GlobalCopyWritesNothingWhileAnotherTransactionHoldsTheHomeLease()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        string leaseKey = scenario.Store.ResolveLeaseIdentity(scenario.Home, SquadDeploymentScope.Global);

        SquadMigrationReport whileHeld;
        using (IDisposable held = new InProcessLease(_state.LeaseHost).Hold(SquadLeaseNames.ForRoot(leaseKey)))
        {
            whileHeld = scenario.Migration.MigrateGlobal(invokingCwd: null);
        }

        Assert.Empty(whileHeld.MigratedItems);
        Assert.False(
            Files.FileExists(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")),
            "A copy that cannot take the lease must not write the home receipt.");
        Assert.Single(whileHeld.UnmigratedLegacyRoots);

        SquadMigrationReport afterRelease = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(afterRelease.MigratedItems);
        Assert.True(Files.FileExists(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
    }

    /// <summary>
    /// The lease the migration takes and the lease a transaction takes are the same operating-system mutex, so a
    /// transaction refuses to start while the migration's lease is held: the other direction of the interlock.
    /// </summary>
    [Fact]
    public void ATransactionIsRefusedWhileTheMigrationLeaseIsHeld()
    {
        string leaseKey = _state.Store.ResolveLeaseIdentity(_state.Store.ResolveHomeRoot(), SquadDeploymentScope.Global);
        string leaseName = SquadLeaseNames.ForRoot(leaseKey);
        InProcessLease migrationLease = new(_state.LeaseHost);
        InProcessLease transactionLease = new(_state.LeaseHost);

        using (migrationLease.Acquire(leaseName, TimeSpan.Zero))
        {
            _ = Assert.ThrowsAny<InvalidOperationException>(() => transactionLease.Acquire(leaseName, TimeSpan.Zero));
        }

        using IDisposable afterRelease = transactionLease.Acquire(leaseName, TimeSpan.Zero);
    }

    /// <summary>
    /// The project import writes under the same project lease an install takes. Held by another transaction, it
    /// imports nothing and warns, and it completes once the lease is released.
    /// </summary>
    [Fact]
    public void ProjectImportWritesNothingWhileAnotherTransactionHoldsTheProjectLease()
    {
        MigrationScenario scenario = CreateScenario();
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));
        string leaseKey = scenario.Store.ResolveLeaseIdentity(repo, SquadDeploymentScope.Project);

        SquadMigrationReport whileHeld;
        using (IDisposable held = new InProcessLease(_state.LeaseHost).Hold(SquadLeaseNames.ForRoot(leaseKey)))
        {
            whileHeld = scenario.Migration.ImportProject(repo);
        }

        Assert.Empty(whileHeld.ImportedProjects);
        Assert.Contains(whileHeld.Warnings, warning => warning.Contains("transaction", StringComparison.OrdinalIgnoreCase));
        Assert.False(Files.FileExists(Path.Combine(Layout.HomeProjectDirectory(repo), "squad.receipt.json")));

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
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));

        scenario.Migration.MigrateGlobal(invokingCwd: null);

        JsonNode ledger = ReadLedger();
        Assert.Equal("kyber-squad.migration-ledger/v1", ledger["schema"]!.GetValue<string>());
        Assert.Equal(new[] { Layout.KeyOf(root) }, ReadStrings(ledger, "globalMigratedFrom"));
        Assert.Empty(ReadStrings(ledger, "projectsImported"));
        Assert.Empty(TemporaryFilesBeside(Layout.LedgerPath()));
    }

    /// <summary>
    /// A successful project import records the root key in the same ledger, so a later run cannot import the
    /// in-repo state a second time after the project's home state is uninstalled.
    /// </summary>
    [Fact]
    public void SuccessfulProjectImportRecordsTheRootKeyInTheLedger()
    {
        MigrationScenario scenario = CreateScenario();
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));

        scenario.Migration.ImportProject(repo);

        JsonNode ledger = ReadLedger();
        Assert.Equal(new[] { Layout.KeyOf(repo) }, ReadStrings(ledger, "projectsImported"));
        Assert.Empty(ReadStrings(ledger, "globalMigratedFrom"));
    }

    /// <summary>
    /// A dry run reports what it would do and must leave no ledger behind, because the ledger records work that
    /// was actually done.
    /// </summary>
    [Fact]
    public void DryRunNeverWritesTheLedger()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));

        scenario.Migration.MigrateGlobal(invokingCwd: null, dryRun: true);
        scenario.Migration.ImportProject(repo, dryRun: true);

        Assert.False(Files.FileExists(Layout.LedgerPath()), "A dry run must not write the migration ledger.");
    }

    /// <summary>
    /// A global copy whose legacy root is already in the ledger is not copied again, even though the home receipt
    /// is gone. The legacy root is still reported, so doctor can say it is on disk and no longer read.
    /// </summary>
    [Fact]
    public void GlobalCopyIsNotRepeatedAfterTheHomeGlobalReceiptIsRemoved()
    {
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        scenario.Migration.MigrateGlobal(invokingCwd: null);
        string homeDirectory = Layout.HomeGlobalDirectory();
        Files.DeleteFile(Path.Combine(homeDirectory, "squad.receipt.json"));
        Files.DeleteFile(Path.Combine(homeDirectory, "squad.lock.yml"));

        SquadMigrationReport again = scenario.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(again.MigratedItems);
        Assert.False(
            Files.FileExists(Path.Combine(homeDirectory, "squad.receipt.json")),
            "A migrated and then uninstalled global receipt must not come back from the legacy copy.");
        Assert.False(
            Files.FileExists(Path.Combine(homeDirectory, "squad.lock.yml")),
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
        MigrationScenario scenario = CreateScenario();
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));
        scenario.Migration.ImportProject(repo);
        string projectDirectory = Layout.HomeProjectDirectory(repo);
        Files.DeleteFile(Path.Combine(projectDirectory, "squad.receipt.json"));
        Files.DeleteFile(Path.Combine(projectDirectory, "squad.lock.yml"));

        SquadMigrationReport again = scenario.Migration.ImportProject(repo);

        Assert.Empty(again.ImportedProjects);
        Assert.False(
            Files.FileExists(Path.Combine(projectDirectory, "squad.receipt.json")),
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
        MigrationScenario scenario = CreateScenario();
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.3"));
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock("1.2.3"));
        Layout.WriteText(Layout.LedgerPath(), ledgerText);

        SquadMigrationReport globalReport = scenario.Migration.MigrateGlobal(invokingCwd: null);
        SquadMigrationReport projectReport = scenario.Migration.ImportProject(repo);

        Assert.NotEmpty(globalReport.MigratedItems);
        Assert.Single(projectReport.ImportedProjects);
        Assert.Contains(
            globalReport.Warnings.Concat(projectReport.Warnings),
            warning => warning.Contains("migration ledger", StringComparison.OrdinalIgnoreCase));
        JsonNode ledger = ReadLedger();
        Assert.Equal(new[] { Layout.KeyOf(root) }, ReadStrings(ledger, "globalMigratedFrom"));
        Assert.Equal(new[] { Layout.KeyOf(repo) }, ReadStrings(ledger, "projectsImported"));
    }

    /// <summary>
    /// A ledger the process cannot open is the same situation as one it cannot parse: it is read as empty with a
    /// reason, so the migration reports it as a warning. An access denial must not escape as an unhandled exception.
    /// </summary>
    [Fact]
    public void LedgerThatCannotBeOpenedIsReadAsEmptyWithAReason()
    {
        string path = Layout.LedgerPath();
        Layout.WriteText(path, "{\"schema\":\"kyber-squad.migration-ledger/v1\",\"globalMigratedFrom\":[],\"projectsImported\":[]}");
        Files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText) && operation.Path == path,
            new UnauthorizedAccessException("denied"));

        SquadMigrationLedger ledger = SquadMigrationLedger.Read(Files, path, out string? unreadableReason);

        Assert.NotNull(unreadableReason);
        Assert.Empty(ledger.GlobalMigratedFrom);
        Assert.Empty(ledger.ProjectsImported);
    }

    private sealed record MigrationScenario(
        string Home,
        string ApplicationData,
        SquadStateStore Store,
        SquadStateMigration Migration);

    private SquadStateTestLayout Layout => _state.Layout;

    private MigrationScenario CreateScenario() =>
        new(Layout.Home, Layout.ApplicationData, _state.Store, _state.Migration);

    private IReadOnlyDictionary<string, SquadTreeEntry> Snapshot(string directory) =>
        SquadStateTreeCapture.Capture(Files, directory);

    private void AssertTreeUnchanged(string directory, IReadOnlyDictionary<string, SquadTreeEntry> before) =>
        Assert.Null(SquadTreeComparison.Difference(before, Snapshot(directory)));

    private JsonNode ReadLedger() =>
        JsonNode.Parse(Files.ReadAllText(Layout.LedgerPath()))!;

    private static string[] ReadStrings(JsonNode ledger, string property) =>
        ledger[property]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();

    private string[] TemporaryFilesBeside(string path) =>
        Files.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp", recursive: false).ToArray();

    private string ReadFilesJson(string receiptPath) =>
        JsonNode.Parse(Files.ReadAllText(receiptPath))!.AsObject()["files"]!.ToJsonString();

    /// <summary>Compares two locations by where they physically are, so the comparison is on the canonical form, not the spelling.</summary>
    private bool IsSameLocation(string expected, string actual) =>
        CanonicalLocation(expected) == CanonicalLocation(actual);

    private string CanonicalLocation(string path)
    {
        string existing = Path.GetFullPath(path);
        List<string> unresolved = [];
        while (!Files.DirectoryExists(existing))
        {
            unresolved.Insert(0, Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing)!;
        }

        return unresolved.Aggregate(
            Layout.PhysicalOf(existing),
            (accumulated, part) => Path.Combine(accumulated, part));
    }
}
