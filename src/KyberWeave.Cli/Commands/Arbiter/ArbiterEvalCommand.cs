using KyberWeave.Contracts.Services;
using KyberWeave.Contracts.Repositories;
using System.Text.Json;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Evaluates a recorded event offline through the evaluator's dry run.</summary>
public sealed class ArbiterEvalCommand(
    IArbiterConfigResolver configs,
    IArbiterRepositoryLocator locator,
    IArbiterDryRunService dryRun,
    IFileSystem files,
    IAnsiConsole console) : AsyncCommand<ArbiterSettings>
{
    private static readonly JsonSerializerOptions EventJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads the <c>ArbiterEvent</c> JSON file named by <c>--event</c> and runs
    /// it through <see cref="ArbiterEvaluator"/> as a dry run: the ledger is
    /// read and never appended, so the command writes nothing. Prints the
    /// event outcome and every dispatch's outcome; an error result prints its
    /// <c>KW-ARB-HOOK-001</c> envelope instead. Exits 1 on an unreadable event,
    /// an unknown <c>--trigger</c>, or an error result.
    /// </summary>
    public override async Task<int> ExecuteAsync(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        if (!configs.TryResolve(settings.Path, settings.Config, report, out KyberWeaveConfig config, out _))
        {
            CommandHelpers.Finish(console, report, settings, "arbiter eval", "Dispatch");
            return 1;
        }

        if (!ValidateTrigger(settings.Trigger, report))
        {
            CommandHelpers.Finish(console, report, settings, "arbiter eval", "Dispatch");
            return 1;
        }

        ArbiterEvent? ev = LoadEvent(settings.Event, report);
        if (ev is null)
        {
            CommandHelpers.Finish(console, report, settings, "arbiter eval", "Dispatch");
            return 1;
        }

        string repositoryRoot = await locator.ResolveRepositoryRootAsync(settings.Path, cancellationToken).ConfigureAwait(false);
        using IArbiterEvaluatorSession? session = dryRun.CreateEvaluator(
            config, repositoryRoot, settings.Provider, report);
        if (session is null)
        {
            CommandHelpers.Finish(console, report, settings, "arbiter eval", "Dispatch");
            return 1;
        }

        ArbiterEvaluationResult result;
        try
        {
            result = await session.Evaluator.EvaluateAsync(
                ev,
                config,
                new ArbiterEvaluatorOptions(DryRun: true, Source: "eval"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Documented boundary: a failed evaluation records its hook error and exits 1 rather than crashing the host.
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            report.Add(new Diagnostic(
                ArbiterEvaluator.HookErrorCode,
                Severity.Error,
                $"The dry-run evaluation failed closed: {exception.Message}",
                "--event"));
            CommandHelpers.Finish(console, report, settings, "arbiter eval", "Dispatch");
            return 1;
        }

        console.MarkupLine($"[bold]Outcome:[/] {Markup.Escape(result.Outcome)}");
        foreach (ArbiterDispatchEvaluation dispatch in result.Dispatches)
        {
            console.MarkupLine(
                $"[grey]Trigger {Markup.Escape(dispatch.Trigger ?? "—")}: {Markup.Escape(dispatch.Outcome)}.[/]");
        }

        if (!string.IsNullOrEmpty(settings.Trigger)
            && result.Dispatches.Count > 0
            && result.Dispatches.All(dispatch =>
                !string.Equals(dispatch.Trigger, settings.Trigger, StringComparison.Ordinal)))
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Warning,
                $"The event classified as '{result.Dispatches[0].Trigger}' rather than '--trigger {settings.Trigger}'.",
                "--trigger",
                Hint: "Omit --trigger, or pass the trigger the event classifies as."));
        }

        if (result.IsError)
        {
            report.Add(new Diagnostic(
                result.ErrorCode ?? ArbiterEvaluator.HookErrorCode,
                Severity.Error,
                result.ErrorMessage ?? "The dry-run evaluation failed; a hook would allow the dispatch and log this error.",
                "--event"));
        }

        CommandHelpers.Finish(console, report, settings, "arbiter eval", "Dispatch");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Validates the <c>--trigger</c> value; an unknown trigger is reported and false returned.</summary>
    private bool ValidateTrigger(string? trigger, DiagnosticReport report)
    {
        if (trigger is null)
            return true;

        try
        {
            ArbiterTriggerFamilies.ForTrigger(trigger);
            return true;
        }
        catch (ArgumentException exception)
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                exception.Message,
                "--trigger"));
            return false;
        }
    }

    /// <summary>Reads and parses the event file named by <c>--event</c>; a missing or unreadable file is reported and null returned.</summary>
    private ArbiterEvent? LoadEvent(string? eventPath, DiagnosticReport report)
    {
        if (string.IsNullOrWhiteSpace(eventPath) || !files.FileExists(eventPath))
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                eventPath is null
                    ? "No event file was supplied."
                    : $"Event file '{eventPath}' does not exist.",
                "--event",
                Hint: "Pass a recorded event, for example 'arbiter eval --trigger delegate --event ./event.json --provider none'."));
            return null;
        }

        try
        {
            string json = files.ReadAllText(eventPath);
            return JsonSerializer.Deserialize<ArbiterEvent>(json, EventJson)
                ?? throw new JsonException("The event file holds no event.");
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is JsonException)
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                $"Event file '{eventPath}' could not be read as an ArbiterEvent: {exception.Message}",
                "--event"));
            return null;
        }
    }

}
