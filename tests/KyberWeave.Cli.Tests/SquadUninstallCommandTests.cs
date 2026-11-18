using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What <c>squad uninstall</c> resolves, asks and prints for a project, over the shared console, path and state fakes.</summary>
public sealed class SquadUninstallCommandTests
{
    private const string WorkingDirectory = "/kw/work";
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new(isInputRedirected: true);
    private readonly FakeSquadStateStore _state = new(new FakeSquadUserPaths("/kw/appdata", "/kw/home"));
    private readonly FakeSquadStateMigration _migration = new();

    [Fact]
    public void Execute_GlobalAndProjectTogether_IsRefusedWithoutTouchingAnything()
    {
        FakeSquadLifecycleService lifecycle = new(_state);

        int exit = Command(lifecycle).Execute(null!, new SquadUninstallSettings { Global = true, Project = true });

        Assert.Equal(2, exit);
        Assert.Equal(
            "kyber-weave squad: error: --global and --project are mutually exclusive. Omit both to use the default scope.\n",
            Output());
        Assert.Empty(lifecycle.Uninstalls);
        Assert.Empty(_migration.InvokingDirectories);
    }

    [Fact]
    public void Execute_RelativePath_ResolvesAgainstTheWorkingDirectoryTheEnvironmentNames()
    {
        FakeSquadLifecycleService lifecycle = new(_state) { Result = new SquadLifecycleResult(true) };

        int exit = Command(lifecycle).Execute(null!, new SquadUninstallSettings { PathOption = "app" });

        Assert.Equal(0, exit);
        Assert.Equal([WorkingDirectory], _migration.InvokingDirectories);
        SquadUninstallRequest request = Assert.Single(lifecycle.Uninstalls);
        Assert.Equal("/kw/work/app", request.TargetRoot);
        Assert.Equal(SquadDeploymentScope.Project, request.Scope);
        Assert.Equal(
            "kyber-weave squad uninstall: target root /kw/work/app (project scope).\n" +
            "No Kyber-Squad deployment found at /kw/work/app. Nothing to uninstall.\n",
            Output());
    }

    [Fact]
    public void Execute_InteractiveAndDeclined_StopsBeforeTheLifecycle()
    {
        FakeSquadLifecycleService lifecycle = new(_state);
        List<string> questions = [];

        int exit = Command(lifecycle, interactive: true, readAnswer: question =>
        {
            questions.Add(question);
            return false;
        }).Execute(null!, new SquadUninstallSettings());

        Assert.Equal(2, exit);
        Assert.Empty(lifecycle.Uninstalls);
        Assert.Equal(["Proceed with 'uninstall' at '/kw/work' (project scope)? (y/n)"], questions);
        Assert.EndsWith("Declined. No changes were made.\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_Interactive_AsksOnTheConsoleWhenNoAnswerSeamIsGiven()
    {
        FakeSquadLifecycleService lifecycle = new(_state);
        _streams.IsInputRedirected = false;
        _streams.ScriptInput("n\n");

        int exit = Command(lifecycle, interactive: true).Execute(null!, new SquadUninstallSettings());

        Assert.Equal(2, exit);
        Assert.Empty(lifecycle.Uninstalls);
        Assert.Contains("Proceed with 'uninstall' at '/kw/work' (project scope)? (y/n) ", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_AnInterruptedTransactionWasRecovered_PrintsTheRecoveryNotice()
    {
        FakeSquadLifecycleService lifecycle = new(_state)
        {
            Result = new SquadLifecycleResult(true, Warnings: ["Recovered an unfinished Squad transaction at '/kw/work/.kyber-weave/.squad-transaction': it had not committed, so its changes were rolled back."])
        };

        int exit = Command(lifecycle).Execute(null!, new SquadUninstallSettings());

        Assert.Equal(0, exit);
        Assert.Contains(
            "warning: Recovered an unfinished Squad transaction at '/kw/work/.kyber-weave/.squad-transaction': " +
            "it had not committed, so its changes were rolled back.\n",
            Output(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_YesFlag_SkipsTheQuestionAndRemoves()
    {
        FakeSquadLifecycleService lifecycle = new(_state);

        int exit = Command(lifecycle, interactive: true, readAnswer: _ => throw new InvalidOperationException("asked"))
            .Execute(null!, new SquadUninstallSettings { Yes = true });

        Assert.Equal(0, exit);
        Assert.Single(lifecycle.Uninstalls);
    }

    [Fact]
    public void Execute_LifecycleFails_PrintsEachErrorAndExitsOne()
    {
        FakeSquadLifecycleService lifecycle = new(_state) { Result = new SquadLifecycleResult(false, Errors: ["first [x]", "second"]) };

        int exit = Command(lifecycle).Execute(null!, new SquadUninstallSettings());

        Assert.Equal(1, exit);
        Assert.EndsWith(
            "kyber-weave squad: error: first [x]\nkyber-weave squad: error: second\n",
            Output(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_MigrationReportsLegacyItemsOnADryRun_PrintsThemAndStops()
    {
        FakeSquadLifecycleService lifecycle = new(_state);
        _migration.Report = new SquadMigrationReport(["moved the receipt"], [], [], []);

        int exit = Command(lifecycle).Execute(null!, new SquadUninstallSettings { DryRun = true });

        Assert.Equal(0, exit);
        Assert.Equal("info moved the receipt\n", Output());
        Assert.Empty(lifecycle.Uninstalls);
    }

    private SquadUninstallCommand Command(
        FakeSquadLifecycleService lifecycle,
        bool interactive = false,
        Func<string, bool>? readAnswer = null)
    {
        SquadConsole console = new(_ansi, _streams);
        return new SquadUninstallCommand(
            lifecycle,
            _state,
            _migration,
            new FakeSquadGlobalRootResolver("/kw/home"),
            new SquadConsoleOptions(interactive, readAnswer),
            console,
            new SquadTargetRootConfirmation(console),
            new FakeProcessEnvironment(currentDirectory: WorkingDirectory),
            SquadTestCommands.InMemoryRoots(console, WorkingDirectory));
    }

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
}
