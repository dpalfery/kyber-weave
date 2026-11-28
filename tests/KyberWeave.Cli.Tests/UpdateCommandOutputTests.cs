using KyberWeave.Cli.Commands.Update;
using KyberWeave.Contracts.Services;
using Spectre.Console.Testing;
using Xunit;
using KyberWeave.Domain.Update;

namespace KyberWeave.Tests;

/// <summary><c>kyber-weave update</c> writes its step lines and its outcome to the console it is given.</summary>
public sealed class UpdateCommandOutputTests : IDisposable
{
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };

    public void Dispose()
    {
        _ansi.Dispose();
    }

    /// <summary>A successful update prints each step line and the outcome line on the injected console.</summary>
    [Fact]
    public async Task Execute_Success_WritesTheStepAndOutcomeLines()
    {
        int exit = await Command(0, "updated kyber-weave 0.2.0").ExecuteAsync(null!, new UpdateSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("downloading…\nupdated kyber-weave 0.2.0\n", Output());
    }

    /// <summary>A failed update names the process and its message in the error form.</summary>
    [Fact]
    public async Task Execute_Failure_WritesTheErrorLine()
    {
        int exit = await Command(1, "no write permission for /usr/local/bin").ExecuteAsync(null!, new UpdateSettings(), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(
            "downloading…\nkyber-weave: error: no write permission for /usr/local/bin\n",
            Output());
    }

    /// <summary>A refused host is reported as exit 1 with its own message, not as an unhandled exception.</summary>
    [Fact]
    public async Task Execute_ServiceRefuses_WritesItsReasonAsTheError()
    {
        int exit = await Command(new RefusingService("this looks like `dotnet run`"))
            .ExecuteAsync(null!, new UpdateSettings(), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal("kyber-weave: error: this looks like `dotnet run`\n", Output());
    }

    /// <summary>A finish-mode success prints nothing: the process that started it prints the one line.</summary>
    [Fact]
    public async Task Execute_FinishModeSuccess_WritesNothing()
    {
        int exit = await Command(0, string.Empty).ExecuteAsync(null!, new UpdateSettings { FinishUpdate = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("downloading…\n", Output());
    }

    /// <summary>A message that holds markup characters is printed as written, not parsed as markup.</summary>
    [Fact]
    public async Task Execute_MessageWithBrackets_WritesItLiterally()
    {
        int exit = await Command(1, "failed [bold]the swap[/]").ExecuteAsync(null!, new UpdateSettings(), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal("downloading…\nkyber-weave: error: failed [bold]the swap[/]\n", Output());
    }

    /// <summary>A missing updater is refused at construction, as every other command refuses its collaborators.</summary>
    [Fact]
    public void Constructor_NullUpdater_Throws()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new UpdateCommand(null!, _ansi));
    }

    /// <summary>A missing console is refused at construction.</summary>
    [Fact]
    public void Constructor_NullConsole_Throws()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new UpdateCommand(new ScriptedService(0, string.Empty), null!));
    }

    /// <summary>An outcome that carries no message prints no outcome line rather than failing on it.</summary>
    [Fact]
    public async Task Execute_SuccessWithNullMessage_WritesNoOutcomeLine()
    {
        int exit = await Command(0, null!).ExecuteAsync(null!, new UpdateSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("downloading…\n", Output());
    }

    private UpdateCommand Command(int exitCode, string message) => Command(new ScriptedService(exitCode, message));

    private UpdateCommand Command(ISelfUpdateService service) => new(service, _ansi);

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class ScriptedService(int exitCode, string message) : ISelfUpdateService
    {
        public Task<SelfUpdateOutcome> RunAsync(
            SelfUpdateOptions options,
            Action<string> log,
            CancellationToken cancellationToken = default)
        {
            log("downloading…");
            return Task.FromResult(new SelfUpdateOutcome(exitCode, message));
        }
    }

    private sealed class RefusingService(string reason) : ISelfUpdateService
    {
        public Task<SelfUpdateOutcome> RunAsync(
            SelfUpdateOptions options,
            Action<string> log,
            CancellationToken cancellationToken = default) =>
            throw new SelfUpdateException(reason);
    }
}
