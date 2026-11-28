using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What <c>squad install</c> resolves, asks and prints for a project, over the shared console, path, config and state fakes.</summary>
public sealed class SquadInstallCommandTests : IDisposable
{
    private const string WorkingDirectory = "/kw/work";
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new(isInputRedirected: true);
    private readonly FakeSquadStateStore _state = new(new FakeSquadUserPaths("/kw/appdata", "/kw/home"));
    private readonly FakeSquadStateMigration _migration = new();
    private readonly FakeSquadTargetResolver _targets = new();
    private readonly FakeKyberWeaveConfigRepository _configs = new();

    public void Dispose()
    {
        _ansi.Dispose();
        _streams.Dispose();
    }

    [Fact]
    public async Task Execute_UnknownTarget_IsRefusedAsClientInput()
    {
        FakeSquadLifecycleService lifecycle = new(_state);

        int exit = await Command(lifecycle).ExecuteAsync(null!, new SquadInstallSettings { Targets = ["not-a-harness"] },
            CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.StartsWith("kyber-weave squad: error: ", Output(), StringComparison.Ordinal);
        Assert.Empty(lifecycle.Installs);
        Assert.Empty(_migration.InvokingDirectories);
    }

    [Fact]
    public async Task Execute_NoTargetsResolved_NamesTheRecoveryCommandAndUsesItsExitCode()
    {
        FakeSquadLifecycleService lifecycle = new(_state);
        _targets.Decision = new SquadTargetResolutionDecision
        {
            Kind = SquadTargetResolutionKind.Failure,
            Targets = [],
            Source = SquadTargetResolutionSource.None,
            ExitCode = 3,
            RecoveryCommand = "--target claude"
        };

        int exit = await Command(lifecycle).ExecuteAsync(null!, new SquadInstallSettings(), CancellationToken.None);

        Assert.Equal(3, exit);
        Assert.Equal(
            "No deployment targets specified or detected.\nSpecify target(s) using --target claude.\n",
            Output());
        Assert.Empty(lifecycle.Installs);
    }

    [Fact]
    public async Task Execute_Project_InstallsAtTheWorkingDirectoryAndSaysSo()
    {
        FakeSquadLifecycleService lifecycle = new(_state);

        int exit = await Command(lifecycle).ExecuteAsync(null!, new SquadInstallSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal([WorkingDirectory], _migration.InvokingDirectories);
        SquadInstallRequest request = Assert.Single(lifecycle.Installs);
        Assert.Equal(WorkingDirectory, request.TargetRoot);
        Assert.Equal(SquadDeploymentScope.Project, request.Scope);
        Assert.Equal([SquadTarget.Claude], request.Targets);
        Assert.Equal(WorkingDirectory, Assert.Single(_targets.Requests).RootPath);
        Assert.Contains("Successfully installed Kyber-Squad to /kw/work.\n", Output(), StringComparison.Ordinal);
        Assert.Contains(SquadTaskGateHint.Message + "\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_AnInterruptedTransactionWasRecovered_PrintsTheRecoveryNotice()
    {
        FakeSquadLifecycleService lifecycle = new(_state)
        {
            Result = new SquadLifecycleResult(true, Warnings: ["Recovered an unfinished Squad transaction at '/kw/work/.kyber-weave/.squad-transaction': it had not committed, so its changes were rolled back."])
        };

        int exit = await Command(lifecycle).ExecuteAsync(null!, new SquadInstallSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(
            "warning: Recovered an unfinished Squad transaction at '/kw/work/.kyber-weave/.squad-transaction': " +
            "it had not committed, so its changes were rolled back.\n",
            Output(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_RelativePath_ResolvesAgainstTheWorkingDirectoryTheEnvironmentNames()
    {
        FakeSquadLifecycleService lifecycle = new(_state);

        int exit = await Command(lifecycle).ExecuteAsync(null!, new SquadInstallSettings { PathOption = "app" },
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("/kw/work/app", Assert.Single(lifecycle.Installs).TargetRoot);
    }

    [Fact]
    public async Task Execute_DryRun_PlansWithoutAskingAndReportsTheFileCount()
    {
        FakeSquadLifecycleService lifecycle = new(_state);

        int exit = await Command(lifecycle, interactive: true, readAnswer: _ => throw new InvalidOperationException("asked"))
            .ExecuteAsync(null!, new SquadInstallSettings { DryRun = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(Assert.Single(lifecycle.Installs).DryRun);
        Assert.Contains("Dry-run: planned 0 deployed files for /kw/work.\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_InteractiveAndDeclined_StopsBeforeTheLifecycle()
    {
        FakeSquadLifecycleService lifecycle = new(_state);

        int exit = await Command(lifecycle, interactive: true, readAnswer: _ => false)
            .ExecuteAsync(null!, new SquadInstallSettings(), CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Empty(lifecycle.Installs);
        Assert.EndsWith("Declined. No changes were made.\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_OpenCodeProject_PrintsTheRegistrationStepOnTheStandardStream()
    {
        FakeSquadLifecycleService lifecycle = new(_state);
        _configs.WriteRepositoryConfig(WorkingDirectory, "arbiter:\n  enabled: true\n");
        _targets.Decision = new SquadTargetResolutionDecision
        {
            Kind = SquadTargetResolutionKind.Resolved,
            Targets = [SquadTarget.OpenCode],
            Source = SquadTargetResolutionSource.Explicit
        };

        int exit = await Command(lifecycle).ExecuteAsync(null!, new SquadInstallSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(
            SquadInstallCommand.OpenCodeArbiterRegistrationStep(SquadDeploymentScope.Project) + Environment.NewLine,
            _streams.Output);
    }

    [Fact]
    public async Task Execute_LifecycleFails_PrintsEachErrorAndExitsOne()
    {
        FakeSquadLifecycleService lifecycle = new(_state) { Result = new SquadLifecycleResult(false, Errors: ["broken [x]"]) };

        int exit = await Command(lifecycle).ExecuteAsync(null!, new SquadInstallSettings(), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.EndsWith("kyber-weave squad: error: broken [x]\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteArbiterTrustSteps_NamesEachTargetsStepOnTheGivenConsole()
    {
        SquadConsole console = new(_ansi, _streams);

        SquadInstallCommand.WriteArbiterTrustSteps(
            console,
            [SquadTarget.Claude],
            new SquadArbiterWiring(true, 8));

        Assert.StartsWith("Arbiter trust (claude): ", Output(), StringComparison.Ordinal);
    }

    private SquadInstallCommand Command(
        FakeSquadLifecycleService lifecycle,
        bool interactive = false,
        Func<string, bool>? readAnswer = null)
    {
        SquadConsole console = new(_ansi, _streams);
        return new SquadInstallCommand(
            lifecycle,
            _state,
            _migration,
            _targets,
            new FakeSquadGlobalRootResolver("/kw/home"),
            new SquadConsoleOptions(interactive, readAnswer),
            console,
            new SquadTargetRootConfirmation(console),
            new FakeProcessEnvironment(currentDirectory: WorkingDirectory),
            SquadTestCommands.InMemoryRoots(console, WorkingDirectory),
            _configs);
    }

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
}
