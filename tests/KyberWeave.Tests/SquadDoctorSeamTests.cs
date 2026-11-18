using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>squad doctor</c> reads its files through the file-system seam and writes every line to the injected console,
/// so a unit test states the workspace and asserts the output without a temporary directory or a captured process.
/// </summary>
public sealed class SquadDoctorSeamTests
{
    private const string Workspace = "/doctor-work";

    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new();
    private readonly InMemoryFileSystem _files = new();

    /// <summary>A workspace with no Devin marker in the injected file system is reported as not checked.</summary>
    [Fact]
    public void Execute_NoDevinMarkerInTheInjectedFileSystem_SkipsTheDevinCheck()
    {
        int exit = Execute(out string output);

        Assert.Equal(0, exit);
        Assert.Contains("Devin duplicate imports: not checked (no '.devin/' in this directory)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Devin marker that exists only in the injected file system makes the check run and report none, so the
    /// answer comes from the seam and not from the developer's disk.
    /// </summary>
    [Fact]
    public void Execute_DevinMarkerInTheInjectedFileSystem_RunsTheDevinCheck()
    {
        _files.CreateDirectory($"{Workspace}/.devin");

        int exit = Execute(out string output);

        Assert.Equal(0, exit);
        Assert.Contains("Devin duplicate imports: none", output, StringComparison.Ordinal);
        Assert.DoesNotContain("not checked (no '.devin/'", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The state migration is given the environment seam's current directory, not the process's, so a run anchored
    /// elsewhere is reproducible in a test.
    /// </summary>
    [Fact]
    public void Execute_MigrationRunsFromTheEnvironmentSeamsCurrentDirectory()
    {
        RecordingMigration migration = new();

        _ = Execute(out _, migration);

        Assert.Equal("/launched-from", migration.InvokingCwd);
    }

    /// <summary>Nothing the doctor reports goes to the standard output the markup console does not own.</summary>
    [Fact]
    public void Execute_WritesNothingToStandardOutput()
    {
        _ = Execute(out _);

        Assert.Equal(string.Empty, _streams.Output);
    }

    private int Execute(out string output) => Execute(out output, new RecordingMigration());

    private int Execute(out string output, RecordingMigration migration)
    {
        SquadDoctorCommand command = Doctor(migration);
        int exit = command.Execute(
            null!,
            new SquadDoctorSettings { Path = Workspace, Global = false },
            CancellationToken.None);

        output = _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
        return exit;
    }

    private SquadDoctorCommand Doctor(RecordingMigration migration)
    {
        FakeProcessExecutor executor = new();
        executor.WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        return new SquadDoctorCommand(
            new EmptyStateStore(),
            migration,
            new FixedGlobalRoots(),
            new FakeSquadRenderer(),
            new EmptyPresetStore(),
            new UnusedPlanner(),
            new SilentGuidance(),
            new FakeProcessEnvironment(currentDirectory: "/launched-from", readVariable: _ => null),
            new NoSourceLocator(),
            new UnusedSourceLoader(),
            new NoShadowing(),
            new NoDevinOverlap(),
            new NoZCodeMcp(),
            new SquadDoctorOptions(executor, Workspace),
            SquadFileSystemPathSemantics.Instance,
            new FakeKyberWeaveConfigRepository(),
            new SquadConsole(_ansi, _streams),
            _files);
    }

    private sealed class EmptyStateStore : ISquadStateStore
    {
        public ISquadUserPaths UserPaths { get; } = new FakeSquadUserPaths("/doctor-state");

        public string ResolveStateDirectory(string targetRoot, SquadDeploymentScope scope) => throw Unused();

        public string ResolveHomeStateDirectory() => throw Unused();

        public string ResolveHomeOwnedFilesRoot() => throw Unused();

        public IReadOnlyList<SquadProjectRegistryEntry> ListProjects() => [];

        public SquadProjectRegistryEntry? FindProject(string root) => null;

        public IReadOnlyList<SquadProjectRegistryEntry> ListProjectsWithoutRebuild() => [];

        public void RegisterProject(string root, IReadOnlyList<string> targets, DateTimeOffset now) => throw Unused();

        public bool UnregisterProject(string root) => throw Unused();

        public string SerializeLock(SquadLock squadLock) => throw Unused();

        public SquadLock DeserializeLock(string yaml) => throw Unused();

        public string SerializeReceipt(SquadReceipt receipt) => throw Unused();

        public SquadReceipt DeserializeReceipt(string json) => throw Unused();

        public SquadReceipt? ReadReceipt(string targetRoot, SquadDeploymentScope scope) => null;

        public SquadLock? ReadLock(string targetRoot, SquadDeploymentScope scope) => null;

        public string ResolveLockPath(string targetRoot, SquadDeploymentScope scope) => throw Unused();

        public string ResolveReceiptPath(string targetRoot, SquadDeploymentScope scope) => throw Unused();

        public string ResolveTransactionDirectory(string targetRoot, SquadDeploymentScope scope) => throw Unused();

        public string ResolveTransactionWorkDirectory(string targetRoot, SquadDeploymentScope scope) => throw Unused();

        public string ResolveFileWorkDirectory(string targetRoot, SquadDeploymentScope scope) => throw Unused();

        public string ResolveStateAuthorityRoot(string targetRoot, SquadDeploymentScope scope) => throw Unused();

        public string ResolveLeaseIdentity(string targetRoot, SquadDeploymentScope scope) => throw Unused();

        public string ResolveHomeRoot() => throw Unused();

        public string ResolveHomeCreationBoundary() => throw Unused();
    }

    private sealed class RecordingMigration : ISquadStateMigration
    {
        public string? InvokingCwd { get; private set; }

        public SquadMigrationReport MigrateGlobal(string? invokingCwd, bool dryRun = false)
        {
            InvokingCwd = invokingCwd;
            return new SquadMigrationReport([], [], [], []);
        }

        public SquadMigrationReport ImportProject(string root, bool dryRun = false) => new([], [], [], []);
    }

    private sealed class FixedGlobalRoots : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) => "/doctor-home";
    }

    private sealed class EmptyPresetStore : ISquadModelPresetStore
    {
        public IReadOnlyList<string> List() => [];

        public SquadModelPreset? TryLoad(string name) => null;
    }

    private sealed class UnusedPlanner : ISquadDeploymentPlanner
    {
        public SquadDeploymentPlan CreateInstall(
            string targetRoot,
            SquadDeploymentScope scope,
            SquadLock squadLock,
            IReadOnlyList<SquadDeploymentFile> renderedFiles,
            IReadOnlyList<SquadDegradation> degradations,
            bool adopt,
            TimeProvider timeProvider,
            ISquadGlobalRootResolver? globalRoots = null,
            IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
            IReadOnlyList<SquadRenderedBlock>? blocks = null) => throw Unused();

        public SquadDeploymentPlan CreateUpdate(
            string targetRoot,
            SquadDeploymentScope scope,
            SquadLock squadLock,
            IReadOnlyList<SquadDeploymentFile> renderedFiles,
            SquadReceipt previousReceipt,
            IReadOnlyList<SquadDegradation> degradations,
            bool replaceManaged,
            TimeProvider timeProvider,
            ISquadGlobalRootResolver? globalRoots = null,
            IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
            IReadOnlyList<SquadRenderedBlock>? blocks = null) => throw Unused();

        public SquadDeploymentPlan CreateUninstall(
            string targetRoot,
            SquadDeploymentScope scope,
            SquadReceipt receipt,
            ISquadGlobalRootResolver? globalRoots = null,
            IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
            SquadTarget? onlyTarget = null) => throw Unused();

        public IReadOnlyList<SquadUnmanagedPathCollision> CollectUnmanagedCollisions(
            string targetRoot,
            SquadDeploymentScope scope,
            IReadOnlyList<SquadDeploymentFile> renderedFiles,
            ISquadGlobalRootResolver? globalRoots,
            SquadReceipt? receipt = null,
            IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null) => [];
    }

    private sealed class SilentGuidance : ISquadResponseGuidanceStore
    {
        public string ReadEffective(string homeDirectory, ICollection<string> warnings) => string.Empty;

        public ISquadResponseGuidanceDeployer CreateDeployer(string homeStateRoot) => throw Unused();

        public bool IsDegradableFailure(Exception exception) => false;
    }

    private sealed class NoSourceLocator : ISquadSourceLocator
    {
        public string? Resolve(string workingDirectory) => null;
    }

    private sealed class UnusedSourceLoader : ISquadSourceLoader
    {
        public SquadSource Load(string root) => throw Unused();
    }

    private sealed class NoShadowing : ISquadSkillShadowing
    {
        public IReadOnlyList<SquadSkillShadow> Find(
            string projectRoot,
            IReadOnlyList<SquadInstalledSkill> installed,
            IReadOnlySet<string> managedPaths) => [];
    }

    private sealed class NoDevinOverlap : IDevinImportOverlapInspector
    {
        public DevinImportOverlapReport Inspect(
            string workingDirectory,
            string? devinUserRoot,
            Func<string, string?> readFileText) => new([]);
    }

    private sealed class NoZCodeMcp : IZCodeMcpInspector
    {
        public ZCodeMcpConfigurationReport Inspect(
            IReadOnlyCollection<string> requiredServers,
            string zcodeStorageRoot,
            string workingDirectory,
            Func<string, string?> readFileText) => new([], [], []);
    }

    private static NotSupportedException Unused() => new("this collaborator is not reached by the test's workspace");
}
