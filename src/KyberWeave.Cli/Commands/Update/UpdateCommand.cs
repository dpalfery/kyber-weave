using KyberWeave.Contracts.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using KyberWeave.Domain.Update;

namespace KyberWeave.Cli.Commands.Update;

/// <summary>Updates the installed kyber-weave binaries from a GitHub Release.</summary>
public sealed class UpdateCommand : Command<UpdateSettings>
{
    private readonly ISelfUpdateService _updater;
    private readonly IAnsiConsole _console;

    /// <summary>Creates the command.</summary>
    /// <param name="updater">The self-update service.</param>
    /// <param name="console">Receives one line per step and the outcome line.</param>
    public UpdateCommand(ISelfUpdateService updater, IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(console);
        _updater = updater;
        _console = console;
    }

    public override int Execute(CommandContext context, UpdateSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            SelfUpdateOutcome outcome = _updater.Run(
                new SelfUpdateOptions(
                    settings.Version,
                    settings.ReleaseCandidate,
                    settings.NoMcp,
                    settings.NoKyberDash,
                    settings.NoMenubar,
                    settings.NoArbiter,
                    settings.NoRestart,
                    settings.FinishUpdate),
                Log);
            WriteOutcome(outcome);
            return outcome.ExitCode;
        }
        catch (SelfUpdateException ex)
        {
            WriteOutcome(new SelfUpdateOutcome(1, ex.Message));
            return 1;
        }
    }

    public int Execute(CommandContext context, UpdateSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    private void Log(string message)
    {
        _console.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
    }

    private void WriteOutcome(SelfUpdateOutcome outcome)
    {
        if (outcome.ExitCode == 0)
        {
            // A finish-mode run has no outcome of its own: the process that started it prints the one line.
            if (!string.IsNullOrEmpty(outcome.Message))
                _console.MarkupLine($"[bold]{Markup.Escape(outcome.Message)}[/]");
            return;
        }

        _console.MarkupLine($"[red]kyber-weave: error: {Markup.Escape(outcome.Message)}[/]");
    }
}
