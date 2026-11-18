using KyberWeave.Cli.Rendering;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     Answers which review cycle a run is in, from the cycle folders the council already wrote: the cycle number,
///     whether it is a full review or a verifier cycle, the prior findings to re-check, and the gates to run again.
/// </summary>
/// <remarks>The use case is <see cref="IReviewCycleService"/>; this command parses its options and prints what it returns.</remarks>
public sealed class ReviewCycleCommand(IReviewCycleService service, IConsole standard, IAnsiConsole ansi) : Command<ReviewCycleSettings>
{
    /// <summary>The command description shared by the real registration and the help tests.</summary>
    public const string Description = "Report which review cycle a task is in, the prior findings to re-check and the gates to re-run.";

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewCycleSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ReviewCommandResult result = service.Run(
            new ReviewCycleRequest(
                settings.Path,
                settings.Config,
                settings.Task,
                settings.BaseRef,
                settings.ParsedFormat == OutputFormat.Json),
            standard.Out,
            cancellationToken);

        if (result.ClientError is not null)
        {
            WriteClientInputError(result.ClientError);
            return result.ExitCode;
        }

        if (result.Report is not null)
            CommandHelpers.Finish(ansi, result.Report, settings, "review cycle", "Review");

        foreach (string line in result.Epilogue ?? [])
            standard.Out.WriteLine(line);

        return result.ExitCode;
    }

    private void WriteClientInputError(string message)
    {
        standard.Out.WriteLine($"kyber-weave review cycle: error: {message}");
    }
}
