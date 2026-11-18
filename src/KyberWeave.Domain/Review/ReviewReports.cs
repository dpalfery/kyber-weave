namespace KyberWeave.Core.Review;

/// <summary>The gate suite's result document.</summary>
/// <param name="Schema">Format identifier, for readers that must tolerate later versions.</param>
/// <param name="Gates">One entry per declared gate, in declaration order.</param>
/// <param name="Coverage">Measured coverage, when a gate produced any.</param>
/// <param name="CoverageReports">
/// Every Cobertura report the run wrote, <see cref="Coverage"/>'s own included. A suite that
/// tests more than one language writes one report each, and only one of them can be the
/// figure a coverage floor is checked against; the rest are evidence a reviewer reads here.
/// </param>
/// <param name="Base">
/// The commit the change was measured against. Absent in reports written before the
/// Arbiter started recording it; readers treat a missing base as an absent fact.
/// </param>
public sealed record GateReport(
    string Schema,
    IReadOnlyList<GateResult> Gates,
    CoverageResult? Coverage = null,
    IReadOnlyList<CoverageReport>? CoverageReports = null,
    string? Base = null)
{
    /// <summary>The current gate report format.</summary>
    public const string CurrentSchema = "kyber-weave.review-gates/v1";
}

/// <summary>The council's findings document.</summary>
/// <param name="Schema">Format identifier.</param>
/// <param name="Findings">Findings surviving the council's own confirmation pass.</param>
/// <param name="ChangedPaths">Repository-relative paths the change touches.</param>
/// <param name="ChangedLines">Total added and removed lines.</param>
/// <param name="PathLineCounts">Per-path added and removed lines, when the producer counted them.</param>
/// <param name="Coverage">Per-lens outcome of the council, when the producer recorded it.</param>
public sealed record FindingsReport(
    string Schema,
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<string> ChangedPaths,
    int ChangedLines,
    IReadOnlyDictionary<string, int>? PathLineCounts = null,
    IReadOnlyList<LensCoverage>? Coverage = null)
{
    /// <summary>The current findings format.</summary>
    public const string CurrentSchema = "kyber-weave.review-findings/v1";

    /// <summary>
    /// The scope metadata this report describes. The verdict engine does not consume it; this is
    /// a convenience accessor for reporting the change's size and paths.
    /// </summary>
    public ReviewScope ToScope()
    {
        return new ReviewScope(ChangedPaths, ChangedLines, PathLineCounts);
    }
}

/// <summary>One council lens's outcome in the findings document.</summary>
/// <param name="Lens">The lens name, as listed in the review lens catalogue.</param>
/// <param name="Outcome">How the lens ended, such as findings, no findings, or skipped.</param>
/// <param name="Reason">Why the lens ended that way, when it did not produce findings.</param>
public sealed record LensCoverage(string Lens, string Outcome, string? Reason);
