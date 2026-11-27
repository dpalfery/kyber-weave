using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What <c>squad instance remove</c> asks, prints and removes, over fake paths, console and instance service.</summary>
public sealed class SquadInstanceRemoveCommandTests : IDisposable
{
    private const string Home = "/kw/home";
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new(isInputRedirected: true);
    private readonly RecordingInstances _instances = new();
    private readonly StubFiles _files = new();

    public void Dispose() => _ansi.Dispose();

    [Fact]
    public void Execute_InstanceFound_RemovesItWithoutAskingWhenNotInteractive()
    {
        _files.Directories.Add(Home + "/.kyber-weave/squad/instances/claude/fast");

        int exit = Run(new SquadInstanceRemoveSettings { Target = "claude", Preset = "fast" });

        Assert.Equal(0, exit);
        Assert.Equal([(SquadTarget.Claude, "fast")], _instances.Removed);
        Assert.Equal("Removed instance 'claude/fast'.\n", Output());
    }

    [Fact]
    public void Execute_InstanceMissing_ReportsNotFound()
    {
        int exit = Run(new SquadInstanceRemoveSettings { Target = "claude", Preset = "fast" });

        Assert.Equal(2, exit);
        Assert.Empty(_instances.Removed);
        Assert.Equal("kyber-weave squad: error: Instance 'claude/fast' not found.\n", Output());
    }

    [Fact]
    public void Execute_Interactive_DecliningLeavesTheInstance()
    {
        _files.Directories.Add(Home + "/.kyber-weave/squad/instances/claude/fast");
        _streams.ScriptInput("n\n");

        int exit = Run(new SquadInstanceRemoveSettings { Target = "claude", Preset = "fast" }, interactive: true);

        Assert.Equal(2, exit);
        Assert.Empty(_instances.Removed);
        Assert.Equal("Remove instance 'claude/fast'? Declined by user.\n", Output());
    }

    [Fact]
    public void Execute_RelativeHome_IsResolvedAgainstTheWorkingDirectory()
    {
        _files.Directories.Add("rel/.kyber-weave/squad/instances/claude/fast");
        SquadInstanceRemoveCommand command = Command("rel", interactive: false);

        int exit = command.Execute(null!, new SquadInstanceRemoveSettings { Target = "claude", Preset = "fast" });

        Assert.Equal(0, exit);
    }

    private int Run(SquadInstanceRemoveSettings settings, bool interactive = false) =>
        Command(Home, interactive).Execute(null!, settings);

    private SquadInstanceRemoveCommand Command(string home, bool interactive) =>
        new(
            _instances,
            new FakeSquadUserPaths("/kw/appdata", home),
            _files,
            new SquadConsoleOptions(interactive, null),
            new SquadConsole(_ansi, _streams),
            FakePathResolver.OverEmptyFileSystem(),
            new FakeProcessEnvironment());

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class RecordingInstances : ISquadInstanceService
    {
        public List<(SquadTarget Target, string Preset)> Removed { get; } = [];

        public Task<SquadInstanceEnsureResult> EnsureAsync(SquadTarget target, string presetName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(SquadTarget target, string presetName, CancellationToken cancellationToken = default)
        {
            Removed.Add((target, presetName));
            return Task.CompletedTask;
        }

        public Task RefreshAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubFiles : ISquadFileAccess
    {
        public HashSet<string> Directories { get; } = [];

        public bool DirectoryExists(string path) => Directories.Contains(path);

        public bool FileExists(string path) => throw new NotSupportedException();

        public string ReadAllText(string path) => throw new NotSupportedException();

        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();

        public byte[] ReadBytesUpTo(string path, int maxBytes) => throw new NotSupportedException();

        public long GetLength(string path) => throw new NotSupportedException();

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public void DeleteDirectory(string path) => throw new NotSupportedException();

        public string NewScratchDirectoryPath(string prefix) => throw new NotSupportedException();

        public IEnumerable<SquadDirectoryEntry> EnumerateRealDirectories(string directory) => throw new NotSupportedException();

        public string? FindCheckoutSquadSourceRoot() => throw new NotSupportedException();

        public void WriteAllBytesAtomic(string path, byte[] contents) => throw new NotSupportedException();
    }
}
