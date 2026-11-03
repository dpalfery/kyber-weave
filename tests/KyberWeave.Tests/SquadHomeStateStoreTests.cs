using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins where home-level Squad state lives, how each transaction journal is anchored, and which
/// lease guards a transaction. Global state is anchored on the user's home directory and never on
/// a target root, so two unrelated roots cannot each keep a private copy of one harness file's
/// ownership receipt.
/// </summary>
/// <remarks>
/// Every fake points <see cref="ISquadUserPaths.HomeDirectory"/> beneath a temp fixture, and the
/// application data directory is beneath it too. No test here may touch the real home, the real
/// application data directory, or a harness directory.
/// </remarks>
public sealed class SquadHomeStateStoreTests
{
    private static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void GlobalLockAndReceiptAreHomeAnchoredForAnyTargetRoot()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string firstRoot = Path.Combine(fixture.Path, "customer-alpha");
        string secondRoot = Path.Combine(fixture.Path, "unrelated-beta");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        SquadStateStore store = StoreFor(home);
        string globalState = Path.Combine(PhysicalPathOf(home), ".kyber-weave", "squad", "global");

        AssertSameLocation(
            Path.Combine(globalState, "squad.lock.yml"),
            store.ResolveLockPath(firstRoot, SquadDeploymentScope.Global));
        AssertSameLocation(
            Path.Combine(globalState, "squad.receipt.json"),
            store.ResolveReceiptPath(firstRoot, SquadDeploymentScope.Global));
        AssertSameLocation(
            Path.Combine(globalState, "squad.lock.yml"),
            store.ResolveLockPath(secondRoot, SquadDeploymentScope.Global));
        AssertSameLocation(
            Path.Combine(globalState, "squad.receipt.json"),
            store.ResolveReceiptPath(secondRoot, SquadDeploymentScope.Global));
    }

    [Fact]
    public void HomeStateDirectoryIsDotKyberWeaveSquadBeneathTheUserHome()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        SquadStateStore store = StoreFor(home);

        AssertSameLocation(
            Path.Combine(PhysicalPathOf(home), ".kyber-weave", "squad"),
            store.ResolveHomeStateDirectory());
    }

    /// <summary>
    /// Project state is keyed by the physical root, so a lexical alias and a symlink alias of one
    /// project must address the same lock and receipt, under the home store rather than the root.
    /// </summary>
    [Fact]
    public void ProjectStateIsKeyedByPhysicalRootUnderHomeAndAliasesShareOneKey()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string root = Path.Combine(fixture.Path, "project");
        string symlinkAlias = Path.Combine(fixture.Path, "project-link");
        Directory.CreateDirectory(root);
        Directory.CreateSymbolicLink(symlinkAlias, root);
        string lexicalAlias = Path.Combine(root, "..", "project");
        SquadStateStore store = StoreFor(home);
        string key = SquadPhysicalRootIdentity.Resolve(root).Key;
        string projectState = Path.Combine(
            PhysicalPathOf(home),
            ".kyber-weave",
            "squad",
            "projects",
            key);

        AssertSameLocation(
            Path.Combine(projectState, "squad.lock.yml"),
            store.ResolveLockPath(root, SquadDeploymentScope.Project));
        AssertSameLocation(
            Path.Combine(projectState, "squad.receipt.json"),
            store.ResolveReceiptPath(root, SquadDeploymentScope.Project));
        AssertSameLocation(projectState, store.ResolveStateDirectory(root, SquadDeploymentScope.Project));
        Assert.Equal(
            store.ResolveLockPath(root, SquadDeploymentScope.Project),
            store.ResolveLockPath(symlinkAlias, SquadDeploymentScope.Project));
        Assert.Equal(
            store.ResolveReceiptPath(root, SquadDeploymentScope.Project),
            store.ResolveReceiptPath(lexicalAlias, SquadDeploymentScope.Project));
    }

    /// <summary>
    /// The project journal stays beside its root, so a project transaction never depends on where
    /// the home store lives. The global journal moves with the home anchor, not with a target root.
    /// </summary>
    [Fact]
    public void TransactionJournalsStayBesideTheirRootOrUnderHome()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string otherHome = CreateHome(fixture, "other-home");
        string root = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(root);
        SquadStateStore store = StoreFor(home);
        string projectJournal = Path.Combine(
            PhysicalPathOf(root),
            ".kyber-weave",
            ".squad-transaction");
        string globalJournal = Path.Combine(
            PhysicalPathOf(home),
            ".kyber-weave",
            ".squad-transaction");

        AssertSameLocation(projectJournal, store.ResolveTransactionDirectory(root, SquadDeploymentScope.Project));
        AssertSameLocation(projectJournal, store.ResolveTransactionWorkDirectory(root, SquadDeploymentScope.Project));
        AssertSameLocation(globalJournal, store.ResolveTransactionDirectory(root, SquadDeploymentScope.Global));
        AssertSameLocation(globalJournal, store.ResolveTransactionWorkDirectory(root, SquadDeploymentScope.Global));
        AssertSameLocation(
            projectJournal,
            StoreFor(otherHome).ResolveTransactionDirectory(root, SquadDeploymentScope.Project));
    }

    /// <summary>
    /// Global work is serialized per home, so two unrelated target roots cannot both hold the global
    /// lease. Before this, the lease was keyed per target root and they never contended.
    /// </summary>
    [Fact]
    public async Task GlobalLeaseIsKeyedByHomeSoUnrelatedTargetRootsContend()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string firstRoot = Path.Combine(fixture.Path, "customer-alpha");
        string secondRoot = Path.Combine(fixture.Path, "unrelated-beta");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        SquadStateStore store = StoreFor(home);

        Exception? contention = await ExecuteWhileLeaseIsHeld(
            store,
            Install(firstRoot, SquadDeploymentScope.Global, AgentFile(SquadDeploymentScope.Global, "alpha.toml", "alpha")),
            Install(secondRoot, SquadDeploymentScope.Global, AgentFile(SquadDeploymentScope.Global, "beta.toml", "beta")));

        AssertActiveLeaseConflict(contention);
        Assert.False(File.Exists(FileAt(secondRoot, AgentRelativePath(SquadDeploymentScope.Global, "beta.toml"))));
    }

    /// <summary>
    /// Project work is serialized per physical root, so projects at two distinct roots proceed
    /// independently. This guards against keying the project lease on the home store.
    /// </summary>
    [Fact]
    public async Task ProjectLeasesOnDistinctRootsDoNotContend()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string firstRoot = Path.Combine(fixture.Path, "project-alpha");
        string secondRoot = Path.Combine(fixture.Path, "project-beta");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        SquadStateStore store = StoreFor(home);

        Exception? contention = await ExecuteWhileLeaseIsHeld(
            store,
            Install(firstRoot, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "alpha.toml", "alpha")),
            Install(secondRoot, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "beta.toml", "beta")));

        Assert.Null(contention);
        Assert.Equal("beta", ReadFile(secondRoot, AgentRelativePath(SquadDeploymentScope.Project, "beta.toml")));
    }

    /// <summary>
    /// A project installed at the home root and global state share one lease, because global state is
    /// anchored on the home and the two use one transaction directory there. Global state is leased per
    /// home, so a project whose physical root is the home must contend with it.
    /// </summary>
    [Fact]
    public async Task ProjectAtHomeRootAndGlobalTransactionContendForOneLease()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        SquadStateStore store = StoreFor(home);

        Exception? contention = await ExecuteWhileLeaseIsHeld(
            store,
            Install(home, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "project.toml", "project")),
            Install(home, SquadDeploymentScope.Global, AgentFile(SquadDeploymentScope.Global, "global.toml", "global")));

        AssertActiveLeaseConflict(contention);
        Assert.False(File.Exists(FileAt(home, AgentRelativePath(SquadDeploymentScope.Global, "global.toml"))));
    }

    /// <summary>
    /// A project installed at a root other than the home holds its own lease and does not contend with
    /// global state, which is leased on the home. Only a root that is the home shares that lease.
    /// </summary>
    [Fact]
    public async Task ProjectAtUnrelatedRootAndGlobalTransactionHoldIndependentLeases()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string root = Path.Combine(fixture.Path, "shared-root");
        Directory.CreateDirectory(root);
        SquadStateStore store = StoreFor(home);

        Exception? contention = await ExecuteWhileLeaseIsHeld(
            store,
            Install(root, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "project.toml", "project")),
            Install(root, SquadDeploymentScope.Global, AgentFile(SquadDeploymentScope.Global, "global.toml", "global")));

        Assert.Null(contention);
        Assert.Equal("global", ReadFile(root, AgentRelativePath(SquadDeploymentScope.Global, "global.toml")));
    }

    /// <summary>
    /// A project root reached through a symlink alias is the same physical root, so it must still
    /// contend for the one project lease. Independence applies across roots, not across aliases.
    /// </summary>
    [Fact]
    public async Task ProjectLeaseIsKeyedByPhysicalRootAcrossSymlinkAliases()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string root = Path.Combine(fixture.Path, "project");
        string alias = Path.Combine(fixture.Path, "project-link");
        Directory.CreateDirectory(root);
        Directory.CreateSymbolicLink(alias, root);
        SquadStateStore store = StoreFor(home);

        Exception? contention = await ExecuteWhileLeaseIsHeld(
            store,
            Install(root, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "alpha.toml", "alpha")),
            Install(alias, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "beta.toml", "beta")));

        AssertActiveLeaseConflict(contention);
        Assert.False(File.Exists(FileAt(root, AgentRelativePath(SquadDeploymentScope.Project, "beta.toml"))));
    }

    /// <summary>
    /// A project receipt records the canonical physical root, not the legacy "." placeholder, so a
    /// receipt read from any alias names the same place the files were written.
    /// </summary>
    [Fact]
    public void WrittenProjectReceiptRecordsCanonicalPhysicalRoot()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string root = Path.Combine(fixture.Path, "project");
        string alias = Path.Combine(fixture.Path, "project-link");
        Directory.CreateDirectory(root);
        Directory.CreateSymbolicLink(alias, root);
        SquadStateStore store = StoreFor(home);

        new SquadTransaction(store).Execute(
            Install(alias, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "alpha.toml", "alpha")));

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(root, SquadDeploymentScope.Project));
        Assert.Equal(PhysicalPathOf(root), receipt.TargetRoot);
    }

    /// <summary>
    /// A global receipt records the home anchor as its target root, because the global state it
    /// describes is anchored there rather than on whichever root installed it.
    /// </summary>
    [Fact]
    public void WrittenGlobalReceiptRecordsHomeAnchorAsTargetRoot()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string root = Path.Combine(fixture.Path, "global-target");
        Directory.CreateDirectory(root);
        SquadStateStore store = StoreFor(home);

        new SquadTransaction(store).Execute(
            Install(root, SquadDeploymentScope.Global, AgentFile(SquadDeploymentScope.Global, "alpha.toml", "alpha")));

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(root, SquadDeploymentScope.Global));
        Assert.Equal(PhysicalPathOf(home), receipt.TargetRoot);
    }

    /// <summary>
    /// A receipt written before this change carries the legacy "." target root, and it must still be
    /// read. Rejecting it would strand every existing project on upgrade.
    /// </summary>
    [Fact]
    public void LegacyDotTargetRootReceiptStillReads()
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string root = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(root);
        SquadStateStore store = StoreFor(home);
        new SquadTransaction(store).Execute(
            Install(root, SquadDeploymentScope.Project, AgentFile(SquadDeploymentScope.Project, "alpha.toml", "alpha")));
        string receiptPath = store.ResolveReceiptPath(root, SquadDeploymentScope.Project);
        JsonObject document = JsonNode.Parse(File.ReadAllText(receiptPath, Encoding.UTF8))!.AsObject();
        document["targetRoot"] = ".";
        File.WriteAllText(receiptPath, document.ToJsonString(), new UTF8Encoding(false));

        SquadReceipt legacy = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(root, SquadDeploymentScope.Project));
        Assert.Equal(".", legacy.TargetRoot);
        Assert.Equal("alpha", ReadFile(root, AgentRelativePath(SquadDeploymentScope.Project, "alpha.toml")));
    }

    /// <summary>
    /// A transaction that fails after the home store exists must leave the home store exactly as it
    /// was. Without this, a half-finished install leaves directories behind that no registry entry
    /// or receipt explains. The precondition asserts the home store is written at all, so the test
    /// cannot pass on a layout that never touches home.
    /// </summary>
    [Theory]
    [InlineData(SquadDeploymentScope.Project)]
    [InlineData(SquadDeploymentScope.Global)]
    public void FailedTransactionAddsNoDirectoriesUnderTheHomeStore(SquadDeploymentScope scope)
    {
        using TempDirectory fixture = new TempDirectory();
        string home = CreateHome(fixture);
        string root = Path.Combine(fixture.Path, "target");
        Directory.CreateDirectory(root);
        SquadStateStore store = StoreFor(home);
        new SquadTransaction(store).Execute(
            Install(root, scope, AgentFile(scope, "alpha.toml", "alpha")));
        string homeStore = Path.Combine(PhysicalPathOf(home), ".kyber-weave");
        Assert.True(
            Directory.Exists(homeStore),
            "A committed transaction must write its state under the home store.");
        SortedSet<string> before = DirectoriesUnder(homeStore);
        SquadDeploymentPlan update = SquadDeploymentPlan.CreateUpdate(
            root,
            scope,
            Lock("1.2.4"),
            [AgentFile(scope, "alpha.toml", "beta")],
            Assert.IsType<SquadReceipt>(store.ReadReceipt(root, scope)),
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        Exception? failure = Record.Exception(() => new SquadTransaction(
            store,
            new FailAtStepObserver(SquadTransactionStepKind.FileStaged)).Execute(update));

        Assert.NotNull(failure);
        Assert.Equal(before, DirectoriesUnder(homeStore));
    }

    private static string CreateHome(TempDirectory fixture, string name = "home")
    {
        string home = Path.Combine(fixture.Path, name);
        Directory.CreateDirectory(home);
        return home;
    }

    private static string PhysicalPathOf(string path) =>
        SquadPhysicalRootIdentity.Resolve(path).PhysicalPath;

    private static SquadStateStore StoreFor(string home) =>
        new(new HomeOnlyUserPaths(home));

    /// <summary>
    /// Compares two locations by where they physically are. A path may be spelled through a symlinked
    /// temp root (<c>/tmp</c> resolving to <c>/private/tmp</c>) and still name one place, so the
    /// comparison is on the canonical form, not the spelling. Exact equality is kept only where a
    /// receipt records a root, because that value is written and must be canonical.
    /// </summary>
    private static void AssertSameLocation(string expected, string actual) =>
        Assert.Equal(CanonicalLocation(expected), CanonicalLocation(actual));

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

    /// <summary>
    /// The harness-relative path a fixture renders one agent to. A global install renders relative to
    /// its target's global root and takes a bare path; a project install keeps the target directory.
    /// A dot-prefixed global path would be read back as the legacy single-root layout (#91), so it is
    /// never used for a global install.
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

    private static string ReadFile(string root, string relativePath) =>
        File.ReadAllText(FileAt(root, relativePath));

    private static SortedSet<string> DirectoriesUnder(string root) =>
        Directory.Exists(root)
            ? new SortedSet<string>(
                Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(root, path).Replace(
                        Path.DirectorySeparatorChar,
                        '/')),
                StringComparer.Ordinal)
            : new SortedSet<string>(StringComparer.Ordinal);

    private static SquadDeploymentPlan Install(
        string root,
        SquadDeploymentScope scope,
        params SquadDeploymentFile[] files) =>
        SquadDeploymentPlan.CreateInstall(
            root,
            scope,
            Lock(),
            files,
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));

    private static async Task<Exception?> ExecuteWhileLeaseIsHeld(
        SquadStateStore store,
        SquadDeploymentPlan activePlan,
        SquadDeploymentPlan contenderPlan)
    {
        using LeaseHoldingObserver holder = new LeaseHoldingObserver(SquadTransactionStepKind.IntentWritten);
        Task active = Task.Run(() => new SquadTransaction(store, holder).Execute(activePlan));
        try
        {
            Assert.True(
                holder.Reached.Wait(TimeSpan.FromSeconds(5)),
                "The active transaction did not reach its durable intent, so it never held a lease.");
            return Record.Exception(() => new SquadTransaction(store).Execute(contenderPlan));
        }
        finally
        {
            holder.Release.Set();
            await active;
        }
    }

    private static void AssertActiveLeaseConflict(Exception? exception)
    {
        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains("active", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

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

    private sealed class HomeOnlyUserPaths(string homeDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } =
            Path.Combine(Path.GetDirectoryName(homeDirectory)!, "application-data");

        public string HomeDirectory { get; } = homeDirectory;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class LeaseHoldingObserver(SquadTransactionStepKind heldAt) :
        ISquadTransactionObserver,
        IDisposable
    {
        public ManualResetEventSlim Reached { get; } = new(initialState: false);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public void AfterStep(SquadTransactionStep transactionStep)
        {
            if (transactionStep.Kind != heldAt)
                return;

            Reached.Set();
            Release.Wait();
        }

        public void Dispose()
        {
            Reached.Dispose();
            Release.Dispose();
        }
    }

    private sealed class FailAtStepObserver(SquadTransactionStepKind failingStep) : ISquadTransactionObserver
    {
        public void AfterStep(SquadTransactionStep transactionStep)
        {
            if (transactionStep.Kind == failingStep)
                throw new InvalidOperationException("Injected failure for the home-store residue test.");
        }
    }
}
