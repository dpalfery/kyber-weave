using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     Refuses a warning suppression the change adds unless it states a reason, and leaves the suppressions that
///     were already in the file alone.
/// </summary>
/// <remarks>
/// <para>
///     This repository asks for "a reason you can state" rather than banning suppressions, and 21 files already
///     carry one. So the gate judges added lines only: a change that leaves a committed suppression untouched
///     passes, and a change that adds one without saying why fails with the file and line named.
/// </para>
/// <para>
///     Exit codes follow the review branch: <c>0</c> when every added suppression states a reason or the change
///     adds none, <c>1</c> when at least one does not or the change could not be read, and <c>2</c> for an option
///     value that is refused before anything runs.
/// </para>
/// </remarks>
public sealed class ReviewSuppressionsCommand(IReviewSuppressionsService service, IConsole standard, IAnsiConsole ansi) : AsyncCommand<ReviewSuppressionsSettings>
{
    /// <summary>A suppression the change added without stating a reason.</summary>
    public const string UnreasonedSuppression = ReviewSuppressionOutcome.NoReason;

    /// <summary>The change could not be read, so no line was scanned.</summary>
    public const string ChangeUnreadable = ReviewSuppressionOutcome.ChangeUnreadable;

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(
        CommandContext context, ReviewSuppressionsSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ReviewCommandResult result = await service.RunAsync(
            new ReviewSuppressionsRequest(settings.Path, settings.BaseRef),
            cancellationToken).ConfigureAwait(false);

        if (result.ClientError is not null)
        {
            WriteClientInputError(result.ClientError);
            return result.ExitCode;
        }

        if (result.Report is not null)
            CommandHelpers.Finish(ansi, result.Report, settings, "review suppressions", "Suppression");

        return result.ExitCode;
    }

    /// <summary>
    /// Client-input errors go through the standard output rather than Spectre markup, so a ref or a path
    /// containing brackets is printed as written instead of being read as markup.
    /// </summary>
    private void WriteClientInputError(string message)
    {
        standard.Out.WriteLine($"kyber-weave review suppressions: error: {message}");
    }
}
