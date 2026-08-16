using KyberWeave.Cli.Update;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Update;

public sealed class UpdateCommand : Command<UpdateSettings>
{
    internal SelfUpdateHost? Host { get; init; }

    internal HttpMessageHandler? Handler { get; init; }

    internal Func<string, string?>? ReadEnvironment { get; init; }

    public override int Execute(CommandContext context, UpdateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            SelfUpdateHost host = Host ?? SelfUpdateHost.FromCurrentProcess();
            using SelfUpdater updater = Handler is null
                ? SelfUpdater.Create(host, Log)
                : new SelfUpdater(Handler, host, Log, ReadEnvironment);
            SelfUpdateOutcome outcome = updater.Run(new SelfUpdateOptions(
                settings.Version,
                settings.ReleaseCandidate,
                settings.NoMcp));
            WriteOutcome(outcome);
            return outcome.ExitCode;
        }
        catch (SelfUpdateException ex)
        {
            WriteOutcome(new SelfUpdateOutcome(1, ex.Message));
            return 1;
        }
    }

    private static void Log(string message) =>
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");

    private static void WriteOutcome(SelfUpdateOutcome outcome)
    {
        if (outcome.ExitCode == 0)
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(outcome.Message)}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[red]kyber-weave: error: {Markup.Escape(outcome.Message)}[/]");
    }
}
