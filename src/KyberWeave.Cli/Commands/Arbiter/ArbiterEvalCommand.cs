using System.Text.Json;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Evaluates a recorded event offline through the evaluator's dry run.</summary>
public sealed class ArbiterEvalCommand : Command<ArbiterSettings>
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
    protected override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (!ArbiterCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig config, out _))
        {
            CommandHelpers.Finish(report, settings, "arbiter eval", "Dispatch");
            return 1;
        }

        if (settings.Trigger is not null)
        {
            try
            {
                ArbiterTriggerFamilies.ForTrigger(settings.Trigger);
            }
            catch (ArgumentException exception)
            {
                report.Add(new Diagnostic(
                    RuleValidator.MalformedSection,
                    Severity.Error,
                    exception.Message,
                    "--trigger"));
                CommandHelpers.Finish(report, settings, "arbiter eval", "Dispatch");
                return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.Event) || !File.Exists(settings.Event))
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                settings.Event is null
                    ? "No event file was supplied."
                    : $"Event file '{settings.Event}' does not exist.",
                "--event",
                Hint: "Pass a recorded event, for example 'arbiter eval --trigger delegate --event ./event.json --provider none'."));
            CommandHelpers.Finish(report, settings, "arbiter eval", "Dispatch");
            return 1;
        }

        ArbiterEvent ev;
        try
        {
            string json = File.ReadAllText(settings.Event);
            ev = JsonSerializer.Deserialize<ArbiterEvent>(json, EventJson)
                ?? throw new JsonException("The event file holds no event.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                $"Event file '{settings.Event}' could not be read as an ArbiterEvent: {exception.Message}",
                "--event"));
            CommandHelpers.Finish(report, settings, "arbiter eval", "Dispatch");
            return 1;
        }

        string repositoryRoot = ArbiterCommandComposition.ResolveRepositoryRoot(settings.Path);
        using ArbiterEvaluatorSession? session = ArbiterCommandComposition.CreateEvaluator(
            config, repositoryRoot, settings.Provider, report);
        if (session is null)
        {
            CommandHelpers.Finish(report, settings, "arbiter eval", "Dispatch");
            return 1;
        }

        ArbiterEvaluationResult result;
        try
        {
            result = session.Evaluator.EvaluateAsync(
                ev,
                config,
                new ArbiterEvaluatorOptions(DryRun: true, Source: "eval"),
                cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            report.Add(new Diagnostic(
                ArbiterEvaluator.HookErrorCode,
                Severity.Error,
                $"The dry-run evaluation failed closed: {exception.Message}",
                "--event"));
            CommandHelpers.Finish(report, settings, "arbiter eval", "Dispatch");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]Outcome:[/] {Markup.Escape(result.Outcome)}");
        foreach (ArbiterDispatchEvaluation dispatch in result.Dispatches)
        {
            AnsiConsole.MarkupLine(
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
                result.ErrorMessage ?? "The dry-run evaluation failed closed.",
                "--event"));
        }

        CommandHelpers.Finish(report, settings, "arbiter eval", "Dispatch");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);
}
