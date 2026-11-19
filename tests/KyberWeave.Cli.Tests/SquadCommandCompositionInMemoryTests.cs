using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The Squad verbs' shared composition helpers over the in-memory file system and the shared fakes: where a
/// target root resolves, which scope a flag selects, and that each of the store, probe and transaction
/// factories builds the object it names. No test here reads the process environment or the disk.
/// </summary>
public sealed class SquadCommandCompositionInMemoryTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _standardStreams = new();
    private readonly string _workingDirectory;

    public SquadCommandCompositionInMemoryTests()
    {
        _workingDirectory = Path.Combine(_files.Root, "work");
        _files.CreateDirectory(_workingDirectory);
    }

    [Fact]
    public void SquadCommandComposition_ResolveStateStore_ReturnsConfiguredOrSharedPaths()
    {
        string appDataDir = Path.Combine(_files.Root, "custom-appdata");
        _files.CreateDirectory(appDataDir);
        string existingDir = Path.Combine(_workingDirectory, "existing");
        _files.CreateDirectory(existingDir);

        SquadStateStore defaultStore = SquadHostDefaults.ResolveStateStore(
            environmentReader: name => name switch
            {
                "HOME" => Path.Combine(_files.Root, "home"),
                "APPDATA" => appDataDir,
                _ => null
            });
        Assert.NotNull(defaultStore);

        FakeSquadUserPaths fakePaths = new(appDataDir);
        SquadStateStore customStore = SquadHostDefaults.ResolveStateStore(fakePaths);
        Assert.NotNull(customStore);
        Assert.StartsWith(
            appDataDir,
            customStore.ResolveReceiptPath(existingDir, SquadDeploymentScope.Global),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SquadCommandComposition_ResolveProbe_ResolvesWithDefaultAndCustomExecutor()
    {
        McpProcessProbe defaultMcp = SquadCommandComposition.ResolveProbe(null, new FakeProcessEnvironment());
        Assert.NotNull(defaultMcp);

        FakeProcessExecutor executor = new();
        McpProcessProbe customMcp = SquadCommandComposition.ResolveProbe(executor, new FakeProcessEnvironment());
        Assert.NotNull(customMcp);
    }

    [Fact]
    public void SquadCommandComposition_ResolveTransaction_ReturnsTransactionWithResolvedState()
    {
        FakeSquadUserPaths fakePaths = new(
            Path.Combine(_files.Root, "custom-appdata"),
            Path.Combine(_files.Root, "custom-home"));

        SquadTransaction txDefault = SquadHostDefaults.ResolveTransaction(
            SquadHostDefaults.ResolveStateStore(fakePaths));
        Assert.NotNull(txDefault);

        SquadStateStore store = SquadHostDefaults.ResolveStateStore(fakePaths);
        SquadTransaction txWithStore = SquadHostDefaults.ResolveTransaction(store);
        Assert.NotNull(txWithStore);

        SquadTransaction txWithPaths = SquadHostDefaults.ResolveTransaction(null, fakePaths);
        Assert.NotNull(txWithPaths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SquadCommandComposition_ResolveTargetRoot_NullOrWhitespace_ReturnsCurrentDirectoryFullPath(string? input)
    {
        string resolved = Roots().ResolveTargetRoot(input);
        Assert.Equal(_workingDirectory, resolved);
    }

    [Fact]
    public void SquadCommandComposition_ResolveTargetRoot_SpecifiedPath_ReturnsFullPath()
    {
        string relative = "some/relative/dir";
        string resolved = Roots().ResolveTargetRoot(relative);
        Assert.Equal(Path.GetFullPath(relative, _workingDirectory), resolved);
    }

    [Theory]
    [InlineData(true, SquadDeploymentScope.Global)]
    [InlineData(false, SquadDeploymentScope.Project)]
    public void SquadCommandComposition_ResolveScope_MapsCorrectly(bool isGlobal, SquadDeploymentScope expected)
    {
        Assert.Equal(expected, SquadCommandComposition.ResolveScope(isGlobal));
    }

    private SquadCommandRoots Roots() =>
        new(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: _workingDirectory), new SquadConsole(_ansi, _standardStreams));
}
