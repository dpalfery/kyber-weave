using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What <c>squad update</c> resolves, asks and prints for a project, over the shared console, path, config and state fakes.</summary>
public sealed class SquadUpdateCommandTests
{
    private const string WorkingDirectory = "/kw/work";
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new(isInputRedirected: true);
    private readonly FakeSquadStateStore _state = new(new FakeSquadUserPaths("/kw/appdata", "/kw/home"));
    private readonly FakeSquadStateMigration _migration = new();
    private readonly FakeSquadTargetResolver _targets = new();
    private readonly FakeKyberWeaveConfigRepository _configs = new();

    [Fact]
    public void Execute_NoDeployment_SaysWhereAndHowToCreateOne()
    {
        FakeSquadLifecycleService lifecycle = new(_state);

        int exit = Command(lifecycle).Execute(null!, new SquadUpdateSettings());

        Assert.Equal(1, exit);
        Assert.Equal([WorkingDirectory], _migration.InvokingDirectories);
        Assert.Equal(
            "No Kyber-Squad deployment found at /kw/work.\nRun kyber-weave squad install to create an initial deployment.\n",
            Output());
        Assert.Empty(lifecycle.Updates);
    }

    [Fact]
    public void Execute_RelativePath_ResolvesAgainstTheWorkingDirectoryTheEnvironmentNames()
    {
        FakeSquadLifecycleService lifecycle = new(_state);
        _state.Add(Receipt("/kw/work/app"));

        int exit = Command(lifecycle).Execute(null!, new SquadUpdateSettings { PathOption = "app" });

        Assert.Equal(0, exit);
        SquadUpdateRequest request = Assert.Single(lifecycle.Updates);
        Assert.Equal("/kw/work/app", request.TargetRoot);
        Assert.Contains("Successfully updated", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_InteractiveAndDeclined_StopsBeforeTheLifecycle()
    {
        FakeSquadLifecycleService lifecycle = new(_state);
        _state.Add(Receipt(WorkingDirectory));
        List<string> questions = [];

        int exit = Command(lifecycle, interactive: true, readAnswer: question =>
        {
            questions.Add(question);
            return false;
        }).Execute(null!, new SquadUpdateSettings());

        Assert.Equal(2, exit);
        Assert.Empty(lifecycle.Updates);
        Assert.Equal(["Proceed with 'update' at '/kw/work' (project scope)? (y/n)"], questions);
        Assert.EndsWith("Declined. No changes were made.\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_NoTargetsResolved_NamesTheRecoveryCommandAndUsesItsExitCode()
    {
        FakeSquadLifecycleService lifecycle = new(_state);
        _state.Add(Receipt(WorkingDirectory));
        _targets.Decision = new SquadTargetResolutionDecision
        {
            Kind = SquadTargetResolutionKind.Failure,
            Targets = [],
            Source = SquadTargetResolutionSource.None,
            ExitCode = 3,
            RecoveryCommand = "--target claude"
        };

        int exit = Command(lifecycle).Execute(null!, new SquadUpdateSettings());

        Assert.Equal(3, exit);
        Assert.Equal(
            "No deployment targets specified or detected.\nSpecify target(s) using --target claude.\n",
            Output());
    }

    [Fact]
    public void Execute_LifecycleFails_PrintsEachErrorAndExitsOne()
    {
        FakeSquadLifecycleService lifecycle = new(_state) { Result = new SquadLifecycleResult(false, Errors: ["broken [x]"]) };
        _state.Add(Receipt(WorkingDirectory));

        int exit = Command(lifecycle).Execute(null!, new SquadUpdateSettings());

        Assert.Equal(1, exit);
        Assert.EndsWith("kyber-weave squad: error: broken [x]\n", Output(), StringComparison.Ordinal);
    }

    private static SquadReceipt Receipt(string root) =>
        new("1", SquadDeploymentScope.Project, root, DateTimeOffset.UnixEpoch, [], []);

    private SquadUpdateCommand Command(
        FakeSquadLifecycleService lifecycle,
        bool interactive = false,
        Func<string, bool>? readAnswer = null)
    {
        SquadConsole console = new(_ansi, _streams);
        return new SquadUpdateCommand(
            lifecycle,
            new NoInstances(),
            _state,
            _migration,
            _targets,
            new FakeSquadGlobalRootResolver("/kw/home"),
            new NoFiles(),
            new SquadConsoleOptions(interactive, readAnswer),
            console,
            new SquadTargetRootConfirmation(console),
            new FakeProcessEnvironment(currentDirectory: WorkingDirectory),
            FakePathResolver.OverEmptyFileSystem(),
            _configs);
    }

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class NoInstances : KyberWeave.Contracts.Services.ISquadInstanceService
    {
        public Task<SquadInstanceEnsureResult> EnsureAsync(SquadTarget target, string presetName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(SquadTarget target, string presetName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RefreshAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoFiles : ISquadFileAccess
    {
        public bool DirectoryExists(string path) => throw new NotSupportedException();

        public bool FileExists(string path) => throw new NotSupportedException();

        public string ReadAllText(string path) => throw new NotSupportedException();

        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public void DeleteDirectory(string path) => throw new NotSupportedException();

        public string NewScratchDirectoryPath(string prefix) => throw new NotSupportedException();

        public IEnumerable<SquadDirectoryEntry> EnumerateRealDirectories(string directory) => throw new NotSupportedException();

        public string? FindCheckoutSquadSourceRoot() => throw new NotSupportedException();
    }
}
