using System.Text.Json.Serialization;

namespace KyberWeave.Core.Review;

/// <summary>How much damage a finding does if the change ships with it.</summary>
/// <remarks>
/// Severity is impact, never certainty. A finding the reporter is only half sure of but
/// which would lose data is <see cref="Critical"/> at a low confidence — both facts are
/// recorded, and <c>VerdictEngine</c> uses each for what it measures.
/// </remarks>
public enum ReviewSeverity
{
    /// <summary>A purely stylistic point with no effect on how the application operates; raised only at fix level <c>nitpick</c>.</summary>
    Nitpick,

    /// <summary>Real but bounded: a narrow edge case, a misleading comment or document statement, a missing test for a stated behaviour.</summary>
    Minor,

    /// <summary>Wrong, or failing under conditions a real user reaches.</summary>
    Major,

    /// <summary>Data loss, a security hole, corruption, or behaviour that breaks silently.</summary>
    Critical
}

/// <summary>What kind of finding this is, so the verdict can route it later.</summary>
/// <remarks>
/// Severity says how much damage the finding does; kind says what it disagrees with. A finding that meets every
/// request, plan decision and base behaviour yet still makes the code sturdier is <see cref="Hardening"/>, and one
/// that disagrees with an approved plan decision is a <see cref="PlanObjection"/> rather than a defect.
/// </remarks>
public enum FindingKind
{
    /// <summary>
    /// Breaks a stated acceptance criterion, a plan contract, or behaviour that works on the base branch; any
    /// security, data-loss or corruption risk is always a defect.
    /// </summary>
    [JsonStringEnumMemberName("defect")]
    Defect,

    /// <summary>
    /// True, but the request, the plan and base behaviour are all met; it makes the code sturdier beyond them.
    /// </summary>
    [JsonStringEnumMemberName("hardening")]
    Hardening,

    /// <summary>Disagrees with an approved plan decision.</summary>
    [JsonStringEnumMemberName("plan-objection")]
    PlanObjection
}

/// <summary>What the review concluded about the change.</summary>
public enum ReviewVerdict
{
    /// <summary>Nothing blocking survived adjudication.</summary>
    Approve,

    /// <summary>Something must change before this can proceed.</summary>
    RequestChanges
}

/// <summary>How risky the change is, derived from what the review found.</summary>
/// <remarks>
/// Deliberately not a function of diff size. A twelve-line migration that drops a column
/// outranks a three-thousand-line regeneration of a generated client, and grading by size
/// gets that backwards every time.
/// </remarks>
public enum RiskGrade
{
    /// <summary>Nothing of consequence found.</summary>
    Low,

    /// <summary>Real findings, or a change reaching beyond its own module.</summary>
    Medium,

    /// <summary>Critical findings or a failed blocking gate.</summary>
    High
}

/// <summary>One finding reported by a review lens.</summary>
/// <param name="Id">Lens-qualified identifier, e.g. <c>di-composition/new-in-constructor</c>.</param>
/// <param name="Lens">The lens that reported it.</param>
/// <param name="Severity">Impact if the change ships with it.</param>
/// <param name="Confidence">How certain the reporter is, from 1 to 10.</param>
/// <param name="File">Repository-relative path the finding is in.</param>
/// <param name="Line">One-based line the finding anchors to.</param>
/// <param name="Excerpt">Verbatim source the finding is about.</param>
/// <param name="Claim">One sentence stating what is wrong.</param>
/// <param name="Evidence">How the reporter knows.</param>
/// <param name="FailureScenario">Concrete conditions and the wrong behaviour that results.</param>
/// <param name="Suggestion">The specific change that fixes it.</param>
/// <param name="Override">
/// The host severity override (a <c>Severity overrides</c> row id in the review guidance) the finding cites or was
/// matched to; null when none.
/// </param>
/// <param name="OverrideSource">
/// Who matched <see cref="Override"/> once the verdict applied it: <c>seat</c> or <c>decision model</c>; null while
/// the override is only cited, or when none applied.
/// </param>
/// <param name="SeatSeverity">The severity the seat gave, when an applied override changed it; null otherwise.</param>
/// <param name="Kind">
/// What kind of finding this is — a defect, a hardening or a plan objection; null when the seat wrote no kind.
/// </param>
/// <param name="Basis">
/// What the finding breaks, quoted: the acceptance criterion, the plan line, or the base behaviour; null when the
/// seat quoted none.
/// </param>
public sealed record ReviewFinding(
    string Id,
    string Lens,
    ReviewSeverity Severity,
    int Confidence,
    string File,
    int Line,
    string? Excerpt = null,
    string? Claim = null,
    string? Evidence = null,
    string? FailureScenario = null,
    string? Suggestion = null,
    string? Override = null,
    string? OverrideSource = null,
    ReviewSeverity? SeatSeverity = null,
    FindingKind? Kind = null,
    string? Basis = null);

/// <summary>What one deterministic gate reported.</summary>
/// <param name="Id">The gate's declared identifier.</param>
/// <param name="Blocking">Whether failing it blocks the change.</param>
/// <param name="ExitCode">The runner's exit code.</param>
/// <param name="Summary">One line describing the outcome.</param>
/// <param name="DurationMilliseconds">How long the gate took.</param>
/// <param name="NotApplicableReason">Why the gate did not run, when it did not apply.</param>
/// <param name="Outcome">
/// How the gate ended: it ran to the end, it was stopped at its time limit, or its program never started.
/// </param>
/// <remarks>
/// A gate that did not apply is neither passed nor failed: <see cref="Passed"/> is false,
/// and the verdict engine excludes it from the failure count rather than treating an
/// unexecuted gate as evidence either way. The reason is optional so reports written
/// before it existed still read.
/// </remarks>
public sealed record GateResult(
    string Id,
    bool Blocking,
    int ExitCode,
    string Summary,
    long DurationMilliseconds = 0,
    string? NotApplicableReason = null,
    GateOutcome Outcome = GateOutcome.Completed)
{
    /// <summary>Whether the gate succeeded.</summary>
    public bool Passed => ExitCode == 0 && NotApplicableReason is null;

    /// <summary>Whether the gate was skipped as not applicable to the change.</summary>
    public bool NotApplicable => NotApplicableReason is not null;
}

/// <summary>How a deterministic gate ended, kept apart from whether it passed.</summary>
/// <remarks>
/// A missing program and a failing check both block a change, but they are fixed in different places,
/// so the outcome records which one happened.
/// </remarks>
public enum GateOutcome
{
    /// <summary>The program ran to the end, whether or not it passed.</summary>
    Completed,

    /// <summary>The program ran past its time limit and was stopped.</summary>
    TimedOut,

    /// <summary>The program could not be started, for example because it is not on the PATH.</summary>
    NotStarted,

    /// <summary>The council deadline stopped the gate, or the gate never started before the deadline.</summary>
    DeadlinePassed
}

/// <summary>Measured line coverage, as reported by the coverage gate.</summary>
/// <param name="FileLinePercent">Line coverage across files.</param>
/// <param name="ClassLinePercent">Line coverage across classes.</param>
public sealed record CoverageResult(double FileLinePercent, double ClassLinePercent);

/// <summary>One Cobertura report the gate suite wrote, and what it measured.</summary>
/// <param name="Path">Repository-relative path of the report, with forward slashes.</param>
/// <param name="Coverage">The line coverage the report records.</param>
public sealed record CoverageReport(string Path, CoverageResult Coverage);

/// <summary>A finding the engine removed, and the rule that removed it.</summary>
/// <param name="Finding">The finding as reported.</param>
/// <param name="Code">The <c>KW-REVIEW-*</c> rule that dropped it.</param>
/// <param name="Reason">Why it was dropped.</param>
public sealed record DroppedFinding(ReviewFinding Finding, string Code, string Reason);

/// <summary>The scope of the change under review.</summary>
/// <param name="ChangedPaths">Repository-relative paths the diff touches.</param>
/// <param name="ChangedLines">Total added and removed lines.</param>
/// <param name="PathLineCounts">Per-path added and removed lines, when the producer counted them.</param>
/// <remarks>
/// Scope is reporting metadata for reviewers. It is not an input to
/// <c>VerdictEngine.Evaluate</c>; the verdict is computed from findings, gates, config,
/// the evaluation date, and the optional coverage result only.
/// </remarks>
public sealed record ReviewScope(
    IReadOnlyList<string> ChangedPaths,
    int ChangedLines,
    IReadOnlyDictionary<string, int>? PathLineCounts = null);
