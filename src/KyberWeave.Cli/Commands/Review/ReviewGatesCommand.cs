using System.ComponentModel;

using KyberWeave.Cli.Rendering;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Doctor;
using KyberWeave.Domain.Doctor;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     Runs the deterministic gates the host declares under <c>review.gates</c>, or, with <c>--agent</c>,
///     one developer agent's gates under <c>review.task-gates</c> against the change that agent made.
/// </summary>
/// <remarks>The use case is <see cref="IReviewGatesService"/>; this command parses its options and renders what it returns.</remarks>
public sealed class ReviewGatesCommand(
    IReviewGatesService service,
    IConsole standard,
    IAnsiConsole ansi,
    IProcessRunner processes) : AsyncCommand<ReviewGatesSettings>
{
    private const int ClientInputExitCode = ReviewCommandResult.ClientInputExitCode;

    /// <summary>The bound on the CodeGraph index run, the same default <c>doctor --ensure</c> uses.</summary>
    internal static readonly TimeSpan IndexTimeout = DoctorOptions.Defaults.IndexTimeout;

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, ReviewGatesSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // An option given an empty value names nothing: as a path it resolves to the current directory, and as
        // --agent or --config it would fall back silently. Refused before anything runs.
        string[] empty = new (string Option, string? Value)[]
            {
                ("[path]", settings.Path), ("--config", settings.Config), ("--out", settings.Out),
                ("--agent", settings.Agent)
            }
            .Where(entry => entry.Value is { } given && string.IsNullOrWhiteSpace(given))
            .Select(entry => entry.Option)
            .ToArray();
        if (empty.Length > 0)
        {
            WriteClientInputError(
                $"{ReviewGateReporting.InvalidInput}: {string.Join(", ", empty)} was given an empty value. Name a value, or leave the " +
                "option out.");
            return ClientInputExitCode;
        }

        bool scopesTheRun = settings.Agent is not null || settings.BaseRef is not null
                            || settings.Paths.Length > 0 || settings.Gate.Length > 0;
        if (settings.All && scopesTheRun)
        {
            WriteClientInputError(
                $"{ReviewGateReporting.InvalidInput}: --all runs all declared repository gates unscoped and cannot be combined with " +
                "--agent, --base, --paths, or --gate.");
            return ClientInputExitCode;
        }

        // --deadline bounds the council's whole run. A named gate is a deliberate re-check outside that budget, and a
        // task-gate run is one developer's check, so neither may be cut by it.
        if (settings.Deadline && (settings.Gate.Length > 0 || settings.Agent is not null))
        {
            WriteClientInputError(
                $"{ReviewGateReporting.InvalidInput}: --deadline bounds the full council run, so it cannot be combined with " +
                (settings.Gate.Length > 0 ? "--gate, which re-checks named gates outside that budget." :
                    "--agent, which runs one agent's task gates."));
            return ClientInputExitCode;
        }

        // --gate selects from review.gates, and --agent selects from review.task-gates. A run cannot be both,
        // so it is refused rather than silently resolving to one of them.
        if (settings.Gate.Length > 0 && settings.Agent is not null)
        {
            WriteClientInputError(
                $"{ReviewGateReporting.InvalidInput}: --gate names a gate declared under review.gates, so it cannot be combined with " +
                "--agent, which runs one agent's task gates.");
            return ClientInputExitCode;
        }

        // --council admits the council's full-suite gates under review.gates, and --agent runs one agent's
        // review.task-gates, which never read --council. A run cannot be both, so it is refused rather than silently
        // dropping --council.
        if (settings.Council && settings.Agent is not null)
        {
            WriteClientInputError(
                $"{ReviewGateReporting.InvalidInput}: --council runs the council's gates under review.gates, so it cannot be " +
                "combined with --agent, which runs one agent's task gates.");
            return ClientInputExitCode;
        }

        // --council writes the full council report, and a council run that names no --out file would finish with its
        // report nowhere. Refused before any gate runs.
        if (settings.Council && settings.Out is null)
        {
            WriteClientInputError(
                $"{ReviewGateReporting.InvalidInput}: --council needs --out, the file the full report is written to. " +
                "review seats prints the complete command for a council run.");
            return ClientInputExitCode;
        }

        if (settings.Agent is null && settings.HasAgentOnlyOptions)
        {
            WriteClientInputError(
                "--task, --fix and --red apply only with --agent. Name the agent whose task " +
                "gates to run, or drop those options.");
            return ClientInputExitCode;
        }

        ReviewCommandResult result = await service.RunAsync(
            Request(settings),
            new BoundedRunner(processes, IndexTimeout),
            standard.Out,
            cancellationToken).ConfigureAwait(false);

        if (result.ClientError is not null)
        {
            WriteClientInputError(result.ClientError);
            return result.ExitCode;
        }

        if (result.Report is not null)
            CommandHelpers.Finish(ansi, result.Report, settings, "review gates", "Gate");

        foreach (string line in result.Epilogue ?? [])
            await standard.Out.WriteLineAsync(line).ConfigureAwait(false);
        await standard.Out.FlushAsync(cancellationToken).ConfigureAwait(false);

        return result.ExitCode;
    }

    private ReviewGatesRequest Request(ReviewGatesSettings settings) => new(
        settings.Path,
        settings.Config,
        settings.Out,
        settings.Agent,
        settings.BaseRef,
        settings.Paths,
        settings.Gate,
        settings.All,
        settings.Deadline,
        settings.Council,
        settings.StopOnFailure,
        settings.Full,
        settings.TaskSlug,
        settings.Fix,
        settings.Red,
        ReportRenderer.IsHuman(settings.FormatFor(ansi)),
        !standard.IsInputRedirected);

    private void WriteClientInputError(string message)
    {
        ansi.MarkupLine($"[red]kyber-weave review gates: error: {Markup.Escape(message)}[/]");
    }

    /// <summary>Bounds every child at the index timeout unless the run names its own, so the index cannot outlive the review.</summary>
    /// <param name="runner">The process seam that starts the child and kills it when the limit passes.</param>
    /// <param name="timeout">The limit applied to a run that names none of its own.</param>
    private sealed class BoundedRunner(IProcessRunner runner, TimeSpan timeout) : IProcessRunner
    {
        /// <inheritdoc />
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return runner.RunAsync(request with { Timeout = request.Timeout ?? timeout }, cancellationToken);
        }

        /// <inheritdoc />
        public bool IsAlive(int processId) => runner.IsAlive(processId);

        /// <inheritdoc />
        public string? FindOnPath(string program) => runner.FindOnPath(program);
    }
}
