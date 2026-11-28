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
public sealed class SquadDoctorSeamTests : IDisposable
{
    private const string Workspace = "/doctor-work";

    /// <summary>The user-level root <see cref="FixedGlobalRoots"/> answers for every target.</summary>
    private const string GlobalRoot = "/doctor-home";

    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new();
    private readonly InMemoryFileSystem _files = new();
    private readonly RecordingMigration _migration = new();
    private readonly RecordingSourceLocator _locator = new();
    private readonly StubDevinOverlap _devinOverlap = new();

    public void Dispose()
    {
        _ansi.Dispose();
        _streams.Dispose();
    }

    /// <summary>A workspace with no Devin marker in the injected file system is reported as not checked.</summary>
    [Fact]
    public void Execute_NoDevinMarkerInTheInjectedFileSystem_SkipsTheDevinCheck()
    {
        int exit = Execute(new SquadDoctorSettings { Path = Workspace }, out string output);

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

        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out string output);

        Assert.Contains("Devin duplicate imports: none", output, StringComparison.Ordinal);
        Assert.DoesNotContain("not checked (no '.devin/'", output, StringComparison.Ordinal);
    }

    /// <summary>The Devin check reports what the inspector finds: the file reading lives behind that port.</summary>
    [Fact]
    public void Execute_DevinInspectorReportsAnOverlap_WarnsAboutIt()
    {
        _files.CreateDirectory($"{Workspace}/.devin");
        _devinOverlap.Report = new([new DevinImportOverlapEntry(".claude/skills", "skill", "claude", ["review"])]);

        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out string output);

        Assert.Contains("Devin also loads 1 skill(s) from '.claude/skills/'", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A retired skill folder that exists only in the injected file system is warned about, so the check reads the
    /// deployment tree through the seam.
    /// </summary>
    [Fact]
    public void Execute_RetiredSkillInTheInjectedFileSystem_WarnsAboutIt()
    {
        _files.CreateDirectory($"{Workspace}/.claude/skills/{SquadRetiredSkills.SecondBrain}");

        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out string output);

        Assert.Contains($"Retired skill '{SquadRetiredSkills.SecondBrain}' still present at", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A retired technology skill under a project skills root warns with the standards
    /// template its rules moved into, so the operator sees the replacement, not just a name.
    /// </summary>
    [Fact]
    public void Execute_RetiredTechnologySkillInAProjectRoot_WarnsWithItsStandardsTemplate()
    {
        _files.CreateDirectory($"{Workspace}/.claude/skills/csharp-dev");

        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out string output);

        Assert.Contains("Retired skill 'csharp-dev' still present at", output, StringComparison.Ordinal);
        Assert.Contains("`csharp` standards template", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A retired skill in a target's user-level skills directory warns too: a user-level
    /// deployment is Squad's default, so the project tree alone would miss it.
    /// </summary>
    [Fact]
    public void Execute_RetiredTechnologySkillUnderAGlobalRoot_WarnsWithItsStandardsTemplate()
    {
        _files.CreateDirectory("/doctor-home/skills/rust-dev");

        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out string output);

        Assert.Contains("Retired skill 'rust-dev' still present at", output, StringComparison.Ordinal);
        Assert.Contains("`rust` standards template", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A copy the global receipt still claims is reported by its own skill's name and new
    /// home, not under <c>second-brain</c>: that is the retained case <c>squad update</c>
    /// leaves behind when the operator edited the file.
    /// </summary>
    [Fact]
    public void Execute_GlobalReceiptClaimsARetiredTechnologySkill_WarnsWithItsNewHome()
    {
        _state.GlobalReceipt = Receipt(
            SquadDeploymentScope.Global,
            new SquadOwnedFile("skills/dal-dev/SKILL.md", new string('0', 64), "claude", Adopted: false));

        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out string output);

        Assert.Contains("Global deployment receipt still claims retired 'dal-dev' files", output, StringComparison.Ordinal);
        Assert.Contains("`data-access-layer` and `sql-server` standards templates", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lookalike directory is a different skill, so no warning names it. The real retired skill
    /// beside it still warns, which proves the lookalike check is not passing because nothing ran.
    /// </summary>
    [Fact]
    public void Execute_LookalikeRetiredSkillName_WarnsOnlyAboutTheRealSkill()
    {
        _files.CreateDirectory($"{Workspace}/.claude/skills/csharp-dev-extra");
        _files.CreateDirectory("/doctor-home/skills/csharp-dev-extra");
        _files.CreateDirectory($"{Workspace}/.claude/skills/csharp-dev");

        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out string output);

        Assert.DoesNotContain("'csharp-dev-extra'", output, StringComparison.Ordinal);
        Assert.Contains("Retired skill 'csharp-dev' still present at", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A receipt-owned hook block whose file exists only in the injected file system is read through the seam: the
    /// reported drift is what the fake file holds, not what the developer's disk does.
    /// </summary>
    [Fact]
    public void Execute_OwnedBlockFileInTheInjectedFileSystem_ReadsItThroughTheSeam()
    {
        _state.Receipt = ReceiptWithOneBlock();
        _files.CreateDirectory($"{Workspace}/.claude");
        _files.WriteAllText($"{Workspace}/.claude/settings.json", "{ not json");

        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out string output);

        Assert.Contains("Owned block drift: .claude/settings.json", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(missing)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(unreadable:", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The state migration imports a project root that exists only in the injected file system, so the answer comes
    /// from the seam rather than from the developer's disk.
    /// </summary>
    [Fact]
    public void Execute_ProjectRootOnlyInTheInjectedFileSystem_ImportsIt()
    {
        _files.CreateDirectory(Workspace);

        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out _);

        Assert.Equal(Workspace, _migration.ImportedProjectRoot);
    }

    /// <summary>
    /// A <c>--global</c> run with a named path warns through the injected console and leaves the injected file system
    /// alone, because the anchor is resolved as a dry run.
    /// </summary>
    [Fact]
    public void Execute_GlobalWithANamedPath_WritesTheIgnoredPathWarningToTheInjectedConsole()
    {
        _ = Execute(new SquadDoctorSettings { Path = Workspace, Global = true }, out string output);

        Assert.Contains("is ignored under --global", output, StringComparison.Ordinal);
    }


    /// <summary>
    /// With no path named, the diagnosed root is the environment seam's current directory, not the process's.
    /// </summary>
    [Fact]
    public void Execute_NoNamedPath_DiagnosesTheEnvironmentSeamsCurrentDirectory()
    {
        _ = Execute(new SquadDoctorSettings { Global = false }, out _);

        Assert.Equal("/launched-from", _locator.WorkingDirectory);
    }


    /// <summary>The state migration runs from the environment seam's current directory, not from the process's.</summary>
    [Fact]
    public void Execute_MigrationRunsFromTheEnvironmentSeamsCurrentDirectory()
    {
        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out _);

        Assert.Equal("/launched-from", _migration.InvokingCwd);
    }

    /// <summary>Nothing the doctor reports goes to the standard output the markup console does not own.</summary>
    [Fact]
    public void Execute_WritesNothingToStandardOutput()
    {
        _ = Execute(new SquadDoctorSettings { Path = Workspace }, out _);

        Assert.Equal(string.Empty, _streams.Output);
    }

    [Fact]
    public void Constructor_TakesTheMcpWiringServiceAsARequiredArgument()
    {
        System.Reflection.ParameterInfo wiring = typeof(SquadDoctorCommand).GetConstructors().Single()
            .GetParameters().Single(parameter => parameter.ParameterType == typeof(KyberWeave.Contracts.Services.IMcpWiringService));

        Assert.False(wiring.HasDefaultValue);
    }

    private int Execute(SquadDoctorSettings settings, out string output)
    {
        SquadDoctorCommand command = Doctor();
        int exit = command.Execute(null!, settings, CancellationToken.None);

        output = _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
        return exit;
    }

    private static SquadReceipt ReceiptWithOneBlock() =>
        new("kyber-squad.receipt/v3", SquadDeploymentScope.Project, Workspace, DateTimeOffset.UnixEpoch, [], [])
        {
            Blocks =
            [
                new SquadOwnedBlock(
                    ".claude/settings.json",
                    "claude",
                    CreatedFile: true,
                    [new SquadOwnedBlockEntry("hooks", new string('a', 64))])
            ]
        };

    private static SquadReceipt Receipt(SquadDeploymentScope scope, params SquadOwnedFile[] files) =>
        new(SquadStateSchemas.ReceiptSchemaV3, scope, GlobalRoot, DateTimeOffset.UnixEpoch, [], files);

    private SquadDoctorCommand Doctor()
    {
        FakeProcessExecutor executor = new();
        executor.WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeProcessEnvironment environment = new(currentDirectory: "/launched-from", readVariable: _ => null);
        SquadConsole console = new(_ansi, _streams);
        return new SquadDoctorCommand(
            _state,
            _migration,
            new FixedGlobalRoots(),
            new FakeSquadRenderer(),
            new EmptyPresetStore(),
            new UnusedPlanner(),
            new SilentGuidance(),
            environment,
            _locator,
            new UnusedSourceLoader(),
            new NoShadowing(),
            _devinOverlap,
            new NoZCodeMcp(),
            new SquadDoctorOptions(executor, WorkingDirectory: null),
            SquadFileSystemPathSemantics.Instance,
            new FakeKyberWeaveConfigRepository(),
            console,
            _files,
            new SquadCommandRoots(_files, new FakePathResolver(_files), environment, console),
            new FakeMcpWiringService());
    }

    private readonly EmptyStateStore _state = new();

    private sealed class EmptyStateStore : ISquadStateStore
    {
        public SquadReceipt? Receipt { get; set; }

        public SquadReceipt? GlobalReceipt { get; set; }

        public ISquadUserPaths UserPaths { get; } = new FakeSquadUserPaths("/doctor-state");

        public string ResolveStateDirectory(string targetRoot, SquadDeploymentScope scope) => throw Unused();

        public string ResolveHomeStateDirectory() => throw Unused();

        public string ResolveHomeOwnedFilesRoot() => throw Unused();

        public IReadOnlyList<SquadProjectRegistryEntry> ListProjects() => [];

        public SquadProjectRegistryEntry? FindProject(string root) => null;

        public bool IsRegistered(string root) => false;

        public IReadOnlyList<SquadProjectRegistryEntry> ListProjectsWithoutRebuild() => [];

        public void RegisterProject(string root, IReadOnlyList<string> targets, DateTimeOffset now) => throw Unused();

        public bool UnregisterProject(string root) => throw Unused();

        public string SerializeLock(SquadLock squadLock) => throw Unused();

        public SquadLock DeserializeLock(string yaml) => throw Unused();

        public string SerializeReceipt(SquadReceipt receipt) => throw Unused();

        public SquadReceipt DeserializeReceipt(string json) => throw Unused();

        public SquadReceipt? ReadReceipt(string targetRoot, SquadDeploymentScope scope) =>
            scope == SquadDeploymentScope.Global
                ? (string.Equals(targetRoot, GlobalRoot, StringComparison.Ordinal) ? GlobalReceipt : null)
                : Receipt;

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

        public string? ImportedProjectRoot { get; private set; }

        public SquadMigrationReport MigrateGlobal(string? invokingCwd, bool dryRun = false)
        {
            InvokingCwd = invokingCwd;
            return new SquadMigrationReport([], [], [], []);
        }

        public SquadMigrationReport ImportProject(string root, bool dryRun = false)
        {
            ImportedProjectRoot = root;
            return new SquadMigrationReport([], [], [], []);
        }
    }

    private sealed class FixedGlobalRoots : ISquadGlobalRootResolver
    {
        public string? HomeDirectory => GlobalRoot;

        public string ResolveGlobalRoot(SquadTarget target) => GlobalRoot;
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

    private sealed class RecordingSourceLocator : ISquadSourceLocator
    {
        public string? WorkingDirectory { get; private set; }

        public string? Resolve(string workingDirectory)
        {
            WorkingDirectory = workingDirectory;
            return null;
        }
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

    private sealed class StubDevinOverlap : IDevinImportOverlapInspector
    {
        public DevinImportOverlapReport Report { get; set; } = new([]);

        public DevinImportOverlapReport Inspect(string workingDirectory, string? devinUserRoot) => Report;
    }

    private sealed class NoZCodeMcp : IZCodeMcpInspector
    {
        public ZCodeMcpConfigurationReport Inspect(
            IReadOnlyCollection<string> requiredServers,
            string zcodeStorageRoot,
            string workingDirectory) => new([], [], []);
    }

    private static NotSupportedException Unused() => new("this collaborator is not reached by the test's workspace");
}
