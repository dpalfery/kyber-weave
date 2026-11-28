using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Contracts.Services;

/// <summary>One step-1 answer kept with the threshold it was judged by and the return it pairs with.</summary>
/// <param name="DecisionId">The decision that recorded the answer.</param>
/// <param name="Confidence">The answer's confidence, a finite number between 0 and 1.</param>
/// <param name="Threshold">The threshold the answer was judged by, or null when the decision recorded none.</param>
/// <param name="Shadow">Whether the answer was recorded as a shadow answer rather than enforced.</param>
/// <param name="Model">The <c>delegate.model</c> answer's model row, when the decision carries one.</param>
/// <param name="Result">The paired return's <c>RESULT</c>, when the ledger has one.</param>
public sealed record ArbiterTuneSample(
    string DecisionId,
    double Confidence,
    double? Threshold,
    bool Shadow,
    ArbiterModelBlock? Model,
    string? Result);

/// <summary>How many answers would change side at one candidate threshold.</summary>
/// <param name="Threshold">The candidate threshold.</param>
/// <param name="Changed">The answers whose side the candidate would flip.</param>
public sealed record ArbiterTuneThreshold(double Threshold, int Changed);

/// <summary>One rule's scored answers: the counts, the histogram and the samples behind them.</summary>
/// <param name="Id">The rule id.</param>
/// <param name="Scored">The answers the tally could place.</param>
/// <param name="Shadow">The scored answers recorded as shadow.</param>
/// <param name="Enforced">The scored answers that were enforced.</param>
/// <param name="Unreadable">The answers whose confidence was not a finite number between 0 and 1.</param>
/// <param name="Histogram">One bin per tenth of the confidence interval, low to high.</param>
/// <param name="Thresholds">The flips at each candidate threshold, in the order the candidates were given.</param>
/// <param name="Samples">The scored answers, in decision-log order.</param>
public sealed record ArbiterTuneRule(
    string Id,
    int Scored,
    int Shadow,
    int Enforced,
    int Unreadable,
    IReadOnlyList<int> Histogram,
    IReadOnlyList<ArbiterTuneThreshold> Thresholds,
    IReadOnlyList<ArbiterTuneSample> Samples);

/// <summary>What the decision log and the ledger say about the scored answers.</summary>
/// <param name="Rules">One entry per rule with a scored answer, ordered by rule id.</param>
/// <param name="TruncationNote">The note to print when the log window was truncated, or null.</param>
public sealed record ArbiterTuneReport(IReadOnlyList<ArbiterTuneRule> Rules, string? TruncationNote);

/// <summary>Scores the step-1 answers in the decision log, read-only, so a threshold can be chosen from evidence.</summary>
public interface IArbiterTuneService
{
    /// <summary>
    /// Reads <c>decisions.jsonl</c> and <c>ledger.jsonl</c> under <c>artifacts/arbiter/</c> and tallies the step-1
    /// answers per rule. Fails closed: an unreadable log, or one holding a line that is not a complete record, is an
    /// error finding rather than an empty tally.
    /// </summary>
    /// <param name="repositoryPath">The repository whose log is read.</param>
    /// <param name="thresholds">The candidate thresholds whose flips are counted.</param>
    /// <param name="onlyRule">The one rule id to tally, or null for every rule.</param>
    /// <param name="report">Receives the findings.</param>
    /// <returns>The tally, or null when the log cannot be read whole; the findings say why.</returns>
    ArbiterTuneReport? Score(string repositoryPath, IReadOnlyList<double> thresholds, string? onlyRule, DiagnosticReport report);

    /// <summary>
    /// The same tally without blocking a thread: the repository root and the arbiter state directory are
    /// resolved while git runs, and the resolution is awaited rather than waited on.
    /// </summary>
    /// <param name="repositoryPath">The repository whose log is read.</param>
    /// <param name="thresholds">The candidate thresholds whose flips are counted.</param>
    /// <param name="onlyRule">The one rule id to tally, or null for every rule.</param>
    /// <param name="report">Receives the findings.</param>
    /// <param name="cancellationToken">Cancels the git run and kills the process tree.</param>
    /// <returns>The tally, or null when the log cannot be read whole; the findings say why.</returns>
    Task<ArbiterTuneReport?> ScoreAsync(
        string repositoryPath,
        IReadOnlyList<double> thresholds,
        string? onlyRule,
        DiagnosticReport report,
        CancellationToken cancellationToken = default);
}
