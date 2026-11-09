using KyberWeave.Cli.Update;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Update;

public sealed class UpdateCommand : Command<UpdateSettings>
{
    internal SelfUpdateHost? Host { get; init; }

    internal HttpMessageHandler? Handler { get; init; }

    internal Func<string, string?>? ReadEnvironment { get; init; }

    internal IArbiterHookRefresher? ArbiterHookRefresher { get; init; }

    internal Func<string, IReadOnlyList<string>, bool, int>? FinishingProcess { get; init; }

    public override int Execute(CommandContext context, UpdateSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            SelfUpdateHost host = Host ?? SelfUpdateHost.FromCurrentProcess();
            IArbiterHookRefresher refresher = ArbiterHookRefresher ?? new ArbiterHookRefresher(Log, ReadEnvironment);
            using SelfUpdater updater = Handler is null
                ? SelfUpdater.Create(host, Log, refresher, FinishingProcess)
                : new SelfUpdater(Handler, host, Log, ReadEnvironment, arbiterHookRefresher: refresher, finishingProcess: FinishingProcess);
            SelfUpdateOutcome outcome = updater.Run(new SelfUpdateOptions(
                settings.Version,
                settings.ReleaseCandidate,
                settings.NoMcp,
                settings.NoKyberDash,
                settings.NoMenubar,
                settings.NoArbiter,
                settings.NoRestart,
                settings.FinishUpdate));
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
