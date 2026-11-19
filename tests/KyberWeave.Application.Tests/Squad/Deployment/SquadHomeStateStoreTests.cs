using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// Pins where home-level Squad state lives, how each transaction journal is anchored, and which lease guards a
/// transaction. Global state is anchored on the user's home directory and never on a target root, so two unrelated
/// roots cannot each keep a private copy of one harness file's ownership receipt.
/// </summary>
/// <remarks>
/// <para>
/// Every test runs over <see cref="InMemorySquadState"/>, through the state store, the planner and the transaction
/// themselves. None touches the real home, the real application data directory, a harness directory, a symbolic link,
/// a mutex or the wall clock. A lease an "active transaction" holds is taken on the shared lease host of the state,
/// which is how the product's cross-process lease is modelled in memory.
/// </para>
/// <para>
/// The two resource-side halves these cases once needed already run in the integration project:
/// <c>PhysicalPathResolverContractTests.ResolvePhysicalRootReturnsTheCanonicalSpellingOfAnExistingDirectory</c> proves
/// a real symbolic link resolves to one physical root, and <c>MutexLeaseContractTests</c> proves the operating-system
/// mutex refuses a second owner.
/// </para>
/// </remarks>
public sealed class SquadHomeStateStoreTests
{
    private static readonly DateTimeOffset UpdatedAt = InMemorySquadState.Start.AddDays(1);

    private readonly InMemorySquadState _state = new();

    private SquadStateStore Store => _state.Store;

    private InMemoryFileSystem Files => _state.Files;

    private string Home => _state.UserPaths.HomeDirectory;

    private string ProjectsDirectory => Path.Combine(Files.Root, "kw-projects");

    [Fact]
    public void GlobalLockAndReceiptAreHomeAnchoredForAnyTargetRoot()
    {
        string firstRoot = CreateRoot("customer-alpha");
        string secondRoot = CreateRoot("unrelated-beta");
        string globalState = _state.Layout.HomeGlobalDirectory();

        Assert.Equal(
            Path.Combine(globalState, "squad.lock.yml"),
            Store.ResolveLockPath(firstRoot, SquadDeploymentScope.Global));
        Assert.Equal(
            Path.Combine(globalState, "squad.receipt.json"),
            Store.ResolveReceiptPath(firstRoot, SquadDeploymentScope.Global));
        Assert.Equal(
            Path.Combine(globalState, "squad.lock.yml"),
            Store.ResolveLockPath(secondRoot, SquadDeploymentScope.Global));
        Assert.Equal(
            Path.Combine(globalState, "squad.receipt.json"),
            Store.ResolveReceiptPath(secondRoot, SquadDeploymentScope.Global));
    }

    [Fact]
    public void HomeStateDirectoryIsDotKyberWeaveSquadBeneathTheUserHome()
    {
        Assert.Equal(
            Path.Combine(PhysicalOf(Home), ".kyber-weave", "squad"),
            Store.ResolveHomeStateDirectory());
    }

    /// <summary>
    /// Project state is keyed by the physical root, so a lexical alias and a symlink alias of one project must
    /// address the same lock and receipt, under the home store rather than the root.
    /// </summary>
    [Fact]
    public void ProjectStateIsKeyedByPhysicalRootUnderHomeAndAliasesShareOneKey()
    {
        string root = CreateRoot("project");
        string symlinkAlias = Path.Combine(ProjectsDirectory, "project-link");
        Files.CreateDirectoryLink(symlinkAlias, root);
        string lexicalAlias = Path.Combine(root, "..", Path.GetFileName(root));
        string projectState = _state.Layout.HomeProjectDirectory(root);

        Assert.Equal(Path.Combine(projectState, "squad.lock.yml"), Store.ResolveLockPath(root, SquadDeploymentScope.Project));
        Assert.Equal(Path.Combine(projectState, "squad.receipt.json"), Store.ResolveReceiptPath(root, SquadDeploymentScope.Project));
        Assert.Equal(projectState, Store.ResolveStateDirectory(root, SquadDeploymentScope.Project));
        Assert.Equal(
            Store.ResolveLockPath(root, SquadDeploymentScope.Project),
            Store.ResolveLockPath(symlinkAlias, SquadDeploymentScope.Project));
        Assert.Equal(
            Store.ResolveReceiptPath(root, SquadDeploymentScope.Project),
            Store.ResolveReceiptPath(lexicalAlias, SquadDeploymentScope.Project));
    }

    /// <summary>
    /// The project journal stays beside its root, so a project transaction never depends on where the home store
    /// lives. The global journal moves with the home anchor, not with a target root.
    /// </summary>
    [Fact]
    public void TransactionJournalsStayBesideTheirRootOrUnderHome()
    {
        string otherHome = Path.Combine(Files.Root, "kw-other-home");
        Files.CreateDirectory(otherHome);
        string root = CreateRoot("project");
        SquadStateStore otherStore = StoreFor(otherHome);
        string projectJournal = Path.Combine(PhysicalOf(root), ".kyber-weave", ".squad-transaction");
        string globalJournal = Path.Combine(PhysicalOf(Home), ".kyber-weave", ".squad-transaction");

        Assert.Equal(projectJournal, Store.ResolveTransactionDirectory(root, SquadDeploymentScope.Project));
        Assert.Equal(projectJournal, Store.ResolveTransactionWorkDirectory(root, SquadDeploymentScope.Project));
        Assert.Equal(globalJournal, Store.ResolveTransactionDirectory(root, SquadDeploymentScope.Global));
        Assert.Equal(globalJournal, Store.ResolveTransactionWorkDirectory(root, SquadDeploymentScope.Global));
        Assert.Equal(
            projectJournal,
            otherStore.ResolveTransactionDirectory(root, SquadDeploymentScope.Project));
    }

    /// <summary>
    /// Global work is serialized per home, so two unrelated target roots cannot both hold the global lease. Before
    /// this, the lease was keyed per target root and they never contended.
    /// </summary>
    [Fact]
    public void GlobalLeaseIsKeyedByHomeSoUnrelatedTargetRootsContend()
    {
        string firstRoot = CreateRoot("customer-alpha");
        string secondRoot = CreateRoot("unrelated-beta");

        Assert.Equal(
            Store.ResolveLeaseIdentity(firstRoot, SquadDeploymentScope.Global),
            Store.ResolveLeaseIdentity(secondRoot, SquadDeploymentScope.Global));
        Exception? contention = ExecuteWhileLeaseIsHeld(
            firstRoot,
            SquadDeploymentScope.Global,
            Install(secondRoot, SquadDeploymentScope.Global, AgentFile(SquadDeploymentScope.Global, "beta.toml", "beta")));

        AssertActiveLeaseConflict(contention);
        Assert.False(Files.FileExists(FileAt(secondRoot, AgentRelativePath(SquadDeploymentScope.Global, "beta.toml"))));
    }

    /// <summary>
    /// Project work is serialized per physical root, so projects at two distinct roots proceed independently. This
    /// guards against keying the project lease on the home store.
    /// </summary>
    [Fact]
    public void ProjectLeasesOnDistinctRootsDoNotContend()
    {
        string firstRoot = CreateRoot("project-alpha");
        string secondRoot = CreateRoot("project-beta");

        Assert.NotEqual(
            Store.ResolveLeaseIdentity(firstRoot, SquadDeploymentScope.Project),
            Store.ResolveLeaseIdentity(secondRoot, SquadDeploymentScope.Project));
        Exception? contention = ExecuteWhileLeaseIsHeld(
            firstRoot,
            SquadDeploymentScope.Project,
            Install(secondRoot, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "beta.toml", "beta")));

        Assert.Null(contention);
        Assert.Equal("beta", ReadFile(secondRoot, AgentRelativePath(SquadDeploymentScope.Project, "beta.toml")));
    }

    /// <summary>
    /// A project installed at the home root and global state share one lease, because global state is anchored on the
    /// home and the two use one transaction directory there. Global state is leased per home, so a project whose
    /// physical root is the home must contend with it.
    /// </summary>
    [Fact]
    public void ProjectAtHomeRootAndGlobalTransactionContendForOneLease()
    {
        string home = PhysicalOf(Home);

        Assert.Equal(
            Store.ResolveLeaseIdentity(home, SquadDeploymentScope.Project),
            Store.ResolveLeaseIdentity(home, SquadDeploymentScope.Global));
        Exception? contention = ExecuteWhileLeaseIsHeld(
            home,
            SquadDeploymentScope.Project,
            Install(home, SquadDeploymentScope.Global, AgentFile(SquadDeploymentScope.Global, "global.toml", "global")));

        AssertActiveLeaseConflict(contention);
        Assert.False(Files.FileExists(FileAt(home, AgentRelativePath(SquadDeploymentScope.Global, "global.toml"))));
    }

    /// <summary>
    /// A project installed at a root other than the home holds its own lease and does not contend with global state,
    /// which is leased on the home. Only a root that is the home shares that lease.
    /// </summary>
    [Fact]
    public void ProjectAtUnrelatedRootAndGlobalTransactionHoldIndependentLeases()
    {
        string root = CreateRoot("shared-root");

        Assert.NotEqual(
            Store.ResolveLeaseIdentity(root, SquadDeploymentScope.Project),
            Store.ResolveLeaseIdentity(root, SquadDeploymentScope.Global));
        Exception? contention = ExecuteWhileLeaseIsHeld(
            root,
            SquadDeploymentScope.Project,
            Install(root, SquadDeploymentScope.Global, AgentFile(SquadDeploymentScope.Global, "global.toml", "global")));

        Assert.Null(contention);
        Assert.Equal("global", ReadFile(root, AgentRelativePath(SquadDeploymentScope.Global, "global.toml")));
    }

    /// <summary>
    /// A project root reached through a symlink alias is the same physical root, so it must still contend for the one
    /// project lease. Independence applies across roots, not across aliases.
    /// </summary>
    [Fact]
    public void ProjectLeaseIsKeyedByPhysicalRootAcrossSymlinkAliases()
    {
        string root = CreateRoot("project");
        string alias = Path.Combine(ProjectsDirectory, "project-link");
        Files.CreateDirectoryLink(alias, root);

        Assert.Equal(
            Store.ResolveLeaseIdentity(root, SquadDeploymentScope.Project),
            Store.ResolveLeaseIdentity(alias, SquadDeploymentScope.Project));
        Exception? contention = ExecuteWhileLeaseIsHeld(
            root,
            SquadDeploymentScope.Project,
            Install(alias, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "beta.toml", "beta")));

        AssertActiveLeaseConflict(contention);
        Assert.False(Files.FileExists(FileAt(root, AgentRelativePath(SquadDeploymentScope.Project, "beta.toml"))));
    }

    /// <summary>
    /// A project receipt records the canonical physical root, not the legacy "." placeholder, so a receipt read from
    /// any alias names the same place the files were written.
    /// </summary>
    [Fact]
    public void WrittenProjectReceiptRecordsCanonicalPhysicalRoot()
    {
        string root = CreateRoot("project");
        string alias = Path.Combine(ProjectsDirectory, "project-link");
        Files.CreateDirectoryLink(alias, root);

        _state.Transaction().Execute(
            Install(alias, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "alpha.toml", "alpha")));

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            Store.ReadReceipt(root, SquadDeploymentScope.Project));
        Assert.Equal(PhysicalOf(root), receipt.TargetRoot);
    }

    /// <summary>
    /// A global receipt records the home anchor as its target root, because the global state it describes is anchored
    /// there rather than on whichever root installed it.
    /// </summary>
    [Fact]
    public void WrittenGlobalReceiptRecordsHomeAnchorAsTargetRoot()
    {
        string root = CreateRoot("global-target");

        _state.Transaction().Execute(
            Install(root, SquadDeploymentScope.Global, AgentFile(SquadDeploymentScope.Global, "alpha.toml", "alpha")));

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            Store.ReadReceipt(root, SquadDeploymentScope.Global));
        Assert.Equal(PhysicalOf(Home), receipt.TargetRoot);
    }

    /// <summary>
    /// A receipt written before this change carries the legacy "." target root, and it must still be read. Rejecting
    /// it would strand every existing project on upgrade.
    /// </summary>
    [Fact]
    public void LegacyDotTargetRootReceiptStillReads()
    {
        string root = CreateRoot("project");
        _state.Transaction().Execute(
            Install(root, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "alpha.toml", "alpha")));
        string receiptPath = Store.ResolveReceiptPath(root, SquadDeploymentScope.Project);
        JsonObject document = JsonNode.Parse(Files.ReadAllText(receiptPath))!.AsObject();
        document["targetRoot"] = ".";
        Files.WriteAllText(receiptPath, document.ToJsonString());

        SquadReceipt legacy = Assert.IsType<SquadReceipt>(
            Store.ReadReceipt(root, SquadDeploymentScope.Project));
        Assert.Equal(".", legacy.TargetRoot);
        Assert.Equal("alpha", ReadFile(root, AgentRelativePath(SquadDeploymentScope.Project, "alpha.toml")));
    }

    /// <summary>
    /// A transaction that fails after the home store exists must leave the home store exactly as it was. Without
    /// this, a half-finished install leaves directories behind that no registry entry or receipt explains. The
    /// precondition asserts the home store is written at all, so the test cannot pass on a layout that never touches
    /// home.
    /// </summary>
    [Theory]
    [InlineData(SquadDeploymentScope.Project)]
    [InlineData(SquadDeploymentScope.Global)]
    public void FailedTransactionAddsNoDirectoriesUnderTheHomeStore(SquadDeploymentScope scope)
    {
        string root = CreateRoot("target");
        _state.Transaction().Execute(
            Install(root, scope, AgentFile(scope, "alpha.toml", "alpha")));
        string homeStore = Path.Combine(PhysicalOf(Home), ".kyber-weave");
        Assert.True(
            Files.DirectoryExists(homeStore),
            "A committed transaction must write its state under the home store.");
        SortedSet<string> before = DirectoriesUnder(homeStore);
        _state.Time.SetUtcNow(UpdatedAt);
        SquadDeploymentPlan update = _state.Planner.CreateUpdate(
            root,
            scope,
            SquadStateTestLayout.Lock("1.2.4"),
            [AgentFile(scope, "alpha.toml", "beta")],
            Assert.IsType<SquadReceipt>(Store.ReadReceipt(root, scope)),
            [],
            replaceManaged: false,
            _state.Time);

        Exception? failure = Record.Exception(() => _state.Transaction(
            new FailAtStepObserver(SquadTransactionStepKind.FileStaged)).Execute(update));

        Assert.NotNull(failure);
        Assert.Equal(before, DirectoriesUnder(homeStore));
    }

    private string CreateRoot(string name)
    {
        string root = Path.Combine(ProjectsDirectory, name);
        Files.CreateDirectory(root);
        return PhysicalOf(root);
    }

    private string PhysicalOf(string path) => _state.Layout.PhysicalOf(path);

    private SquadStateStore StoreFor(string home) =>
        new(
            new FakeSquadUserPaths(_state.UserPaths.ApplicationDataDirectory, home),
            Files,
            _state.Semantics,
            _state.Paths,
            _state.Environment,
            new InProcessLease(_state.LeaseHost),
            TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// The harness-relative path a fixture renders one agent to. A global install renders relative to its target's
    /// global root and takes a bare path; a project install keeps the target directory. A dot-prefixed global path
    /// would be read back as the legacy single-root layout (#91), so it is never used for a global install.
    /// </summary>
    private static string AgentRelativePath(SquadDeploymentScope scope, string fileName) =>
        scope == SquadDeploymentScope.Global
            ? $"agents/{fileName}"
            : $".codex/agents/{fileName}";

    private static SquadDeploymentFile AgentFile(
        SquadDeploymentScope scope,
        string fileName,
        string content) =>
        Rendered(AgentRelativePath(scope, fileName), content);

    private static string FileAt(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string ReadFile(string root, string relativePath) =>
        Files.ReadAllText(FileAt(root, relativePath));

    private SortedSet<string> DirectoriesUnder(string root) =>
        new(
            SquadStateTreeCapture.Capture(Files, root)
                .Where(entry => entry.Value.Kind == SquadTreeEntryKind.Directory)
                .Select(entry => entry.Key),
            StringComparer.Ordinal);

    private SquadDeploymentPlan Install(
        string root,
        SquadDeploymentScope scope,
        params SquadDeploymentFile[] files) =>
        _state.Planner.CreateInstall(
            root,
            scope,
            SquadStateTestLayout.Lock(),
            files,
            [],
            adopt: false,
            _state.Time);

    /// <summary>
    /// Runs a contender transaction while a lease of one deployment root is held, as another process holds it while
    /// its transaction runs. The contender's refusal, or its success, is the answer.
    /// </summary>
    private Exception? ExecuteWhileLeaseIsHeld(
        string leaseRoot,
        SquadDeploymentScope leaseScope,
        SquadDeploymentPlan contenderPlan)
    {
        string name = SquadLeaseNames.ForRoot(Store.ResolveLeaseIdentity(leaseRoot, leaseScope));
        using (new InProcessLease(_state.LeaseHost).Hold(name))
            return Record.Exception(() => _state.Transaction().Execute(contenderPlan));
    }

    private static void AssertActiveLeaseConflict(Exception? exception)
    {
        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains("active", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SquadDeploymentFile Rendered(
        string relativePath,
        string content,
        string target = "codex") =>
        new(relativePath, Encoding.UTF8.GetBytes(content), target);

    private sealed class FailAtStepObserver(SquadTransactionStepKind failingStep) : ISquadTransactionObserver
    {
        public void AfterStep(SquadTransactionStep transactionStep)
        {
            if (transactionStep.Kind == failingStep)
                throw new InvalidOperationException("Injected failure for the home-store residue test.");
        }
    }
}
