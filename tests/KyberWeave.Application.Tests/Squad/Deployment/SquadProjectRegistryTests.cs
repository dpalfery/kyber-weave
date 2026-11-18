using KyberWeave.Testing.Squad;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// Pins the home-level project registry: which projects a machine has deployed Squad into, how a
/// project's entry follows its install, update and uninstall, and how a registry that disagrees
/// with the state on disk is reported rather than trusted.
/// </summary>
/// <remarks>
/// The registry is an index over state that already exists under the home store. It is never the
/// authority for ownership, so a missing or stale entry is a finding to report, not a reason to
/// refuse a deployment. The tests run over the in-memory state, which points the home directory at an in-memory home.
/// </remarks>
public sealed class SquadProjectRegistryTests
{
    private static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    private static readonly string[] ExpectedTargets = ["codex", "warp"];

    private static readonly string[] ExpectedSquadEntries = ["projects", "registry.json"];

    private readonly InMemorySquadState _state = new();

    private string Work => Path.Combine(_state.Files.Root, "work");

    [Fact]
    public void ProjectInstallRegistersPathLastUpdatedUtcAndTargets()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        _state.Files.CreateDirectory(root);
        SquadStateStore store = _state.Store;

        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project));

        SquadProjectRegistryEntry entry = Assert.Single(store.ListProjects());
        Assert.Equal(SquadPhysicalRootIdentities.Resolve(root, _state.Semantics).PhysicalPath, entry.Path);
        Assert.Equal(SquadPhysicalRootIdentities.Resolve(root, _state.Semantics).Key, entry.RootKey);
        Assert.Equal(InstalledAt, entry.LastUpdatedUtc);
        Assert.Equal(ExpectedTargets, entry.Targets.Order(StringComparer.Ordinal));
        Assert.False(entry.Stale);
        Assert.True(_state.Files.FileExists(RegistryPath(home)), "The registry must be written beneath the home store.");
    }

    [Fact]
    public void ProjectUpdateRefreshesLastUpdatedUtc()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        _state.Files.CreateDirectory(root);
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project));
        DateTimeOffset updatedAt = InstalledAt.AddDays(1);

        _state.Transaction().Execute(_state.Planner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [
                Rendered(".codex/agents/conductor.toml", "conductor v2"),
                Rendered(".warp/roles/reviewer.md", "reviewer", "warp")
            ],
            Assert.IsType<SquadReceipt>(store.ReadReceipt(root, SquadDeploymentScope.Project)),
            [],
            replaceManaged: false,
            new FixedTimeProvider(updatedAt)));

        SquadProjectRegistryEntry entry = Assert.Single(store.ListProjects());
        Assert.Equal(updatedAt, entry.LastUpdatedUtc);
        Assert.Equal(ExpectedTargets, entry.Targets.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProjectUninstallRemovesItsRegistryEntry()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        _state.Files.CreateDirectory(root);
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project));

        _state.Transaction().Execute(_state.Planner.CreateUninstall(
            root,
            SquadDeploymentScope.Project,
            Assert.IsType<SquadReceipt>(store.ReadReceipt(root, SquadDeploymentScope.Project))));

        Assert.Empty(store.ListProjects());
        Assert.False(
            store.UnregisterProject(root),
            "Uninstall must already have removed the entry, so there is nothing left to unregister.");
    }

    /// <summary>
    /// A registered root that no longer exists is reported stale with its reason, not dropped, so an
    /// operator can see what was registered before choosing to unregister it.
    /// </summary>
    [Fact]
    public void RegisteredProjectWhosePathIsMissingIsFlaggedStale()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        _state.Files.CreateDirectory(root);
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project));
        _state.Files.DeleteTreeNoFollow(root);

        SquadProjectRegistryEntry entry = Assert.Single(store.ListProjects());

        Assert.True(entry.Stale);
        Assert.Contains("path", entry.StaleReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An entry whose recorded key no longer matches the key derived from its path is flagged stale.
    /// The key is replaced textually so the test does not depend on how the registry lays out the
    /// entry beyond keying it by root key.
    /// </summary>
    [Fact]
    public void RegisteredProjectWhoseKeyNoLongerMatchesItsPathIsFlaggedStale()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        _state.Files.CreateDirectory(root);
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project));
        string key = SquadPhysicalRootIdentities.Resolve(root, _state.Semantics).Key;
        string registry = _state.Files.ReadAllText(RegistryPath(home));
        _state.Files.WriteAllText(
            RegistryPath(home),
            registry.Replace(key, new string('0', key.Length), StringComparison.Ordinal));

        IReadOnlyList<SquadProjectRegistryEntry> entries = store.ListProjects();

        Assert.Contains(entries, entry =>
            entry.Stale &&
            entry.StaleReason is not null &&
            entry.StaleReason.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegisteredProjectWhoseReceiptIsMissingIsFlaggedStale()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        _state.Files.CreateDirectory(root);
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project));
        _state.Files.DeleteFile(store.ResolveReceiptPath(root, SquadDeploymentScope.Project));

        SquadProjectRegistryEntry entry = Assert.Single(store.ListProjects());

        Assert.True(entry.Stale);
        Assert.Contains("receipt", entry.StaleReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A registry that lost entries is repaired from the per-project state beneath
    /// <c>projects/&lt;key&gt;</c>. Losing the index must never make an installed project invisible.
    /// </summary>
    [Fact]
    public void RegistryMissingEntriesIsRebuiltFromProjectState()
    {
        string home = _state.UserPaths.HomeDirectory;
        string firstRoot = Path.Combine(Work, "project-alpha");
        string secondRoot = Path.Combine(Work, "project-beta");
        _state.Files.CreateDirectory(firstRoot);
        _state.Files.CreateDirectory(secondRoot);
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(firstRoot, SquadDeploymentScope.Project));
        _state.Transaction().Execute(Install(secondRoot, SquadDeploymentScope.Project));
        _state.Files.DeleteFile(RegistryPath(home));

        IReadOnlyList<SquadProjectRegistryEntry> entries = store.ListProjects();

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.False(entry.Stale));
        Assert.Equal(
            new[]
            {
                SquadPhysicalRootIdentities.Resolve(firstRoot, _state.Semantics).PhysicalPath,
                SquadPhysicalRootIdentities.Resolve(secondRoot, _state.Semantics).PhysicalPath
            }.Order(StringComparer.Ordinal),
            entries.Select(entry => entry.Path).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The registry is written to a temporary file and renamed into place, so a finished write leaves
    /// no temporary residue beside it. The directory listing is the observable proof of that.
    /// </summary>
    [Fact]
    public void RegistryWriteLeavesNoTemporaryFileBesideTheRegistry()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        _state.Files.CreateDirectory(root);
        SquadStateStore store = _state.Store;

        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project));

        Assert.Equal(
            ExpectedSquadEntries,
            EntriesOf(SquadDirectoryOf(home)));
    }

    /// <summary>
    /// A registry write that fails must leave the prior registry in place and create no partial file.
    /// The failure is simulated by putting a directory where the registry file belongs, which makes the
    /// final rename fail. A crash mid-write needs a seam that does not exist yet, so it is a known gap.
    /// </summary>
    [Fact]
    public void FailedRegistryWriteLeavesNoPartialFile()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        string otherRoot = Path.Combine(Work, "project-beta");
        _state.Files.CreateDirectory(root);
        _state.Files.CreateDirectory(otherRoot);
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project));
        Assert.True(_state.Files.FileExists(RegistryPath(home)), "A committed install must leave a registry file to fail against.");
        _state.Files.DeleteFile(RegistryPath(home));
        _state.Files.CreateDirectory(RegistryPath(home));
        _state.Files.WriteAllText(Path.Combine(RegistryPath(home), "sentinel.txt"), "sentinel");

        Exception? failure = Record.Exception(() =>
            store.RegisterProject(otherRoot, ["codex"], InstalledAt.AddDays(1)));

        Assert.NotNull(failure);
        Assert.True(_state.Files.DirectoryExists(RegistryPath(home)));
        Assert.Equal("sentinel", _state.Files.ReadAllText(Path.Combine(RegistryPath(home), "sentinel.txt")));
        Assert.Equal(
            ExpectedSquadEntries,
            EntriesOf(SquadDirectoryOf(home)));
    }

    /// <summary>
    /// Global install is machine-wide and not a project, so it must not appear in the project registry.
    /// Registering it would make every global install look like a project whose path is the home.
    /// </summary>
    [Fact]
    public void GlobalInstallDoesNotRegisterAProject()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "global-target");
        _state.Files.CreateDirectory(root);
        SquadStateStore store = _state.Store;

        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Global));

        Assert.Empty(store.ListProjects());
        Assert.False(_state.Files.FileExists(RegistryPath(home)));
    }

    /// <summary>
    /// A committed project install must succeed even when its registry write fails, because the
    /// deployment is already durable and the index is repairable. The warning that should accompany
    /// it has no observable channel in the current surface, so that half of the contract is a gap.
    /// </summary>
    [Fact]
    public void ProjectInstallSucceedsWhenRegistryWriteFailsAfterCommit()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        _state.Files.CreateDirectory(root);
        _state.Files.CreateDirectory(RegistryPath(home));
        SquadStateStore store = _state.Store;

        Exception? failure = Record.Exception(() =>
            _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project)));

        Assert.Null(failure);
        Assert.Equal("alpha", _state.Files.ReadAllText(Path.Combine(root, ".codex", "agents", "alpha.toml")));
        Assert.NotNull(store.ReadReceipt(root, SquadDeploymentScope.Project));
    }

    /// <summary>
    /// A lookup is a read. When the registry has lost an entry, <see cref="SquadStateStore.FindProject"/> may answer
    /// from per-project state, but it must not write the rebuilt registry back, because the registry is the index
    /// and a lookup is not one of the write paths that own it.
    /// </summary>
    [Fact]
    public void FindProjectAnswersFromProjectStateWithoutWritingTheRegistry()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        _state.Files.CreateDirectory(root);
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project));
        _state.Files.DeleteFile(RegistryPath(home));

        SquadProjectRegistryEntry? entry = store.FindProject(root);

        Assert.NotNull(entry);
        Assert.Equal(SquadPhysicalRootIdentities.Resolve(root, _state.Semantics).Key, entry.RootKey);
        Assert.False(_state.Files.FileExists(RegistryPath(home)), "A lookup must not recreate the registry it read from.");
    }

    /// <summary>
    /// A corrupt receipt beneath one project's state must not hide every other project. The corrupt entry is left
    /// out of the listing, and the registry rebuild carries on with the receipts that do parse.
    /// </summary>
    [Fact]
    public void CorruptReceiptDuringRegistryRebuildDoesNotAbortTheListing()
    {
        string home = _state.UserPaths.HomeDirectory;
        string corruptRoot = Path.Combine(Work, "project-alpha");
        string healthyRoot = Path.Combine(Work, "project-beta");
        _state.Files.CreateDirectory(corruptRoot);
        _state.Files.CreateDirectory(healthyRoot);
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(corruptRoot, SquadDeploymentScope.Project));
        _state.Transaction().Execute(Install(healthyRoot, SquadDeploymentScope.Project));
        _state.Files.DeleteFile(RegistryPath(home));
        _state.Files.WriteAllText(store.ResolveReceiptPath(corruptRoot, SquadDeploymentScope.Project), "{ corrupt receipt \n");

        SquadProjectRegistryEntry entry = Assert.Single(store.ListProjects());

        Assert.Equal(SquadPhysicalRootIdentities.Resolve(healthyRoot, _state.Semantics).PhysicalPath, entry.Path);
        Assert.Null(store.FindProject(corruptRoot));
    }

    /// <summary>
    /// A registry entry whose <c>lastUpdatedUtc</c> is not a timestamp is an unreadable registry. The error names the
    /// entry so the operator can find it, and it is the same error a malformed registry raises everywhere else.
    /// </summary>
    [Fact]
    public void RegistryEntryWithAMalformedTimestampIsRejectedAsUnreadable()
    {
        string home = _state.UserPaths.HomeDirectory;
        string root = Path.Combine(Work, "project");
        _state.Files.CreateDirectory(root);
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(root, SquadDeploymentScope.Project));
        string registry = _state.Files.ReadAllText(RegistryPath(home));
        _state.Files.WriteAllText(
            RegistryPath(home),
            Regex.Replace(registry, "\"lastUpdatedUtc\"\\s*:\\s*\"[^\"]*\"", "\"lastUpdatedUtc\": \"not-a-timestamp\""));

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => store.ListProjects());

        Assert.Contains("lastUpdatedUtc", failure.Message, StringComparison.Ordinal);
        Assert.Contains(SquadPhysicalRootIdentities.Resolve(root, _state.Semantics).Key, failure.Message, StringComparison.Ordinal);
    }

    private string RegistryPath(string home) =>
        Path.Combine(SquadDirectoryOf(home), "registry.json");

    private string SquadDirectoryOf(string home) =>
        Path.Combine(SquadPhysicalRootIdentities.Resolve(home, _state.Semantics).PhysicalPath, ".kyber-weave", "squad");

    private string[] EntriesOf(string directory) =>
        _state.Files.EnumerateEntries(directory)
            .Select(entry => Path.GetFileName(entry.Path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// A project install renders a codex agent and a warp reviewer. A global install renders relative
    /// to its target's global root, so it takes a bare path only; a dot-prefixed path would be read
    /// back as the legacy single-root layout (#91).
    /// </summary>
    private SquadDeploymentPlan Install(string root, SquadDeploymentScope scope) =>
        _state.Planner.CreateInstall(
            root,
            scope,
            Lock(),
            scope == SquadDeploymentScope.Global
                ? [Rendered("agents/alpha.toml", "alpha")]
                :
                [
                    Rendered(".codex/agents/alpha.toml", "alpha"),
                    Rendered(".warp/roles/reviewer.md", "reviewer", "warp")
                ],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));

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

    private static SquadDeploymentFile Rendered(
        string relativePath,
        string content,
        string target = "codex") =>
        new(relativePath, Encoding.UTF8.GetBytes(content), target);

    private static string Digest(string content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
