using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// Scores the step-1 answers in the decision log so a threshold can be chosen from evidence.
/// </summary>
/// <remarks>
/// Read-only: it opens <c>decisions.jsonl</c> and <c>ledger.jsonl</c> under <c>artifacts/arbiter/</c>
/// and writes nothing, not even the directory. For each step-1 rule it counts the scored answers,
/// splits them into shadow and enforced, histograms their confidence in tenths, and counts how many
/// answers would change side at each candidate threshold. A flip is measured against the threshold
/// recorded on each decision, because that is the threshold the answer was actually judged by.
/// A decision whose ledger event carries a return shows that return's <c>RESULT</c> beside its score.
/// </remarks>
public sealed class ArbiterTuneCommand : Command<ArbiterSettings>
{
    /// <summary>The number of confidence bins: one per tenth of the unit interval.</summary>
    public const int HistogramBins = 10;

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

        string arbiterDirectory = ArbiterCommandComposition.ResolveArbiterDirectory(settings.Path);
        IReadOnlyList<ArbiterDecisionRecord> records;
        IReadOnlyList<ArbiterLedgerEvent> events;
        try
        {
            (records, int malformedDecisions) = new DecisionLog(arbiterDirectory).ReadAllWithDiagnostics();
            (events, int malformedEvents) = new InFlightLedger(arbiterDirectory).ReadAllWithDiagnostics();
            if (malformedDecisions + malformedEvents > 0)
            {
                report.Add(new Diagnostic(
                    ArbiterEvaluator.HookErrorCode,
                    Severity.Error,
                    $"The arbiter log under '{arbiterDirectory}' holds {malformedDecisions + malformedEvents} line(s) that are not a complete record.",
                    arbiterDirectory,
                    "tune reads nothing partial; repair or remove the malformed line, then re-run."));
                CommandHelpers.Finish(report, settings, CommandName, "Event");
                return 1;
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException
            or NotSupportedException
            or InvalidDataException)
        {
            report.Add(new Diagnostic(
                ArbiterEvaluator.HookErrorCode,
                Severity.Error,
                $"The arbiter log under '{arbiterDirectory}' could not be read: {exception.Message}",
                arbiterDirectory));
            CommandHelpers.Finish(report, settings, CommandName, "Event");
            return 1;
        }

        Dictionary<string, RuleTally> tallies = Tally(records, settings.Rule);
        Dictionary<string, string> results = PairedResults(events);

        CommandHelpers.WriteUnfolded(() =>
        {
            if (settings.Json)
            {
                WriteJson(tallies, thresholds);
            }
            else
            {
                WriteText(tallies, thresholds, results);
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

    private static Dictionary<string, RuleTally> Tally(IReadOnlyList<ArbiterDecisionRecord> records, string? onlyRule)
    {
        Dictionary<string, RuleTally> tallies = new(StringComparer.Ordinal);
        foreach (ArbiterDecisionRecord record in records)
        {
            foreach (ArbiterDecisionRule rule in record.Rules)
            {
                if (rule.Step != 1 || rule.Confidence is not double confidence)
                {
                    continue;
                }

                if (onlyRule is not null && !string.Equals(rule.Id, onlyRule, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!tallies.TryGetValue(rule.Id, out RuleTally? tally))
                {
                    tally = new RuleTally(rule.Id);
                    tallies[rule.Id] = tally;
                }

                tally.Add(record.Id, record.LedgerId, confidence, rule.Threshold, rule.Shadow == true);
            }
        }

        return tallies;
    }

    /// <summary>The return's <c>RESULT</c> keyed by the decision's ledger id, from the post event that names the pre.</summary>
    private static Dictionary<string, string> PairedResults(IReadOnlyList<ArbiterLedgerEvent> events)
    {
        Dictionary<string, string> results = new(StringComparer.Ordinal);
        foreach (ArbiterLedgerEvent ev in events)
        {
            if (!string.Equals(ev.Phase, ArbiterLedgerPhases.Post, StringComparison.Ordinal)
                || ev.PreId is null
                || ev.Returns?.TaskReview is not string result)
            {
                continue;
            }

            results[ev.PreId] = result;
        }

        return results;
    }

    private static void WriteText(
        IReadOnlyDictionary<string, RuleTally> tallies,
        double[] thresholds,
        IReadOnlyDictionary<string, string> results)
    {
        if (tallies.Count == 0)
        {
            AnsiConsole.WriteLine(NoScoredAnswers);
            return;
        }

        foreach (RuleTally tally in tallies.Values.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            AnsiConsole.MarkupLine(
                $"[bold]{Markup.Escape(tally.Id)}[/]  scored {tally.Scored}  shadow {tally.Shadow}  enforced {tally.Enforced}");
            AnsiConsole.MarkupLine($"  confidence (tenths): {string.Join(' ', tally.Histogram)}");
            foreach (ThresholdFlips flips in tally.FlipsAt(thresholds))
            {
                AnsiConsole.MarkupLine(
                    $"  threshold {flips.Threshold.ToString("0.00", CultureInfo.InvariantCulture)}: {flips.Changed} flip(s)");
            }

            foreach (RuleSample sample in tally.Samples)
            {
                if (!results.TryGetValue(sample.LedgerId, out string? result))
                {
                    continue;
                }

                AnsiConsole.MarkupLine(
                    $"  decision {Markup.Escape(sample.DecisionId)} score {sample.Confidence.ToString("0.00", CultureInfo.InvariantCulture)} RESULT: {Markup.Escape(result)}");
            }
        }
    }

    private static void WriteJson(IReadOnlyDictionary<string, RuleTally> tallies, double[] thresholds)
    {
        List<JsonRuleReport> rules = [];
        foreach (RuleTally tally in tallies.Values.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            rules.Add(new JsonRuleReport(
                tally.Id,
                tally.Scored,
                tally.Shadow,
                tally.Enforced,
                tally.Histogram,
                [.. tally.FlipsAt(thresholds).Select(f => new JsonThresholdReport(f.Threshold, f.Changed))]));
        }

        string json = JsonSerializer.Serialize(new JsonTuneReport(rules), JsonOptions);
        AnsiConsole.WriteLine(json);
    }

    /// <summary>One rule's scored answers, the samples behind them, and the histogram of their confidence.</summary>
    private sealed class RuleTally(string id)
    {
        private readonly List<RuleSample> _samples = [];

        public string Id { get; } = id;

        public int Scored => _samples.Count;

        public int Shadow { get; private set; }

        public int Enforced => Scored - Shadow;

        public int[] Histogram { get; } = new int[HistogramBins];

        public IReadOnlyList<RuleSample> Samples => _samples;

        public void Add(string decisionId, string ledgerId, double confidence, double? threshold, bool shadow)
        {
            int bin = Math.Clamp((int)Math.Floor(confidence * HistogramBins), 0, HistogramBins - 1);
            Histogram[bin]++;
            if (shadow)
            {
                Shadow++;
            }

            _samples.Add(new RuleSample(decisionId, ledgerId, confidence, threshold, shadow));
        }

        /// <summary>
        /// Counts the answers that would change side at each candidate. An answer flips when its
        /// confidence is at or above the candidate on one side and below it on the other, measured
        /// against the threshold recorded on that decision. Decisions with no recorded threshold
        /// cannot be measured, so they never count as a flip.
        /// </summary>
        public IEnumerable<ThresholdFlips> FlipsAt(IEnumerable<double> candidates) =>
            candidates.Select(candidate => new ThresholdFlips(
                candidate,
                _samples.Count(s => s.Threshold is double recorded
                    && (s.Confidence >= candidate) != (s.Confidence >= recorded))));
    }

    /// <summary>One scored answer, kept with the threshold it was judged by and the ledger event it pairs with.</summary>
    private sealed record RuleSample(string DecisionId, string LedgerId, double Confidence, double? Threshold, bool Shadow);

    private sealed record ThresholdFlips(double Threshold, int Changed);

    private sealed record JsonTuneReport([property: JsonPropertyName("rules")] IReadOnlyList<JsonRuleReport> Rules);

    private sealed record JsonRuleReport(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("scored")] int Scored,
        [property: JsonPropertyName("shadow")] int Shadow,
        [property: JsonPropertyName("enforced")] int Enforced,
        [property: JsonPropertyName("histogram")] IReadOnlyList<int> Histogram,
        [property: JsonPropertyName("thresholds")] IReadOnlyList<JsonThresholdReport> Thresholds);

    private sealed record JsonThresholdReport(
        [property: JsonPropertyName("threshold")] double Threshold,
        [property: JsonPropertyName("changed")] int Changed);
}
