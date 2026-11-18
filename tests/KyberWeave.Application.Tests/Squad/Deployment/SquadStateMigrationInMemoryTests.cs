using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// The Squad state migration over an in-memory file system, a simulated lease table and a fake clock: what it
/// copies, what it refuses, what it reports when another writer holds the root, and what it does with state
/// it cannot read. The same behaviour runs against a real disk in the legacy-state tests of the unit project.
/// </summary>
public sealed class SquadStateMigrationInMemoryTests
{
    private readonly InMemorySquadState _state = new();

    private SquadStateTestLayout Layout => _state.Layout;

    private string LeaseNameOfGlobal() =>
        SquadLeaseNames.ForRoot(_state.Store.ResolveLeaseIdentity(_state.Store.ResolveHomeRoot(), SquadDeploymentScope.Global));

    private string LeaseNameOfProject(string root) =>
        SquadLeaseNames.ForRoot(_state.Store.ResolveLeaseIdentity(root, SquadDeploymentScope.Project));

    private string SeedLegacyGlobal()
    {
        string root = _state.CreateRoot("customer-alpha");
        Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock());
        return root;
    }

    private string SeedInRepo()
    {
        string repo = _state.CreateRoot("repo");
        Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock());
        return repo;
    }

    [Fact]
    public void ALegacyGlobalReceiptIsCopiedToTheHomeWithTheHomeAsItsTargetRootAndTheLockByteForByte()
    {
        string root = SeedLegacyGlobal();
        byte[] legacyLock = _state.Files.ReadAllBytes(Path.Combine(Layout.LegacyGlobalDirectory(root), "squad.lock.yml"));

        SquadMigrationReport report = _state.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Equal(legacyLock, _state.Files.ReadAllBytes(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml")));
        Assert.Equal(
            Layout.PhysicalOf(_state.UserPaths.HomeDirectory),
            Layout.ReadTargetRoot(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
    }

    [Fact]
    public void TheLegacyFilesAreLeftByteIdenticalAndAGlobalCopyIsRecordedInTheLedger()
    {
        string root = SeedLegacyGlobal();
        IReadOnlyDictionary<string, SquadTreeEntry> legacyBefore = SquadStateTreeCapture.Capture(
            _state.Files,
            Layout.LegacyGlobalDirectory(root));

        _state.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Null(SquadTreeComparison.Difference(
            legacyBefore,
            SquadStateTreeCapture.Capture(_state.Files, Layout.LegacyGlobalDirectory(root))));
        JsonObject ledger = JsonNode.Parse(_state.Files.ReadAllText(Layout.LedgerPath()))!.AsObject();
        Assert.Equal([Layout.KeyOf(root)], ledger["globalMigratedFrom"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void ASecondRunChangesNothingAndAMigratedThenUninstalledGlobalIsNotCopiedBack()
    {
        SeedLegacyGlobal();
        _state.Migration.MigrateGlobal(invokingCwd: null);
        IReadOnlyDictionary<string, SquadTreeEntry> afterFirst = _state.Capture();

        SquadMigrationReport second = _state.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(second.MigratedItems);
        Assert.Null(SquadTreeComparison.Difference(afterFirst, _state.Capture()));

        _state.Files.DeleteFile(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json"));
        _state.Files.DeleteFile(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml"));
        SquadMigrationReport third = _state.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(third.MigratedItems);
        Assert.False(_state.Files.FileExists(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
        Assert.Single(third.UnmigratedLegacyRoots);
    }

    [Fact]
    public void ADryRunReportsWhatWouldMoveAndWritesNothing()
    {
        SeedLegacyGlobal();
        IReadOnlyDictionary<string, SquadTreeEntry> before = _state.Capture();

        SquadMigrationReport report = _state.Migration.MigrateGlobal(invokingCwd: null, dryRun: true);

        Assert.Single(report.MigratedItems);
        Assert.Null(SquadTreeComparison.Difference(before, _state.Capture()));
    }

    [Fact]
    public void SeveralNonEmptyLegacyRootsAreReportedAmbiguousAndNothingIsCopied()
    {
        SeedLegacyGlobal();
        string second = _state.CreateRoot("customer-beta");
        Layout.WriteLegacyGlobal(second, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.4"));
        _state.Files.CreateDirectory(Path.Combine(_state.UserPaths.ApplicationDataDirectory, "KyberWeave", "squad", "roots", "0f3a-empty"));

        SquadMigrationReport report = _state.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        Assert.Equal(2, report.UnmigratedLegacyRoots.Count);
        Assert.False(_state.Files.FileExists(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
    }

    [Fact]
    public void ALegacyRootThatIsALinkOrWhoseListingIsDeniedIsNotCounted()
    {
        string root = SeedLegacyGlobal();
        string roots = Path.GetDirectoryName(Layout.LegacyGlobalDirectory(root))!;
        _state.Files.CreateDirectoryLink(Path.Combine(roots, "linked"), Layout.LegacyGlobalDirectory(root));
        string denied = Path.Combine(roots, "denied");
        Layout.WriteText(Path.Combine(denied, "squad.receipt.json"), "{}");
        _state.Files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.EnumerateEntries) && operation.Path == denied,
            new UnauthorizedAccessException("denied"));

        SquadMigrationReport report = _state.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Single(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
    }

    [Fact]
    public void AGlobalCopyIsRefusedAndReportedTwiceWhileAnotherWriterHoldsTheHomeLease()
    {
        SeedLegacyGlobal();
        using IDisposable holder = new InProcessLease(_state.LeaseHost).Hold(LeaseNameOfGlobal());

        SquadMigrationReport report = _state.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        SquadUnmigratedLegacyRoot busy = Assert.Single(report.UnmigratedLegacyRoots);
        Assert.Contains("holds the lease", busy.Reason, StringComparison.Ordinal);
        Assert.Contains(report.Warnings, warning => warning.Contains("was not copied", StringComparison.Ordinal));
        Assert.False(_state.Files.FileExists(Path.Combine(Layout.HomeGlobalDirectory(), "squad.receipt.json")));
    }

    [Fact]
    public void TheLeaseIsReleasedAfterACopySoAnotherWriterCanTakeItAtOnce()
    {
        SeedLegacyGlobal();

        _state.Migration.MigrateGlobal(invokingCwd: null);

        using IDisposable next = new InProcessLease(_state.LeaseHost).Hold(LeaseNameOfGlobal());
    }

    [Fact]
    public void ADifferentHomeLockWithoutAReceiptBlocksTheCopyAndIsNeverOverwritten()
    {
        SeedLegacyGlobal();
        Layout.WriteText(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml"), "someone else's lock");

        SquadMigrationReport report = _state.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Empty(report.MigratedItems);
        Assert.Single(report.UnmigratedLegacyRoots);
        Assert.Equal("someone else's lock", _state.Files.ReadAllText(Path.Combine(Layout.HomeGlobalDirectory(), "squad.lock.yml")));
    }

    [Fact]
    public void ALedgerThatCannotBeReadIsTreatedAsEmptyWithAWarningAndRewrittenByTheNextCopy()
    {
        SeedLegacyGlobal();
        Layout.WriteText(Layout.LedgerPath(), "{ not json");

        SquadMigrationReport report = _state.Migration.MigrateGlobal(invokingCwd: null);

        Assert.NotEmpty(report.MigratedItems);
        Assert.Contains(report.Warnings, warning => warning.Contains("migration ledger", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "kyber-squad.migration-ledger/v1",
            JsonNode.Parse(_state.Files.ReadAllText(Layout.LedgerPath()))!["schema"]!.GetValue<string>());
    }

    [Fact]
    public void ALedgerTheProcessMayNotOpenIsReadAsEmptyWithAReason()
    {
        SeedLegacyGlobal();
        Layout.WriteText(Layout.LedgerPath(), "{}");
        _state.Files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText) && operation.Path == Layout.LedgerPath(),
            new UnauthorizedAccessException("denied"));

        SquadMigrationReport report = _state.Migration.MigrateGlobal(invokingCwd: null);

        Assert.Contains(report.Warnings, warning => warning.Contains("denied", StringComparison.Ordinal));
    }

    [Fact]
    public void AProjectImportCopiesTheInRepoStateRegistersTheProjectAtTheClocksInstantAndRecordsTheLedger()
    {
        string repo = SeedInRepo();
        _state.Time.Advance(TimeSpan.FromHours(3));

        SquadMigrationReport report = _state.Migration.ImportProject(repo);

        Assert.Equal([repo], report.ImportedProjects);
        string projectDirectory = Layout.HomeProjectDirectory(repo);
        Assert.Equal(
            _state.Files.ReadAllBytes(Path.Combine(repo, ".kyber-weave", "squad.lock.yml")),
            _state.Files.ReadAllBytes(Path.Combine(projectDirectory, "squad.lock.yml")));
        Assert.Equal(repo, Layout.ReadTargetRoot(Path.Combine(projectDirectory, "squad.receipt.json")));
        Assert.Equal(_state.Time.GetUtcNow(), _state.Store.FindProject(repo)!.LastUpdatedUtc);
        Assert.Contains(
            Layout.KeyOf(repo),
            JsonNode.Parse(_state.Files.ReadAllText(Layout.LedgerPath()))!["projectsImported"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void AProjectImportIsNotRepeatedAfterTheHomeProjectReceiptIsRemoved()
    {
        string repo = SeedInRepo();
        _state.Migration.ImportProject(repo);
        string projectDirectory = Layout.HomeProjectDirectory(repo);
        _state.Files.DeleteFile(Path.Combine(projectDirectory, "squad.receipt.json"));
        _state.Files.DeleteFile(Path.Combine(projectDirectory, "squad.lock.yml"));

        SquadMigrationReport again = _state.Migration.ImportProject(repo);

        Assert.Empty(again.ImportedProjects);
        Assert.False(_state.Files.FileExists(Path.Combine(projectDirectory, "squad.receipt.json")));
    }

    [Fact]
    public void AProjectImportIsRefusedWhileAnotherWriterHoldsTheRootsLease()
    {
        string repo = SeedInRepo();
        using IDisposable holder = new InProcessLease(_state.LeaseHost).Hold(LeaseNameOfProject(repo));

        SquadMigrationReport report = _state.Migration.ImportProject(repo);

        Assert.Empty(report.ImportedProjects);
        Assert.Contains(report.Warnings, warning => warning.Contains("was not imported", StringComparison.Ordinal));
        Assert.False(_state.Files.FileExists(Path.Combine(Layout.HomeProjectDirectory(repo), "squad.receipt.json")));
    }

    [Fact]
    public void AnInRepoReceiptWithoutALockIsNotImported()
    {
        string repo = _state.CreateRoot("repo");
        Layout.WriteText(
            Path.Combine(repo, ".kyber-weave", "squad.receipt.json"),
            _state.Store.SerializeReceipt(SquadStateTestLayout.ProjectReceipt(SquadStateTestLayout.OwnedFile("a.md", "a"))));

        SquadMigrationReport report = _state.Migration.ImportProject(repo);

        Assert.Empty(report.ImportedProjects);
        Assert.Contains(report.Warnings, warning => warning.Contains("no lock", StringComparison.Ordinal));
    }
}
