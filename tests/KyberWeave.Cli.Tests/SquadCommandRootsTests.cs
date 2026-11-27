using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>Where a Squad verb anchors, what it migrates and which projects it names, over the in-memory file system and the shared fakes.</summary>
public sealed class SquadCommandRootsTests : IDisposable
{
    private const string WorkingDirectory = "/kw/work";
    private const string Home = "/kw/home";
    private readonly InMemoryFileSystem _files = new();
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeSquadStateStore _state = new(new FakeSquadUserPaths("/kw/appdata", Home));
    private readonly FakeSquadStateMigration _migration = new();

    public void Dispose() => _ansi.Dispose();

    [Theory]
    [InlineData(null, "/kw/work")]
    [InlineData("  ", "/kw/work")]
    [InlineData("app", "/kw/work/app")]
    [InlineData("/elsewhere", "/elsewhere")]
    public void ResolveTargetRoot_ResolvesAgainstTheWorkingDirectoryTheEnvironmentNames(string? path, string expected)
    {
        Assert.Equal(expected, Roots().ResolveTargetRoot(path));
    }

    [Fact]
    public void ResolveGlobalAnchorRoot_NoLegacyReceipt_IsTheHomeAndCreatesItWhenMissing()
    {
        string anchor = Roots().ResolveGlobalAnchorRoot(_state, null, dryRun: false);

        Assert.Equal(Home, anchor);
        Assert.True(_files.DirectoryExists(Home));
        Assert.Equal(string.Empty, Output());
    }

    [Fact]
    public void ResolveGlobalAnchorRoot_DryRun_LeavesTheHomeAlone()
    {
        string anchor = Roots().ResolveGlobalAnchorRoot(_state, null, dryRun: true);

        Assert.Equal(Home, anchor);
        Assert.False(_files.DirectoryExists(Home));
    }

    [Fact]
    public void ResolveGlobalAnchorRoot_SuppliedPath_IsNamedAndIgnored()
    {
        string anchor = Roots().ResolveGlobalAnchorRoot(_state, "/some/where", dryRun: true);

        Assert.Equal(Home, anchor);
        Assert.Equal(
            "warning: the path '/some/where' is ignored under --global; global state lives beneath the home directory '/kw/home'.\n",
            Output());
    }

    [Fact]
    public void ResolveGlobalAnchorRoot_DefaultPath_SaysNothing()
    {
        Roots().ResolveGlobalAnchorRoot(_state, ".", dryRun: true);

        Assert.Equal(string.Empty, Output());
    }

    [Fact]
    public void ResolveGlobalAnchorRoot_LegacyReceiptWithAbsoluteRoot_AnchorsAtItsRecordedRoot()
    {
        _state.Add(LegacyReceipt("/legacy/root"), readAt: Home);

        string anchor = Roots().ResolveGlobalAnchorRoot(_state, "/elsewhere", dryRun: false);

        Assert.Equal("/legacy/root", anchor);
        Assert.False(_files.DirectoryExists(Home));
        Assert.Equal(
            "warning: the path '/elsewhere' is ignored under --global; this legacy single-root deployment is anchored at its recorded root '/legacy/root'.\n",
            Output());
    }

    [Fact]
    public void ResolveGlobalAnchorRoot_LegacyReceiptWithRelativeRoot_FallsBackToTheSuppliedPath()
    {
        _state.Add(LegacyReceipt("relative/root"), readAt: Home);

        string anchor = Roots().ResolveGlobalAnchorRoot(_state, "app", dryRun: false);

        Assert.Equal("/kw/work/app", anchor);
    }

    [Fact]
    public void ResolveGlobalAnchorRoot_ModernReceipt_IsNotTreatedAsLegacy()
    {
        _state.Add(new SquadReceipt(
            "kyber-squad.receipt/v2",
            SquadDeploymentScope.Global,
            "/legacy/root",
            DateTimeOffset.UnixEpoch,
            [],
            [new SquadOwnedFile("agents/x.md", new string('0', 64), "claude", false)]),
            readAt: Home);

        Assert.Equal(Home, Roots().ResolveGlobalAnchorRoot(_state, null, dryRun: true));
    }

    [Fact]
    public void EnsureStateMigrated_ProjectRootThatExists_ImportsItsState()
    {
        _files.CreateDirectory("/kw/work");
        _migration.Report = new SquadMigrationReport(["moved a"], [], ["/kw/work"], ["careful"]);

        SquadMigrationReport report = Roots().EnsureStateMigrated(_migration, WorkingDirectory, "/kw/work", dryRun: false);

        Assert.Equal([WorkingDirectory], _migration.InvokingDirectories);
        Assert.Equal(["moved a"], report.MigratedItems);
        Assert.Equal(["/kw/work"], report.ImportedProjects);
        Assert.Equal(["careful"], report.Warnings);
    }

    [Fact]
    public void EnsureStateMigrated_ProjectRootThatIsMissing_ImportsNothing()
    {
        _migration.Report = new SquadMigrationReport([], [], ["/kw/work"], []);

        SquadMigrationReport report = Roots().EnsureStateMigrated(_migration, WorkingDirectory, "/kw/work", dryRun: false);

        Assert.Empty(report.ImportedProjects);
    }

    [Fact]
    public void EnsureStateMigrated_NoProject_OnlyMigratesGlobalState()
    {
        _files.CreateDirectory("/kw/work");
        _migration.Report = new SquadMigrationReport([], [], ["/kw/work"], []);

        SquadMigrationReport report = Roots().EnsureStateMigrated(_migration, WorkingDirectory, null, dryRun: true);

        Assert.Empty(report.ImportedProjects);
    }

    [Fact]
    public void ResolveNamedProjects_RegisteredProject_IsNamedByItsRegisteredPath()
    {
        _state.AddProject(Project("/kw/work/app"));

        IReadOnlyList<string> named = Roots().ResolveNamedProjects(["app", "/kw/work/app"], _state);

        Assert.Equal(["/kw/work/app"], named);
    }

    [Fact]
    public void ResolveNamedProjects_UnregisteredPathWithInRepoState_IsImportable()
    {
        _files.CreateDirectory("/kw/work/lib/.kyber-weave");
        _files.WriteAllText("/kw/work/lib/.kyber-weave/squad.receipt.json", "{}");

        IReadOnlyList<string> named = Roots().ResolveNamedProjects(["lib"], _state);

        Assert.Equal(["/kw/work/lib"], named);
    }

    [Fact]
    public void ResolveNamedProjects_UnknownPath_NamesTheNearestRegisteredOne()
    {
        _state.AddProject(Project("/kw/work/app"));

        ArgumentException refusal = Assert.Throws<ArgumentException>(
            () => Roots().ResolveNamedProjects(["/kw/work/ap"], _state));

        Assert.Equal(
            "'/kw/work/ap' is not a registered Squad project, and it holds no in-repo Squad state to import. Did you mean '/kw/work/app'?",
            refusal.Message);
    }

    [Fact]
    public void ResolveNamedProjects_UnknownPathWithNoRegistry_SaysNothingIsRegistered()
    {
        ArgumentException refusal = Assert.Throws<ArgumentException>(
            () => Roots().ResolveNamedProjects(["/kw/work/ap"], _state));

        Assert.EndsWith(" No projects are registered.", refusal.Message, StringComparison.Ordinal);
    }

    private SquadCommandRoots Roots() =>
        new(
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(currentDirectory: WorkingDirectory, homeDirectory: Home),
            new SquadConsole(_ansi, new FakeConsole()));

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static SquadProjectRegistryEntry Project(string path) =>
        new(path, path, DateTimeOffset.UnixEpoch, ["claude"], false, null);

    private static SquadReceipt LegacyReceipt(string targetRoot) =>
        new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            targetRoot,
            DateTimeOffset.UnixEpoch,
            [],
            [new SquadOwnedFile(".claude/agents/x.md", new string('0', 64), "claude", false)]);
}
