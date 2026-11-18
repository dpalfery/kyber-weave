using KyberWeave.Cli.Rendering;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
/// Computes the review verdict from the council's seat output, or a findings document, and the gate report.
/// </summary>
public sealed class ReviewVerdictCommand(IReviewVerdictService verdicts, IConsole standard, IAnsiConsole ansi) : Command<ReviewVerdictSettings>
{
    /// <summary>The command description shared by the real registration and the help tests.</summary>
    public const string Description = "Compute approve / request-changes from findings and gates.";

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewVerdictSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ReviewVerdictResult result = verdicts.Decide(
            new ReviewVerdictRequest(
                settings.Path,
                settings.Config,
                settings.Seats,
                settings.Findings,
                settings.Gates,
                settings.Prior,
                settings.Out,
                settings.BaseRef,
                settings.Task,
                settings.Drop,
                settings.NotSeated,
                settings.Candidates,
                settings.NoDecisionModel,
                ReviewSeatsCommand.VerdictCommand(settings.Task)),
            cancellationToken);

        CommandHelpers.Finish(ansi, result.Report, settings, "review verdict", "Review");
        if (result.NextStep is { } next && ReportRenderer.IsHuman(settings.ParsedFormat))
            standard.Out.WriteLine(next);

        return result.ExitCode;
    }

    /// <summary>The verdict as printed in the console summary.</summary>
    internal static string DescribeForSummary(ReviewVerdict verdict) => ReviewVerdictLabel.Summary(verdict);
}
