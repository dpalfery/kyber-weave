using System.Collections.ObjectModel;

namespace KyberWeave.Core.Configuration;

/// <summary>The paths a gate applies to, when it declares any.</summary>
/// <param name="Paths">PathGlob patterns; the gate applies when a changed path matches one.</param>
/// <remarks>
/// A gate without this runs unconditionally. The patterns are evaluated through
/// <c>KW-ARB-GATE-001</c>, so the runner and the Arbiter answer "does this gate apply"
/// from the same rule rather than from two implementations that can drift apart.
/// </remarks>
public sealed record ReviewGateAppliesWhen(IReadOnlyList<string> Paths);

/// <summary>One deterministic gate the host declares.</summary>
/// <param name="Id">Slug identifying the gate in findings and reports.</param>
/// <param name="Run">The command and its arguments, already split into argv.</param>
/// <param name="Blocking">Whether failing this gate blocks the change.</param>
/// <param name="TimeoutSeconds">Seconds to wait before the runner kills the process tree.</param>
/// <param name="Os"><c>windows</c> or <c>unix</c> to run only on that platform; null to run everywhere.</param>
/// <param name="WorkingDirectory">
/// Repository-relative directory the gate runs in; null means the repository root.
/// </param>
/// <param name="AppliesWhen">The paths the gate applies to, or null when it always runs.</param>
/// <remarks>
/// <see cref="Run"/> is a list, never a command line, because
/// <see cref="Processes.ProcessRunner"/> refuses a concatenated argument string and refuses
/// a shell. A host declaring gates therefore cannot express a pipeline, a redirect, or a
/// substitution — and neither can anything that later edits this configuration. The gate
/// vocabulary is exactly "run this program with these arguments", which is the whole
/// injection surface closed at the point the value is written.
/// </remarks>
public sealed record ReviewGate(
    string Id,
    IReadOnlyList<string> Run,
    bool Blocking = true,
    int TimeoutSeconds = 900,
    string? Os = null,
    string? WorkingDirectory = null,
    ReviewGateAppliesWhen? AppliesWhen = null)
{
    /// <summary>Glob patterns selecting the changed files the gate applies to; empty for every change.</summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>
    /// Whether the gate also fails when its command exits zero but writes anything other than whitespace to
    /// standard output. For tools that report a problem by listing it and still exit zero, such as <c>gofmt -l</c>.
    /// </summary>
    public bool FailOnOutput { get; init; }

    /// <summary>
    /// Whether this gate runs the full product suite. Only the review council and CI may run it; see <see cref="FullSuiteGate"/>.
    /// </summary>
    public bool FullSuite { get; init; }

    /// <summary>
    /// Repository-relative paths the gate needs on disk before it can start, such as <c>dash/node_modules</c>.
    /// </summary>
    /// <remarks>
    /// A gate that declares a prerequisite and finds it absent does not start, so the missing install is reported
    /// as the missing dependency it is rather than through whatever module-not-found output the tool would print.
    /// The paths are deliberately not required to exist when the configuration loads: their absence is a fact
    /// about this machine, not about the configuration.
    /// </remarks>
    public IReadOnlyList<string> RequiresPaths { get; init; } = [];

    /// <summary>
    /// Whether the gate runs alongside the gates declared after it instead of holding them up.
    /// </summary>
    /// <remarks>
    /// The gate still starts at its place in the declared order, so everything declared before it has finished,
    /// and its result still sits at that place in the report. Only a gate that neither reads nor writes what the
    /// gates after it produce may set this: the runner does not check that.
    /// </remarks>
    public bool Concurrent { get; init; }
}

/// <summary>
/// One gate a developer agent runs against its own working tree before a reviewer sees the change.
/// </summary>
/// <param name="Id">Slug identifying the gate within its agent.</param>
/// <param name="Include">Glob patterns selecting the changed files the gate applies to.</param>
/// <param name="Os">
/// <c>windows</c> or <c>unix</c> to run only on that platform; null to run everywhere.
/// </param>
/// <param name="Run">
/// The verifying command, as argv. <c>{changed}</c>, <c>{task}</c> and <c>{tests}</c> stay literal here.
/// </param>
/// <param name="Fix">The command that applies the fix, as argv, with the same token rules as <see cref="Run"/>.</param>
/// <param name="Blocking">Whether a failing gate blocks the change.</param>
/// <param name="TimeoutSeconds">Seconds before the runner kills the process tree.</param>
/// <param name="WorkingDirectory">
/// Repository-relative directory the gate runs in; null means the repository root.
/// </param>
/// <remarks>
/// Like <see cref="ReviewGate"/>, both commands are argv lists and never a command line. The
/// runner expands the <c>{changed}</c>, <c>{task}</c> and <c>{tests}</c> tokens; this record only
/// carries them, so a token that would mean nothing where the operator put it is refused at the point
/// they wrote it.
/// </remarks>
public sealed record ReviewTaskGate(
    string Id,
    IReadOnlyList<string> Include,
    string? Os,
    IReadOnlyList<string> Run,
    IReadOnlyList<string> Fix,
    bool Blocking = true,
    int TimeoutSeconds = 900,
    string? WorkingDirectory = null)
{
    /// <summary>
    /// Whether the gate also fails when its command exits zero but writes anything other than whitespace to
    /// standard output. For tools that report a problem by listing it and still exit zero, such as <c>gofmt -l</c>.
    /// </summary>
    public bool FailOnOutput { get; init; }
}

/// <summary>The unit-coverage floor a change must clear.</summary>
/// <param name="FileLinePercent">Minimum line coverage across files.</param>
/// <param name="ClassLinePercent">Minimum line coverage across classes.</param>
public sealed record ReviewCoverage(double FileLinePercent = 0, double ClassLinePercent = 0)
{
    /// <summary>Whether the host declared a floor at all.</summary>
    public bool IsDeclared => FileLinePercent > 0 || ClassLinePercent > 0;
}

/// <summary>How much of a body has to match before a duplicate cluster is worth reporting.</summary>
/// <param name="MinimumLines">
/// Normalized lines a body must reach to be compared at all. Below this the matches are
/// guard clauses, one-line delegations, and property bodies — identical everywhere, and
/// evidence of nothing.
/// </param>
public sealed record ReviewDuplicates(int MinimumLines = 4)
{
    /// <summary>The threshold applied when a host declares none.</summary>
    public static ReviewDuplicates Default { get; } = new();
}

/// <summary>A finding the host has decided not to be told about again, for now.</summary>
/// <param name="Id">The finding identifier being suppressed.</param>
/// <param name="Reason">Why, stated by whoever suppressed it.</param>
/// <param name="Expires">The day the suppression stops applying.</param>
/// <remarks>
/// <see cref="Expires"/> is required and has no "never" value. A permanent suppression is
/// how a review system quietly stops reviewing: the reason ages out of everyone's memory
/// while the exemption stays. Forcing re-justification on a date is the cheapest available
/// defence against that.
/// </remarks>
public sealed record ReviewSuppression(string Id, string Reason, DateOnly Expires);

/// <summary>How hard the council and the verdict engine lean on a change.</summary>
/// <remarks>
/// A named preset over two thresholds, not a third mechanism: <see cref="ReviewPolicy.MinConfidence"/>
/// and <see cref="ReviewPolicy.MajorCountBlocks"/> remain the only numbers the engine reads, and a
/// host that declares either explicitly overrides the preset. Minor findings never block
/// at any level; strictness changes how many findings are heard and how few majors it takes to
/// request changes.
/// </remarks>
public enum ReviewStrictness
{
    /// <summary>Only high-confidence findings count, and several majors are needed to block.</summary>
    Lenient,

    /// <summary>The product default.</summary>
    Balanced,

    /// <summary>Lower-confidence findings count, and a single surviving major blocks.</summary>
    Strict
}

/// <summary>The rules the verdict engine may not decide for itself.</summary>
public sealed class ReviewPolicy
{
    /// <summary>The preset these thresholds came from.</summary>
    public ReviewStrictness Strictness { get; init; } = ReviewStrictness.Balanced;

    /// <summary>How many surviving major findings force changes to be requested.</summary>
    public int MajorCountBlocks { get; init; } = 3;

    /// <summary>The confidence below which a finding is treated as noise.</summary>
    public int MinConfidence { get; init; } = 7;

    /// <summary>Active finding suppressions.</summary>
    public IReadOnlyList<ReviewSuppression> Suppressions { get; init; } = [];

    /// <summary>Product defaults for a host that declares no policy.</summary>
    public static ReviewPolicy ProductDefaults { get; } = new();

    /// <summary>The thresholds a strictness preset stands for.</summary>
    /// <param name="strictness">The preset.</param>
    public static ReviewPolicy ForStrictness(ReviewStrictness strictness) => strictness switch
    {
        ReviewStrictness.Lenient => new ReviewPolicy { Strictness = strictness, MinConfidence = 8, MajorCountBlocks = 5 },
        ReviewStrictness.Balanced => new ReviewPolicy { Strictness = strictness, MinConfidence = 7, MajorCountBlocks = 3 },
        ReviewStrictness.Strict => new ReviewPolicy { Strictness = strictness, MinConfidence = 5, MajorCountBlocks = 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(strictness), strictness, "Unknown strictness.")
    };
}

/// <summary>The <c>review:</c> host configuration.</summary>
public sealed class ReviewConfig
{
    /// <summary>The deterministic gates, in declaration order.</summary>
    public IReadOnlyList<ReviewGate> Gates { get; init; } = [];

    /// <summary>
    /// The gates each developer agent runs on its own change, keyed by agent slug, each list in
    /// declaration order. Empty when the host declares none.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> TaskGates { get; init; } =
        new ReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>>(
            new Dictionary<string, IReadOnlyList<ReviewTaskGate>>(StringComparer.Ordinal));

    /// <summary>
    /// The host's mapping from a source glob to the fully qualified test classes that cover it, which the
    /// <c>{tests}</c> task-gate token expands through. Empty when the host declares none.
    /// </summary>
    /// <remarks>
    /// A declared entry is read before the file-name convention, because an operator who maps a source area
    /// knows which classes exercise it and a file name does not.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TestMap { get; init; } =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    /// <summary>The unit-coverage floor.</summary>
    public ReviewCoverage Coverage { get; init; } = new();

    /// <summary>The duplicate-detection threshold.</summary>
    public ReviewDuplicates Duplicates { get; init; } = ReviewDuplicates.Default;

    /// <summary>Maximum concurrent workers the ready-queue dispatcher may run.</summary>
    /// <remarks>
    /// Bounds MSBuild node and Roslyn server contention across parallel workers. The default
    /// of four keeps parallelism while giving each worker its own <c>obj/</c> tree through
    /// per-task artifacts paths; a host that names another value gets that value instead.
    /// </remarks>
    public int MaxConcurrentWorkers { get; init; } = 4;

    /// <summary>Seconds the review council may run its gates before the deadline stops them.</summary>
    /// <remarks>
    /// Read from <c>review.council-deadline-seconds</c>. It bounds only a <c>review gates --deadline</c> run, which
    /// the council passes: a gate the deadline cuts, and every gate after it, is reported as not run
    /// (<c>KW-REVIEW-039</c>) rather than failed, so the council returns a verdict on the evidence it has instead of
    /// waiting on a slow suite. Without <c>--deadline</c> no run deadline applies.
    /// </remarks>
    public int CouncilDeadlineSeconds { get; init; } = 600;

    /// <summary>The rules the verdict engine may not override.</summary>
    public ReviewPolicy Policy { get; init; } = ReviewPolicy.ProductDefaults;

    /// <summary>
    /// Non-fatal problems in the <c>review:</c> section, such as a key the loader does not know.
    /// The config still loads; the CLI reports each entry as a warning.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Product defaults for an unconfigured host.</summary>
    public static ReviewConfig ProductDefaults { get; } = new();
}
