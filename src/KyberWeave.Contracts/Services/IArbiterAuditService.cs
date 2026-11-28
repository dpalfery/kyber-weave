using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Contracts.Services;

/// <summary>What <c>arbiter audit</c> found in the ledger and the decision log.</summary>
/// <param name="Findings">The findings counted, matching the warning and info diagnostics it added.</param>
/// <param name="EventCount">The ledger events read, in scope or not.</param>
/// <param name="Attestations">The attestations listed for information, in the order they are printed.</param>
/// <param name="ModelEvaluations">The decision-model answers behind the dispatches in scope.</param>
/// <param name="ImplementationDispatches">The dispatches in scope that went to implementation agents.</param>
public sealed record ArbiterAuditReport(
    int Findings,
    int EventCount,
    IReadOnlyList<string> Attestations,
    int ModelEvaluations,
    int ImplementationDispatches);

/// <summary>Reads the Arbiter ledger and decision log and reports what they say about past dispatches.</summary>
public interface IArbiterAuditService
{
    /// <summary>
    /// Reads the ledger and the decision log under <c>artifacts/arbiter/</c> and reports
    /// <c>KW-ARB-AUDIT-001</c> to <c>-007</c>, in that order of discovery. Attestations are listed for information and
    /// are not findings. A dispatch no hook observed leaves no record, so this cannot report it.
    /// </summary>
    /// <param name="repositoryPath">The repository whose logs are read.</param>
    /// <param name="config">The effective configuration; it says whether a decision model is configured.</param>
    /// <param name="since">The earliest event to report on, or null for every event.</param>
    /// <param name="session">The one session to report on, or null for every session.</param>
    /// <param name="matchesPlan">Whether an event's plan header is the plan the caller named, or null when none was named.</param>
    /// <param name="report">Receives the findings, the attestations and the metrics.</param>
    /// <returns>The report, or null when the log cannot be read whole; the findings say why.</returns>
    ArbiterAuditReport? Audit(
        string repositoryPath,
        KyberWeaveConfig config,
        DateTimeOffset? since,
        string? session,
        Func<string?, bool>? matchesPlan,
        DiagnosticReport report);

    /// <summary>
    /// The same audit without blocking a thread: the repository root and the arbiter state directory are
    /// resolved while git runs, and the resolution is awaited rather than waited on.
    /// </summary>
    /// <param name="repositoryPath">The repository whose logs are read.</param>
    /// <param name="config">The effective configuration; it says whether a decision model is configured.</param>
    /// <param name="since">The earliest event to report on, or null for every event.</param>
    /// <param name="session">The one session to report on, or null for every session.</param>
    /// <param name="matchesPlan">Whether an event's plan header is the plan the caller named, or null when none was named.</param>
    /// <param name="report">Receives the findings, the attestations and the metrics.</param>
    /// <param name="cancellationToken">Cancels the git run and kills the process tree.</param>
    /// <returns>The report, or null when the log cannot be read whole; the findings say why.</returns>
    Task<ArbiterAuditReport?> AuditAsync(
        string repositoryPath,
        KyberWeaveConfig config,
        DateTimeOffset? since,
        string? session,
        Func<string?, bool>? matchesPlan,
        DiagnosticReport report,
        CancellationToken cancellationToken = default);
}
