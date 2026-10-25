using System.Collections.ObjectModel;

namespace KyberWeave.Core.Configuration;

/// <summary>One deterministic gate the host declares.</summary>
/// <param name="Id">Slug identifying the gate in findings and reports.</param>
/// <param name="Run">The command and its arguments, already split into argv.</param>
/// <param name="Blocking">Whether failing this gate blocks the change.</param>
/// <param name="TimeoutSeconds">Seconds to wait before the runner kills the process tree.</param>
/// <remarks>
/// <see cref="Run"/> is a list, never a command line, because
/// <see cref="Processes.ProcessRunner"/> refuses a concatenated argument string and refuses
/// a shell. A host declaring gates therefore cannot express a pipeline, a redirect, or a
/// substitution — and neither can anything that later edits this configuration. The gate
/// vocabulary is exactly "run this program with these arguments", which is the whole
/// injection surface closed at the point the value is written.
/// </remarks>
public sealed record ReviewGate(string Id, IReadOnlyList<string> Run, bool Blocking = true, int TimeoutSeconds = 900);

/// <summary>
/// One gate a developer agent runs against its own working tree before a reviewer sees the change.
/// </summary>
/// <param name="Id">Slug identifying the gate within its agent.</param>
/// <param name="Include">Glob patterns selecting the changed files the gate applies to.</param>
/// <param name="Os">
/// <c>windows</c> or <c>unix</c> to run only on that platform; null to run everywhere.
/// </param>
/// <param name="Run">The verifying command, as argv. <c>{changed}</c> and <c>{task}</c> stay literal here.</param>
/// <param name="Fix">The command that applies the fix, as argv, with the same token rules as <see cref="Run"/>.</param>
/// <param name="Blocking">Whether a failing gate blocks the change.</param>
/// <param name="TimeoutSeconds">Seconds before the runner kills the process tree.</param>
/// <remarks>
/// Like <see cref="ReviewGate"/>, both commands are argv lists and never a command line. The
/// runner expands the <c>{changed}</c> and <c>{task}</c> tokens; this record only carries them,
/// so a token that is not a whole argument is refused at the point the operator wrote it.
/// </remarks>
public sealed record ReviewTaskGate(
    string Id,
    IReadOnlyList<string> Include,
    string? Os,
    IReadOnlyList<string> Run,
    IReadOnlyList<string> Fix,
    bool Blocking = true,
    int TimeoutSeconds = 900);

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

/// <summary>The rules the verdict engine may not decide for itself.</summary>
public sealed class ReviewPolicy
{
    /// <summary>Glob patterns whose paths always escalate to a human reviewer.</summary>
    public IReadOnlyList<string> AlwaysHuman { get; init; } = [];

    /// <summary>
    /// The largest diff, in changed lines, that is reviewed at all.
    /// </summary>
    /// <remarks>
    /// An attention limit, not a risk signal. A change past this ceiling is not made safe by
    /// reviewing it harder, so it escalates rather than being graded.
    /// </remarks>
    public int MaxReviewableLines { get; init; } = 10_000;

    /// <summary>How many surviving major findings force changes to be requested.</summary>
    public int MajorCountBlocks { get; init; } = 3;

    /// <summary>The confidence below which a finding is treated as noise.</summary>
    public int MinConfidence { get; init; } = 7;

    /// <summary>Active finding suppressions.</summary>
    public IReadOnlyList<ReviewSuppression> Suppressions { get; init; } = [];

    /// <summary>Product defaults for a host that declares no policy.</summary>
    public static ReviewPolicy ProductDefaults { get; } = new();
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

    /// <summary>The rules the verdict engine may not override.</summary>
    public ReviewPolicy Policy { get; init; } = ReviewPolicy.ProductDefaults;

    /// <summary>Product defaults for an unconfigured host.</summary>
    public static ReviewConfig ProductDefaults { get; } = new();
}
