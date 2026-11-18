using System.ComponentModel;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Review;
using KyberWeave.Domain.Doctor;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     Prepares the council's seats in a few seconds, so they start while the gates run: indexes the tree, opens the
///     cycle folder, writes the diff and the change half of the evidence, writes every packet and prints each seat's
///     dispatch block, the lenses a catalogue file pattern rules out, and the gate command to run alongside.
/// </summary>
/// <remarks>The use case is <see cref="IReviewSeatsService"/>; this command parses its options and prints what it returns.</remarks>
public sealed class ReviewSeatsCommand(
    IReviewSeatsService service,
    IReviewDecisionModelFactory decisionModels,
    IConsole standard,
    IAnsiConsole ansi,
    IProcessExecutor? indexExecutor = null) : Command<ReviewSeatsSettings>
{
    /// <summary>The command description shared by the real registration and the help tests.</summary>
    public const string Description =
        "Prepare the review council's seats: index the tree, write the diff, evidence and packets, and print each seat's dispatch block.";

    private const int ClientInputExitCode = ReviewCommandResult.ClientInputExitCode;

    private static readonly TimeSpan IndexTimeout = DoctorOptions.Defaults.IndexTimeout;

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewSeatsSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.BaseRef is not null && GitChangeParsing.ValidateBase(settings.BaseRef) is { } baseProblem)
        {
            Say($"kyber-weave review seats: error: {baseProblem}");
            return ClientInputExitCode;
        }

        ReviewCommandResult result = service.Prepare(
            Request(settings),
            (repositoryRoot, config) => decisionModels.CreateForRepository(repositoryRoot, config, settings.NoDecisionModel, cachePath: null),
            indexExecutor ?? ProcessExecutor.WithTimeout(IndexTimeout),
            standard.Out,
            cancellationToken);

        if (result.ClientError is not null)
        {
            Say($"kyber-weave review seats: error: {result.ClientError}");
            return result.ExitCode;
        }

        if (result.Report is not null)
            CommandHelpers.Finish(ansi, result.Report, settings, "review seats", "Review");

        foreach (string line in result.Epilogue ?? [])
            Say(line);

        return result.ExitCode;
    }

    /// <summary>The verdict command line the reviewer copies; it needs no other argument, since the cycle folder records the rest.</summary>
    internal static string VerdictCommand(string? task) =>
        task is null ? "kyber-weave review verdict ." : $"kyber-weave review verdict . --task {task}";

    private void Say(string line) => standard.Out.WriteLine(line);

    private static ReviewSeatsRequest Request(ReviewSeatsSettings settings) => new(
        settings.Path,
        settings.Config,
        settings.Task,
        settings.BaseRef,
        settings.HandBack,
        settings.Full,
        settings.NoDecisionModel);
}

/// <summary>Options for <c>review seats</c>.</summary>
public sealed class ReviewSeatsSettings : AnalysisSettings
{
    /// <summary>The task id whose review folder holds the run's cycles.</summary>
    [CommandOption("--task <ID>")]
    [Description("The task id whose review folder, .agent_scratchpad/<task-id>/review, holds the run's cycles. Omitted: the only review folder, or the one named for the current branch.")]
    public string? Task { get; set; }

    /// <summary>The ref the change is measured from.</summary>
    [CommandOption("--base <REF>")]
    [Description("Git ref the change is measured from. Defaults to origin/HEAD.")]
    public string? BaseRef { get; set; }

    /// <summary>Whether each block names the seat file the seat writes, for the review-council skill.</summary>
    [CommandOption("--hand-back")]
    [Description("Add a 'Seat file:' line to every block, so each seat writes its output there (the review-council skill's path).")]
    public bool HandBack { get; set; }

    /// <summary>Whether a later cycle seats every applicable lens instead of only those the fix needs.</summary>
    [CommandOption("--full")]
    [Description("In a later cycle, seat every applicable lens instead of only the lenses that raised the findings being fixed. A person's choice (the full mode); the review loop never passes it.")]
    public bool Full { get; set; }

    /// <summary>Whether the decision model is left out of the lens-applicability step.</summary>
    [CommandOption("--no-decision-model")]
    [Description("Do not ask the decision model (arbiter.provider) whether each judgement lens applies. Without a configured model nothing is asked anyway.")]
    public bool NoDecisionModel { get; set; }
}
