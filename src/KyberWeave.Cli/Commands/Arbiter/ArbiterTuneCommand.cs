using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// Scores the step-1 answers in the decision log so a threshold can be chosen from evidence.
/// </summary>
/// <remarks>
/// Read-only: the scoring service reads <c>decisions.jsonl</c> and <c>ledger.jsonl</c> under
/// <c>artifacts/arbiter/</c> and writes nothing, not even the directory. For each step-1 rule this prints the count of
/// scored answers split into shadow and enforced, their confidence histogram in tenths, and how many answers would
/// change side at each candidate threshold. A decision whose ledger event carries a return shows that return's
/// <c>RESULT</c> beside its score.
/// </remarks>
public sealed class ArbiterTuneCommand(IArbiterTuneService tune, IAnsiConsole console) : Command<ArbiterSettings>
{
    /// <summary>The candidate thresholds when <c>--thresholds</c> is not given.</summary>
    public static readonly double[] DefaultThresholds = [0.5, 0.6, 0.7, 0.8, 0.9];

    private const string NoScoredAnswers = "no scored answers.";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const string CommandName = "arbiter tune";

    /// <summary>Reports the scored answers per rule. Exits 1 only when the logs cannot be read whole.</summary>
    public override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiagnosticReport report = new();
        if (!TryParseThresholds(settings.Thresholds, out double[] thresholds, out string? problem))
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                problem!,
                "--thresholds",
                Hint: "Pass values between 0 and 1, for example 0.5,0.6,0.7."));
            CommandHelpers.Finish(report, settings, CommandName, "Event");
            return 1;
        }

        if (tune.Score(settings.Path, thresholds, settings.Rule, report) is not { } scored)
        {
            CommandHelpers.Finish(report, settings, CommandName, "Event");
            return 1;
        }

        CommandHelpers.WriteUnfolded(console, () =>
        {
            if (settings.Json)
            {
                WriteJson(scored);
            }
            else
            {
                if (scored.TruncationNote is not null)
                {
                    console.WriteLine($"warning {ArbiterAuditCommand.LogWindowTruncated}: {scored.TruncationNote} The tallies below cover only the newest records.");
                }

                WriteText(scored);
            }
        });
        return 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    /// <summary>Parses <c>--thresholds</c>: a comma-separated list of values in [0, 1]; the defaults when absent.</summary>
    public static bool TryParseThresholds(string? text, out double[] thresholds, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            thresholds = DefaultThresholds;
            return true;
        }

        List<double> parsed = [];
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // NaN compares false against both bounds, so it needs its own finiteness check.
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value)
                || value < 0
                || value > 1)
            {
                thresholds = [];
                problem = $"--thresholds value '{part}' is not a number between 0 and 1.";
                return false;
            }

            parsed.Add(value);
        }

        if (parsed.Count == 0)
        {
            thresholds = [];
            problem = "--thresholds names no value; pass a comma-separated list such as 0.5,0.6,0.7.";
            return false;
        }

        thresholds = [.. parsed];
        return true;
    }

    private void WriteText(ArbiterTuneReport report)
    {
        if (report.Rules.Count == 0)
        {
            console.WriteLine(NoScoredAnswers);
            return;
        }

        foreach (ArbiterTuneRule rule in report.Rules)
        {
            console.MarkupLine(
                $"[bold]{Markup.Escape(rule.Id)}[/]  scored {rule.Scored}  shadow {rule.Shadow}  enforced {rule.Enforced}{UnreadableSuffix(rule)}");
            console.MarkupLine($"  confidence (tenths): {string.Join(' ', rule.Histogram)}");
            foreach (ArbiterTuneThreshold flips in rule.Thresholds)
            {
                console.MarkupLine(
                    $"  threshold {flips.Threshold.ToString("0.00", CultureInfo.InvariantCulture)}: {flips.Changed} flip(s)");
            }

            foreach (ArbiterTuneSample sample in rule.Samples)
            {
                if (sample.Result is not { } result)
                {
                    continue;
                }

                string modelText = sample.Model is null
                    ? string.Empty
                    : $" profile {Markup.Escape(sample.Model.Profile)} token {Markup.Escape(sample.Model.Token ?? "none")} delivered {(sample.Model.Delivered ? "true" : "false")}";
                console.MarkupLine(
                    $"  decision {Markup.Escape(sample.DecisionId)} score {sample.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}{modelText} RESULT: {Markup.Escape(result)}");
            }
        }
    }

    private static string UnreadableSuffix(ArbiterTuneRule rule) =>
        rule.Unreadable == 0 ? string.Empty : $"  unreadable {rule.Unreadable}";

    private void WriteJson(ArbiterTuneReport report)
    {
        List<JsonRuleReport> rules = [];
        foreach (ArbiterTuneRule rule in report.Rules)
        {
            List<JsonModelRow> model = [];
            foreach (ArbiterTuneSample sample in rule.Samples)
            {
                if (sample.Model is null)
                {
                    continue;
                }

                model.Add(new JsonModelRow(
                    sample.DecisionId,
                    sample.Shadow,
                    sample.Model.Profile,
                    sample.Model.Token,
                    sample.Model.Delivered,
                    sample.Result));
            }

            rules.Add(new JsonRuleReport(
                rule.Id,
                rule.Scored,
                rule.Shadow,
                rule.Enforced,
                rule.Unreadable,
                rule.Histogram,
                [.. rule.Thresholds.Select(flips => new JsonThresholdReport(flips.Threshold, flips.Changed))],
                model));
        }

        string json = JsonSerializer.Serialize(new JsonTuneReport(rules, report.TruncationNote), JsonOptions);
        console.WriteLine(json);
    }

    /// <summary>One <c>delegate.model</c> answer's model row in the JSON report: what it targeted and whether it was delivered.</summary>
    private sealed record JsonModelRow(
        [property: JsonPropertyName("decision")] string Decision,
        [property: JsonPropertyName("shadow")] bool Shadow,
        [property: JsonPropertyName("profile")] string Profile,
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("delivered")] bool Delivered,
        [property: JsonPropertyName("result")] string? Result);

    private sealed record JsonTuneReport(
        [property: JsonPropertyName("rules")] IReadOnlyList<JsonRuleReport> Rules,
        [property: JsonPropertyName("truncated")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Truncated);

    private sealed record JsonRuleReport(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("scored")] int Scored,
        [property: JsonPropertyName("shadow")] int Shadow,
        [property: JsonPropertyName("enforced")] int Enforced,
        [property: JsonPropertyName("unreadable")] int Unreadable,
        [property: JsonPropertyName("histogram")] IReadOnlyList<int> Histogram,
        [property: JsonPropertyName("thresholds")] IReadOnlyList<JsonThresholdReport> Thresholds,
        [property: JsonPropertyName("model")] IReadOnlyList<JsonModelRow> Model);

    private sealed record JsonThresholdReport(
        [property: JsonPropertyName("threshold")] double Threshold,
        [property: JsonPropertyName("changed")] int Changed);
}
