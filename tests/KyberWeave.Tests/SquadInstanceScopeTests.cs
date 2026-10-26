using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the instance deployment scope contract and root resolution rules.
/// </summary>
/// <remarks>
/// Acceptance Criteria 7.3 specifies that Squad SHALL give each harness and preset pair its own
/// instance folder under ~/.kyber-weave/. An instance deployment is isolated from both project
/// repositories and the machine-wide global harness configurations. Its state lives under
/// <c>~/.kyber-weave/squad/instance-state/&lt;target&gt;/&lt;preset&gt;/</c>, it is never registered in
/// <c>registry.json</c>, migration ignores it, and rendering lowers paths under the instance root
/// via <see cref="SquadInstanceRootResolver" />.
/// </remarks>
public sealed class SquadInstanceScopeTests : IDisposable
{
    private static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Verifies that SquadDeploymentScope declares the Instance enum member as distinct from Project and Global.
    /// </summary>
    [Fact]
    public void SquadDeploymentScopeInstanceEnumIsDefinedAndDistinct()
    {
        Assert.True(Enum.IsDefined(typeof(SquadDeploymentScope), "Instance"));
        Assert.NotEqual(SquadDeploymentScope.Project, SquadDeploymentScope.Instance);
        Assert.NotEqual(SquadDeploymentScope.Global, SquadDeploymentScope.Instance);
    }

    /// <summary>
    /// Verifies that a valid instance root under ~/.kyber-weave/squad/instances/target/preset/
    /// resolves state, lock, receipt, and transaction journal paths into the corresponding
    /// instance-state directory.
    /// </summary>
    [Fact]
    public void ValidInstanceRootResolvesStateLockAndReceiptUnderInstanceState()
    {
        string applicationData = Path.Combine(_temp.Path, "app-data");
        FakeUserPaths userPaths = new(applicationData);
        Directory.CreateDirectory(userPaths.HomeDirectory);
        SquadStateStore store = new(userPaths);

        string instanceRoot = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instances",
            "claude",
            "default");
        Directory.CreateDirectory(instanceRoot);

        string expectedStateDir = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instance-state",
            "claude",
            "default");

        string stateDirectory = store.ResolveStateDirectory(instanceRoot, SquadDeploymentScope.Instance);
        string lockPath = store.ResolveLockPath(instanceRoot, SquadDeploymentScope.Instance);
        string receiptPath = store.ResolveReceiptPath(instanceRoot, SquadDeploymentScope.Instance);
        string transactionDir = store.ResolveTransactionDirectory(instanceRoot, SquadDeploymentScope.Instance);

        Assert.Equal(expectedStateDir, stateDirectory);
        Assert.Equal(Path.Combine(expectedStateDir, "squad.lock.yml"), lockPath);
        Assert.Equal(Path.Combine(expectedStateDir, "squad.receipt.json"), receiptPath);
        Assert.Equal(Path.Combine(expectedStateDir, ".squad-transaction"), transactionDir);
    }

    /// <summary>
    /// An instance root must strictly adhere to &lt;home&gt;/.kyber-weave/squad/instances/&lt;target&gt;/&lt;preset&gt;/.
    /// Any other root must be rejected with ArgumentException to prevent state misplacement.
    /// </summary>
    [Theory]
    [InlineData("outside-home")]
    [InlineData("missing-preset")]
    [InlineData("instances-base-only")]
    [InlineData("relative-path")]
    public void InvalidInstanceRootThrowsArgumentException(string scenario)
    {
        string applicationData = Path.Combine(_temp.Path, "app-data");
        FakeUserPaths userPaths = new(applicationData);
        Directory.CreateDirectory(userPaths.HomeDirectory);
        SquadStateStore store = new(userPaths);

        string invalidRoot = scenario switch
        {
            "outside-home" => Path.Combine(_temp.Path, "custom-location"),
            "missing-preset" => Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "instances", "claude"),
            "instances-base-only" => Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "instances"),
            "relative-path" => Path.Combine(".kyber-weave", "squad", "instances", "claude", "default"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };

        Assert.Throws<ArgumentException>(() => store.ResolveStateDirectory(invalidRoot, SquadDeploymentScope.Instance));
        Assert.Throws<ArgumentException>(() => store.ResolveLockPath(invalidRoot, SquadDeploymentScope.Instance));
        Assert.Throws<ArgumentException>(() => store.ResolveReceiptPath(invalidRoot, SquadDeploymentScope.Instance));
        Assert.Throws<ArgumentException>(() => store.ResolveTransactionDirectory(invalidRoot, SquadDeploymentScope.Instance));
    }

    /// <summary>
    /// Executing an instance deployment plan deploys native harness files to the instance root
    /// and places state files under instance-state, keeping the instance directory free of transaction journals.
    /// </summary>
    [Fact]
    public void ExecuteInstanceTransactionDeploysToInstanceRootAndStoresStateUnderInstanceState()
    {
        string applicationData = Path.Combine(_temp.Path, "app-data");
        FakeUserPaths userPaths = new(applicationData);
        Directory.CreateDirectory(userPaths.HomeDirectory);
        SquadStateStore store = new(userPaths);

        string instanceRoot = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instances",
            "claude",
            "default");
        Directory.CreateDirectory(instanceRoot);

        SquadLock squadLock = CreateLock();
        SquadDeploymentFile renderedAgent = new(
            "agents/conductor.md",
            Encoding.UTF8.GetBytes("# Conductor Agent Content"),
            "claude");

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            instanceRoot,
            SquadDeploymentScope.Instance,
            squadLock,
            [renderedAgent],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));

        SquadTransaction transaction = new(store);
        transaction.Execute(plan);

        string deployedFile = Path.Combine(instanceRoot, "agents", "conductor.md");
        Assert.True(File.Exists(deployedFile));
        Assert.Equal("# Conductor Agent Content", File.ReadAllText(deployedFile));

        string expectedStateDir = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instance-state",
            "claude",
            "default");
        Assert.True(File.Exists(Path.Combine(expectedStateDir, "squad.lock.yml")));
        Assert.True(File.Exists(Path.Combine(expectedStateDir, "squad.receipt.json")));

        Assert.False(Directory.Exists(Path.Combine(instanceRoot, ".squad-transaction")));
        Assert.False(Directory.Exists(Path.Combine(instanceRoot, ".kyber-weave")));
    }

    /// <summary>
    /// Instance deployments are personal ephemeral configurations and must never be registered
    /// in the shared ~/.kyber-weave/squad/registry.json file.
    /// </summary>
    [Fact]
    public void ExecuteInstanceTransactionNeverRegistersInProjectRegistry()
    {
        string applicationData = Path.Combine(_temp.Path, "app-data");
        FakeUserPaths userPaths = new(applicationData);
        Directory.CreateDirectory(userPaths.HomeDirectory);
        SquadStateStore store = new(userPaths);

        string instanceRoot = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instances",
            "claude",
            "default");
        Directory.CreateDirectory(instanceRoot);

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            instanceRoot,
            SquadDeploymentScope.Instance,
            CreateLock(),
            [new SquadDeploymentFile("agents/conductor.md", Encoding.UTF8.GetBytes("# Body"), "claude")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));

        SquadTransaction transaction = new(store);
        transaction.Execute(plan);

        IReadOnlyList<SquadProjectRegistryEntry> projects = store.ListProjects();
        Assert.DoesNotContain(projects, project => string.Equals(project.Path, instanceRoot, StringComparison.Ordinal));

        string registryPath = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "registry.json");
        if (File.Exists(registryPath))
        {
            string registryContent = File.ReadAllText(registryPath);
            Assert.DoesNotContain("instances", registryContent, StringComparison.Ordinal);
            Assert.DoesNotContain("claude/default", registryContent, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// State migration operates only across legacy global state and project state; it must ignore
    /// instance-state directories entirely.
    /// </summary>
    [Fact]
    public void SquadStateMigrationIgnoresInstanceStateDirectory()
    {
        string applicationData = Path.Combine(_temp.Path, "app-data");
        FakeUserPaths userPaths = new(applicationData);
        Directory.CreateDirectory(userPaths.HomeDirectory);
        SquadStateStore store = new(userPaths);

        string instanceStateDir = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instance-state",
            "claude",
            "default");
        Directory.CreateDirectory(instanceStateDir);
        string lockFile = Path.Combine(instanceStateDir, "squad.lock.yml");
        string receiptFile = Path.Combine(instanceStateDir, "squad.receipt.json");
        File.WriteAllText(lockFile, store.SerializeLock(CreateLock()));
        File.WriteAllText(receiptFile, store.SerializeReceipt(new SquadReceipt(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Instance,
            Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "instances", "claude", "default"),
            InstalledAt,
            [],
            [])));

        SquadStateMigration migration = new(store, userPaths);
        SquadMigrationReport report = migration.MigrateGlobal(userPaths.HomeDirectory);

        Assert.True(File.Exists(lockFile));
        Assert.True(File.Exists(receiptFile));
        Assert.Empty(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
        Assert.Empty(report.ImportedProjects);
    }

    /// <summary>
    /// SquadInstanceRootResolver implements ISquadGlobalRootResolver for an instance deployment:
    /// it resolves the configured instance root for the matching target, and throws for any other target.
    /// </summary>
    [Fact]
    public void SquadInstanceRootResolverResolvesTargetAndThrowsForOtherTargets()
    {
        string instanceRoot = Path.Combine(_temp.Path, "instance-root");
        SquadInstanceRootResolver resolver = new(instanceRoot, SquadTarget.Claude);

        Assert.Equal(instanceRoot, resolver.ResolveGlobalRoot(SquadTarget.Claude));
        Action codexAction = () => resolver.ResolveGlobalRoot(SquadTarget.Codex);
        Action copilotAction = () => resolver.ResolveGlobalRoot(SquadTarget.Copilot);
        Assert.Throws<ArgumentException>(codexAction);
        Assert.Throws<ArgumentException>(copilotAction);
    }

    /// <summary>
    /// Rendering with Scope = Global using SquadInstanceRootResolver produces bare agents/ paths
    /// (e.g. agents/conductor.md) rather than project-scoped prefixed paths (.claude/agents/conductor.md),
    /// which deploy directly into the instance directory.
    /// </summary>
    [Fact]
    public async Task ClaudeInstanceRendersAgentsUnderInstanceRoot()
    {
        string applicationData = Path.Combine(_temp.Path, "app-data");
        FakeUserPaths userPaths = new(applicationData);
        string instanceRoot = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instances",
            "claude",
            "default");
        Directory.CreateDirectory(instanceRoot);

        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadInstanceRootResolver resolver = new(instanceRoot, SquadTarget.Claude);

        SquadRenderRequest request = new(
            SourceDirectory: Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad"),
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Global,
            UserScopeDirectory: userPaths.ApplicationDataDirectory,
            GlobalRoots: resolver);

        SquadRenderResult result = await renderer.RenderAsync(request);

        Assert.Contains(result.Files, file =>
            string.Equals(file.RelativePath, "agents/conductor.md", StringComparison.Ordinal) &&
            string.Equals(file.Target, "claude", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Files, file => file.RelativePath.StartsWith(".claude/", StringComparison.Ordinal));
    }

    /// <summary>
    /// For harness renderers that generate absolute reference links (such as Copilot), rendering with
    /// SquadInstanceRootResolver must ensure that absolute links point inside the instance root rather
    /// than the machine-wide global directory.
    /// </summary>
    [Fact]
    public async Task CopilotInstanceAbsoluteLinksPointInsideInstanceRoot()
    {
        string applicationData = Path.Combine(_temp.Path, "app-data");
        FakeUserPaths userPaths = new(applicationData);
        string instanceRoot = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instances",
            "copilot",
            "default");
        Directory.CreateDirectory(instanceRoot);

        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadInstanceRootResolver resolver = new(instanceRoot, SquadTarget.Copilot);

        SquadRenderRequest request = new(
            SourceDirectory: Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad"),
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Global,
            UserScopeDirectory: userPaths.ApplicationDataDirectory,
            GlobalRoots: resolver);

        SquadRenderResult result = await renderer.RenderAsync(request);

        SquadDeploymentFile conductorFile = Assert.Single(
            result.Files,
            file => string.Equals(file.RelativePath, "agents/conductor.agent.md", StringComparison.Ordinal));

        string content = Encoding.UTF8.GetString(conductorFile.Content.Span);
        string normalizedInstanceRoot = instanceRoot.Replace('\\', '/').TrimEnd('/');
        string expectedLink = $"]({normalizedInstanceRoot}/agents/conductor/references/intake-path.md)";

        Assert.Contains(expectedLink, content, StringComparison.Ordinal);
        Assert.DoesNotContain(".copilot", content, StringComparison.Ordinal);
    }

    private static SquadLock CreateLock(string version = "1.2.3") =>
        new(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["claude"],
            [],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FakeUserPaths(string appDataDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = appDataDirectory;

        public string HomeDirectory => Path.Combine(ApplicationDataDirectory, "home");
    }
}
