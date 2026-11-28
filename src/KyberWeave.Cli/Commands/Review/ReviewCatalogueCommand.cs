using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
/// Prints the catalogue <c>review:</c> block to stdout and writes no file, so gates shipped
/// in a newer release can be copied into a host configuration by hand.
/// </summary>
/// <remarks>The use case is <see cref="IReviewCatalogueService"/>; this command parses its options and prints what it returns.</remarks>
public sealed class ReviewCatalogueCommand(IReviewCatalogueService service, IConsole standard, IAnsiConsole ansi) : AsyncCommand<ReviewCatalogueSettings>
{
    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(
        CommandContext context, ReviewCatalogueSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ReviewCommandResult result = await service.RunAsync(
            new ReviewCatalogueRequest(settings.Path, settings.Config, settings.Technology),
            standard.Out,
            cancellationToken).ConfigureAwait(false);

        if (result.ClientError is not null)
        {
            WriteClientInputError(result.ClientError);
            return result.ExitCode;
        }

        if (result.Report is not null)
            CommandHelpers.Finish(ansi, result.Report, settings, "review catalogue", "Gate");

        return result.ExitCode;
    }

    /// <summary>
    /// Client-input errors go through the standard output rather than Spectre markup, so a
    /// long line (like the unknown-technology hint) is never folded across console width and
    /// the hint stays intact for hosts that match on it.
    /// </summary>
    private void WriteClientInputError(string message)
    {
        standard.Out.WriteLine($"kyber-weave review catalogue: error: {message}");
    }
}
