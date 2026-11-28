using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Application.Arbiter;

/// <summary>Scores the step-1 answers in the decision log, read-only.</summary>
/// <param name="locator">Finds the repository's <c>artifacts/arbiter/</c> directory.</param>
/// <param name="state">Opens the decision log and the ledger.</param>
public sealed class ArbiterTuneService(IArbiterRepositoryLocator locator, IArbiterStateFactory state) : IArbiterTuneService
{
    /// <summary>The number of confidence bins: one per tenth of the unit interval.</summary>
    public const int HistogramBins = 10;

    private const string DelegateModelTrigger = ArbiterTriggers.DelegateModel;

    /// <inheritdoc />
    public ArbiterTuneReport? Score(
        string repositoryPath,
        IReadOnlyList<double> thresholds,
        string? onlyRule,
        DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(report);

        string arbiterDirectory = locator.ResolveArbiterDirectory(repositoryPath);
        return Analyze(arbiterDirectory, thresholds, onlyRule, report);
    }

    /// <inheritdoc />
    public async Task<ArbiterTuneReport?> ScoreAsync(
        string repositoryPath,
        IReadOnlyList<double> thresholds,
        string? onlyRule,
        DiagnosticReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(report);

        string arbiterDirectory = await locator.ResolveArbiterDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        return Analyze(arbiterDirectory, thresholds, onlyRule, report);
    }

    /// <summary>The tally the two entries share: reads the log under <paramref name="arbiterDirectory"/> and scores the step-1 answers.</summary>
    private ArbiterTuneReport? Analyze(
        string arbiterDirectory,
        IReadOnlyList<double> thresholds,
        string? onlyRule,
        DiagnosticReport report)
    {
        IReadOnlyList<ArbiterDecisionRecord> records;
        IReadOnlyList<ArbiterLedgerEvent> events;
        string? truncationNote;
        try
        {
            ArbiterLogRead<ArbiterDecisionRecord> decisionRead = state.OpenDecisionLog(arbiterDirectory).ReadAllWithDiagnostics();
            ArbiterLogRead<ArbiterLedgerEvent> ledgerRead = state.OpenLedger(arbiterDirectory).ReadAllWithDiagnostics();
            records = decisionRead.Records;
            events = ledgerRead.Records;
            if (ArbiterLogProblems.Unreadable(ledgerRead, decisionRead) is { } unreadable)
            {
                report.Add(Unreadable(arbiterDirectory, $"exists but could not be read: {unreadable}",
                    "tune reads nothing partial; an unreadable log is not an empty one."));
                return null;
            }

            if (decisionRead.MalformedLines + ledgerRead.MalformedLines > 0)
            {
                report.Add(Unreadable(arbiterDirectory,
                    $"holds {decisionRead.MalformedLines + ledgerRead.MalformedLines} line(s) that are not a complete record.",
                    "tune reads nothing partial; repair or remove the malformed line, then re-run."));
                return null;
            }

            truncationNote = ArbiterLogProblems.TruncationNote(ledgerRead, decisionRead);
        }
        // A truncated, hand-edited or foreign line is malformed JSON, which throws JsonException rather than
        // an IOException; tune must fail closed the same way either way.
        catch (Exception exception) when (FileFailure.Is(exception) || exception is System.Text.Json.JsonException or InvalidDataException)
        {
            report.Add(Unreadable(arbiterDirectory, $"could not be read: {exception.Message}", null));
            return null;
        }

        Dictionary<string, string> results = PairedResults(events);
        List<ArbiterTuneRule> rules = [.. Tally(records, onlyRule, results)
            .Values
            .OrderBy(tally => tally.Id, StringComparer.Ordinal)
            .Select(tally => tally.ToRule(thresholds))];
        return new ArbiterTuneReport(rules, truncationNote);
    }

    private static Diagnostic Unreadable(string arbiterDirectory, string detail, string? hint) =>
        new(
            ArbiterEvaluator.HookErrorCode,
            Severity.Error,
            $"The arbiter log under '{arbiterDirectory}' {detail}",
            arbiterDirectory,
            Hint: hint);

    private static Dictionary<string, RuleTally> Tally(
        IReadOnlyList<ArbiterDecisionRecord> records,
        string? onlyRule,
        IReadOnlyDictionary<string, string> results)
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

                if (!IsReadableConfidence(confidence))
                {
                    tally.CountUnreadable();
                    continue;
                }

                ArbiterModelBlock? model = string.Equals(record.Trigger, DelegateModelTrigger, StringComparison.Ordinal)
                    ? record.Model
                    : null;
                tally.Add(
                    record.Id,
                    confidence,
                    rule.Threshold,
                    rule.Shadow == true,
                    model,
                    results.TryGetValue(record.LedgerId, out string? result) ? result : null);
            }
        }

        return tallies;
    }

    /// <summary>
    /// A confidence the tally can place: a finite number between 0 and 1. NaN and out-of-range values are
    /// skipped and counted as unreadable, because clamping them would put them in a bin they never earned.
    /// </summary>
    private static bool IsReadableConfidence(double confidence) =>
        double.IsFinite(confidence) && confidence is >= 0 and <= 1;

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

    /// <summary>One rule's scored answers, the samples behind them, and the histogram of their confidence.</summary>
    private sealed class RuleTally(string id)
    {
        private readonly List<ArbiterTuneSample> _samples = [];

        public string Id { get; } = id;

        public int Shadow { get; private set; }

        public int Unreadable { get; private set; }

        public int[] Histogram { get; } = new int[HistogramBins];

        public void CountUnreadable() => Unreadable++;

        public void Add(
            string decisionId,
            double confidence,
            double? threshold,
            bool shadow,
            ArbiterModelBlock? model,
            string? result)
        {
            int bin = Math.Clamp((int)Math.Floor(confidence * HistogramBins), 0, HistogramBins - 1);
            Histogram[bin]++;
            if (shadow)
            {
                Shadow++;
            }

            _samples.Add(new ArbiterTuneSample(decisionId, confidence, threshold, shadow, model, result));
        }

        /// <summary>
        /// Builds the model, counting the answers that would change side at each candidate. An answer flips when its
        /// confidence is at or above the candidate on one side and below it on the other, measured against the
        /// threshold recorded on that decision. A decision with no recorded threshold cannot be measured, so it
        /// never counts as a flip.
        /// </summary>
        public ArbiterTuneRule ToRule(IReadOnlyList<double> thresholds) =>
            new(
                Id,
                _samples.Count,
                Shadow,
                _samples.Count - Shadow,
                Unreadable,
                Histogram,
                [.. thresholds.Select(candidate => new ArbiterTuneThreshold(candidate, Flips(candidate)))],
                _samples);

        private int Flips(double candidate) =>
            _samples.Count(sample => sample.Threshold is double recorded
                && sample.Confidence >= candidate != sample.Confidence >= recorded);
    }
}
