using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Update;

/// <summary>Updates the installed kyber-weave binaries from a GitHub Release.</summary>
/// <param name="updater">The self-update service.</param>
public sealed class UpdateCommand(ISelfUpdateService updater) : Command<UpdateSettings>
{
    public override int Execute(CommandContext context, UpdateSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            SelfUpdateOutcome outcome = updater.Run(
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

    private static void Log(string message)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
    }

    private static void WriteOutcome(SelfUpdateOutcome outcome)
    {
        if (outcome.ExitCode == 0)
        {
            // A finish-mode run has no outcome of its own: the process that started it prints the one line.
            if (outcome.Message.Length > 0)
                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(outcome.Message)}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[red]kyber-weave: error: {Markup.Escape(outcome.Message)}[/]");
    }
}
