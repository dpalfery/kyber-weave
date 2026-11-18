using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
/// Finds symbols whose bodies are duplicated across the tree, from the CodeGraph index.
/// </summary>
/// <remarks>The use case is <see cref="IReviewDuplicatesService"/>; this command parses its options and renders what it returns.</remarks>
public sealed class ReviewDuplicatesCommand(IReviewDuplicatesService service, IAnsiConsole console) : Command<ReviewDuplicatesSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewDuplicatesSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ReviewCommandResult result = service.Run(
            new ReviewDuplicatesRequest(settings.Path, settings.Config, settings.Out),
            cancellationToken);

        if (result.Report is not null)
            CommandHelpers.Finish(console, result.Report, settings, "review duplicates", "Cluster");

        return result.ExitCode;
    }
}
