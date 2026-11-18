using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// The Squad state store over an in-memory file system: where it keeps the registry, receipts and locks, what it
/// does with a registry or receipt it cannot read, and what a crash at each file operation leaves behind. No test
/// here touches a disk, a mutex or the wall clock.
/// </summary>
public sealed class SquadStateStoreTests
{
    private readonly InMemorySquadState _state = new();

    private SquadStateStore Store => _state.Store;

    private SquadStateTestLayout Layout => _state.Layout;

    private InMemoryFileSystem Files => _state.Files;

    private void WriteProjectReceipt(string root)
    {
        SquadReceipt receipt = new(
            SquadStateSchemas.ReceiptSchemaV1,
            SquadDeploymentScope.Project,
            root,
            SquadStateTestLayout.InstalledAt,
            [],
            [SquadStateTestLayout.OwnedFile(".codex/agents/alpha.toml", "alpha")]);
        string directory = Layout.HomeProjectDirectory(root);
        Layout.WriteText(Path.Combine(directory, "squad.receipt.json"), Store.SerializeReceipt(receipt));
    }

    private JsonObject ReadRegistry() =>
        JsonNode.Parse(Files.ReadAllText(Layout.RegistryPath()))!.AsObject();

    [Fact]
    public void ProjectStateLivesUnderTheHomeByTheRootsPhysicalKey()
    {
        string root = _state.CreateRoot("alpha");

        string directory = Store.ResolveStateDirectory(root, SquadDeploymentScope.Project);

        Assert.Equal(Layout.HomeProjectDirectory(root), directory);
        Assert.Equal(Path.Combine(directory, "squad.receipt.json"), Store.ResolveReceiptPath(root, SquadDeploymentScope.Project));
        Assert.Equal(Path.Combine(directory, "squad.lock.yml"), Store.ResolveLockPath(root, SquadDeploymentScope.Project));
    }

    [Fact]
    public void GlobalStateLivesUnderTheHomeUnlessTheTargetRootAlreadyHoldsIt()
    {
        string other = _state.CreateRoot("other");
        string home = Path.GetFullPath(_state.UserPaths.HomeDirectory);

        Assert.Equal(
            Path.Combine(home, ".kyber-weave", "squad", "global"),
            Store.ResolveStateDirectory(other, SquadDeploymentScope.Global));

        Layout.WriteText(Path.Combine(other, ".kyber-weave", "squad", "global", "squad.receipt.json"), "{}");

        Assert.Equal(
            Path.Combine(other, ".kyber-weave", "squad", "global"),
            Store.ResolveStateDirectory(other, SquadDeploymentScope.Global));
    }

    [Fact]
    public void ARelativeGlobalTargetRootIsResolvedAgainstTheEnvironmentsWorkingDirectory()
    {
        string work = _state.Environment.CurrentDirectory;
        Layout.WriteText(Path.Combine(work, "rel", ".kyber-weave", "squad", "global", "squad.lock.yml"), "x");

        string directory = Store.ResolveStateDirectory("rel", SquadDeploymentScope.Global);

        Assert.Equal(Path.Combine(work, "rel", ".kyber-weave", "squad", "global"), directory);
    }

    [Fact]
    public void RegisterProjectWritesTheRegistryAtomicallyWithSortedDistinctTargetsAndTheGivenInstant()
    {
        string root = _state.CreateRoot("alpha");
        DateTimeOffset now = new(2026, 11, 18, 10, 30, 0, TimeSpan.FromHours(2));

        Store.RegisterProject(root, ["warp", "codex", "warp"], now);

        JsonObject registry = ReadRegistry();
        Assert.Equal(SquadStateStore.RegistrySchema, registry["schema"]!.GetValue<string>());
        JsonObject entry = registry["projects"]!.AsObject()[Layout.KeyOf(root)]!.AsObject();
        Assert.Equal(root, entry["path"]!.GetValue<string>());
        Assert.Equal("2026-11-18T08:30:00.0000000Z", entry["lastUpdatedUtc"]!.GetValue<string>());
        Assert.Equal(["codex", "warp"], entry["targets"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Empty(Files.EnumerateFiles(Path.GetDirectoryName(Layout.RegistryPath())!, "*.tmp", recursive: false));
    }

    [Fact]
    public void ARegistryWriteThatFailsLeavesThePreviousRegistryInPlace()
    {
        string alpha = _state.CreateRoot("alpha");
        string beta = _state.CreateRoot("beta");
        Store.RegisterProject(alpha, ["codex"], SquadStateTestLayout.InstalledAt);
        string before = Files.ReadAllText(Layout.RegistryPath());
        Files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.WriteAllTextAtomic));

        Assert.Throws<IOException>(() => Store.RegisterProject(beta, ["codex"], SquadStateTestLayout.InstalledAt));

        Files.ClearFaults();
        Assert.Equal(before, Files.ReadAllText(Layout.RegistryPath()));
    }

    [Fact]
    public void UnregisterProjectRemovesOnlyTheNamedEntryAndAnswersWhetherOneExisted()
    {
        string alpha = _state.CreateRoot("alpha");
        string beta = _state.CreateRoot("beta");
        Store.RegisterProject(alpha, ["codex"], SquadStateTestLayout.InstalledAt);
        Store.RegisterProject(beta, ["codex"], SquadStateTestLayout.InstalledAt);

        Assert.True(Store.UnregisterProject(alpha));
        Assert.False(Store.UnregisterProject(alpha));

        Assert.Equal([Layout.KeyOf(beta)], ReadRegistry()["projects"]!.AsObject().Select(pair => pair.Key));
    }

    [Fact]
    public void ListProjectsRebuildsAMissingEntryFromItsReceiptAndWritesTheRegistryBack()
    {
        string root = _state.CreateRoot("alpha");
        WriteProjectReceipt(root);

        IReadOnlyList<SquadProjectRegistryEntry> listed = Store.ListProjects();

        SquadProjectRegistryEntry entry = Assert.Single(listed);
        Assert.Equal(root, entry.Path);
        Assert.Equal(["codex"], entry.Targets);
        Assert.False(entry.Stale);
        Assert.True(Files.FileExists(Layout.RegistryPath()));
    }

    [Fact]
    public void ListProjectsWithoutRebuildAnswersFromTheReceiptAndWritesNothing()
    {
        string root = _state.CreateRoot("alpha");
        WriteProjectReceipt(root);

        IReadOnlyList<SquadProjectRegistryEntry> listed = Store.ListProjectsWithoutRebuild();

        Assert.Single(listed);
        Assert.False(Files.FileExists(Layout.RegistryPath()));
        Assert.Equal(root, Store.FindProject(root)!.Path);
    }

    [Fact]
    public void ACorruptReceiptIsLeftOutOfTheListingAndTheOtherProjectsAreStillListed()
    {
        string good = _state.CreateRoot("good");
        string bad = _state.CreateRoot("bad");
        WriteProjectReceipt(good);
        Layout.WriteText(Path.Combine(Layout.HomeProjectDirectory(bad), "squad.receipt.json"), "not json");

        IReadOnlyList<SquadProjectRegistryEntry> listed = Store.ListProjects();

        Assert.Equal([good], listed.Select(entry => entry.Path));
    }

    [Fact]
    public void AStateDirectoryThatIsALinkOrIsNotARootKeyIsNotAProject()
    {
        string root = _state.CreateRoot("alpha");
        WriteProjectReceipt(root);
        string projects = Path.GetDirectoryName(Layout.HomeProjectDirectory(root))!;
        string linked = Path.Combine(projects, new string('a', 64));
        Files.CreateDirectoryLink(linked, Layout.HomeProjectDirectory(root));
        Layout.WriteText(Path.Combine(projects, "not-a-key", "squad.receipt.json"), "{}");

        IReadOnlyList<SquadProjectRegistryEntry> listed = Store.ListProjectsWithoutRebuild();

        Assert.Equal([Layout.KeyOf(root)], listed.Select(entry => entry.RootKey));
    }

    [Fact]
    public void ARegisteredProjectWhoseDirectoryIsGoneIsReportedStaleNotDropped()
    {
        string root = _state.CreateRoot("alpha");
        Store.RegisterProject(root, ["codex"], SquadStateTestLayout.InstalledAt);
        Files.DeleteTreeNoFollow(root);

        SquadProjectRegistryEntry entry = Assert.Single(Store.ListProjects());

        Assert.True(entry.Stale);
        Assert.Contains("no longer exists", entry.StaleReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ nope")]
    [InlineData("[]")]
    [InlineData("{\"schema\":\"kyber-squad.registry/v0\",\"projects\":{}}")]
    [InlineData("{\"schema\":\"kyber-squad.registry/v1\",\"projects\":{\"k\":{\"path\":\"/x\"}}}")]
    public void ARegistryThatDoesNotParseIsAnErrorTheOperatorSees(string registryText)
    {
        Layout.WriteText(Layout.RegistryPath(), registryText);

        Assert.Throws<InvalidDataException>(() => Store.ListProjects());
    }

    [Fact]
    public void ReceiptAndLockReadBackWhatTheStoreWrote()
    {
        string root = _state.CreateRoot("alpha");
        SquadLock squadLock = SquadStateTestLayout.Lock("2.0.0");
        SquadReceipt receipt = SquadStateTestLayout.ProjectReceipt(SquadStateTestLayout.OwnedFile("a.md", "a"));
        string directory = Store.ResolveStateDirectory(root, SquadDeploymentScope.Project);
        Layout.WriteText(Path.Combine(directory, "squad.lock.yml"), Store.SerializeLock(squadLock));
        Layout.WriteText(Path.Combine(directory, "squad.receipt.json"), Store.SerializeReceipt(receipt));

        Assert.Equal("2.0.0", Store.ReadLock(root, SquadDeploymentScope.Project)!.SquadVersion);
        Assert.Equal(receipt.Files.Single().RelativePath, Store.ReadReceipt(root, SquadDeploymentScope.Project)!.Files.Single().RelativePath);
    }

    [Fact]
    public void ReceiptAndLockAreNullWhenNothingIsRecorded()
    {
        string root = _state.CreateRoot("alpha");

        Assert.Null(Store.ReadReceipt(root, SquadDeploymentScope.Project));
        Assert.Null(Store.ReadLock(root, SquadDeploymentScope.Project));
    }

    [Fact]
    public void EveryRegisterProjectCrashLeavesTheOldRegistryOrTheCompleteNewOne()
    {
        int operations = CountOperations(
            state => state.Store.RegisterProject(Path.GetFullPath("/kw/projects/beta"), ["codex"], SquadStateTestLayout.InstalledAt),
            Seed);
        Assert.True(operations > 1, "The run must perform several file operations for the sweep to mean anything.");

        for (int point = 1; point <= operations; point++)
        {
            InMemorySquadState state = new();
            Seed(state);
            string beta = state.CreateRoot("beta");
            byte[] before = state.Files.ReadAllBytes(state.Layout.RegistryPath());
            state.Files.KillAt(state.Files.OperationCount + point);

            Assert.Throws<InMemoryFileSystemKilledException>(() =>
                state.Store.RegisterProject(beta, ["codex"], SquadStateTestLayout.InstalledAt));

            state.Files.ClearFaults();
            byte[] after = state.Files.ReadAllBytes(state.Layout.RegistryPath());
            IReadOnlyList<string> keys = ParseKeys(after);
            Assert.True(
                after.AsSpan().SequenceEqual(before) || keys.Contains(state.Layout.KeyOf(beta), StringComparer.Ordinal),
                $"Killed at operation {point}: the registry is neither the old one nor the complete new one.");
            Assert.Contains(state.Layout.KeyOf(state.CreateRoot("alpha")), keys);
            Assert.Empty(state.Files.EnumerateFiles(Path.GetDirectoryName(state.Layout.RegistryPath())!, "*.tmp", recursive: false));
        }

        static void Seed(InMemorySquadState state)
        {
            _ = state.CreateRoot("beta");
            state.Store.RegisterProject(state.CreateRoot("alpha"), ["codex"], SquadStateTestLayout.InstalledAt);
        }
    }

    [Fact]
    public void EveryRebuildCrashLeavesNoRegistryOrTheCompleteRebuiltOne()
    {
        int operations = CountOperations(state => state.Store.ListProjects(), Seed);
        Assert.True(operations > 1, "The run must perform several file operations for the sweep to mean anything.");

        for (int point = 1; point <= operations; point++)
        {
            InMemorySquadState state = new();
            Seed(state);
            state.Files.KillAt(state.Files.OperationCount + point);

            Assert.Throws<InMemoryFileSystemKilledException>(() => state.Store.ListProjects());

            state.Files.ClearFaults();
            string registryPath = state.Layout.RegistryPath();
            if (state.Files.FileExists(registryPath))
            {
                Assert.Equal(
                    [state.Layout.KeyOf(state.CreateRoot("alpha"))],
                    ParseKeys(state.Files.ReadAllBytes(registryPath)));
            }

            Assert.Single(state.Store.ListProjects());
            Assert.Empty(state.Files.EnumerateFiles(Path.GetDirectoryName(registryPath)!, "*.tmp", recursive: false));
        }

        static void Seed(InMemorySquadState state)
        {
            string alpha = state.CreateRoot("alpha");
            SquadReceipt receipt = new(
                SquadStateSchemas.ReceiptSchemaV1,
                SquadDeploymentScope.Project,
                alpha,
                SquadStateTestLayout.InstalledAt,
                [],
                [SquadStateTestLayout.OwnedFile("a.md", "a")]);
            state.Layout.WriteText(
                Path.Combine(state.Layout.HomeProjectDirectory(alpha), "squad.receipt.json"),
                state.Store.SerializeReceipt(receipt));
        }
    }

    private static int CountOperations(Action<InMemorySquadState> run, Action<InMemorySquadState> seed)
    {
        InMemorySquadState state = new();
        seed(state);
        int before = state.Files.OperationCount;
        run(state);
        return state.Files.OperationCount - before;
    }

    private static IReadOnlyList<string> ParseKeys(byte[] registry) =>
        JsonNode.Parse(registry)!.AsObject()["projects"]!.AsObject().Select(pair => pair.Key).ToArray();
}
