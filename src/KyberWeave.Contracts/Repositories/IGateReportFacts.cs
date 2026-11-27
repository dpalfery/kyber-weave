using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the gate report a worker's return cites and answers the facts the gate rules read from it.</summary>
public interface IGateReportFacts
{
    /// <summary>Reads the gate report for a repository root.</summary>
    /// <returns>The report, or <c>null</c> when it is missing or unreadable.</returns>
    GateReport? TryRead(string repositoryRoot);

    /// <summary>Reads a gate report from its file path.</summary>
    /// <returns>The report, or <c>null</c> when it is missing or unreadable.</returns>
    GateReport? TryReadFile(string gatesJsonPath);

    /// <summary>
    /// Merges <c>AgreesFact</c> into <paramref name="facts"/> when the trigger
    /// declares it, leaving every other trigger's bag untouched.
    /// </summary>
    /// <param name="facts">The fact bag built for the event's trigger.</param>
    /// <param name="ev">The event; only a return carries the text to verify.</param>
    ArbiterFactSet Enrich(ArbiterFactSet facts, ArbiterEvent ev);

    /// <summary>
    /// Whether the repository's gate report shows a failed gate, which corroborates a lens finding. The same
    /// report reader <see cref="Agrees(string?, string?)"/> uses, so a finding and a digest are checked against one source.
    /// </summary>
    /// <param name="repositoryRoot">The root a relative report path resolves against.</param>
    /// <param name="reviewRun">The cycle the dispatch names, or null when it names none.</param>
    /// <returns><c>true</c> when a gate ran and failed; <c>false</c> when the report is missing or shows none failed.</returns>
    bool FailedGateCorroborates(string? repositoryRoot, string? reviewRun);

    /// <summary>Whether a returned digest's gate report corroborates its claimed status.</summary>
    /// <param name="returnedText">The worker's return text, digest included.</param>
    /// <param name="repositoryRoot">The root a relative report path resolves against.</param>
    /// <returns>
    /// <c>true</c> when the named report corroborates a <c>PASS</c> claim, <c>false</c> when
    /// it contradicts the claim or the claim is one no report can corroborate, and
    /// <c>null</c> when there is no claim or no report to read. False is a machine finding and
    /// escalates; null is undecidable and goes to the model question.
    /// </returns>
    bool? Agrees(string? returnedText, string? repositoryRoot);

    /// <summary>
    /// Whether a returned digest's passing report was preceded by a failing run of the same task gate: the
    /// worker wrote its tests, ran the gate with <c>--red</c>, then implemented and ran it again. Both runs write
    /// reports, so the proof is two files in one folder, not a line the worker composes.
    /// </summary>
    /// <param name="returnedText">The worker's return text, digest included.</param>
    /// <param name="repositoryRoot">The root a relative report path resolves against.</param>
    /// <returns>
    /// <c>true</c> when the claimed report is a clean pass and a failing-run report in its folder was written
    /// before it; <c>false</c> when the claimed report is a clean pass with no older failing-run report beside it,
    /// or is itself the failing-run report; <c>null</c> when there is no claim, no report, or the claimed report
    /// is not a clean pass (<c>KW-ARB-GATE-REPORT-001</c> judges that).
    /// </returns>
    bool? FailingRunFirst(string? returnedText, string? repositoryRoot);
}
