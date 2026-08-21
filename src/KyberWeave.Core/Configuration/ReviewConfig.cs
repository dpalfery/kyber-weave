namespace KyberWeave.Core.Configuration;

/// <summary>One deterministic gate the host declares.</summary>
/// <param name="Id">Slug identifying the gate in findings and reports.</param>
/// <param name="Run">The command and its arguments, already split into argv.</param>
/// <param name="Blocking">Whether failing this gate blocks the change.</param>
/// <remarks>
/// <see cref="Run"/> is a list, never a command line, because
/// <see cref="Processes.ProcessRunner"/> refuses a concatenated argument string and refuses
/// a shell. A host declaring gates therefore cannot express a pipeline, a redirect, or a
/// substitution — and neither can anything that later edits this configuration. The gate
/// vocabulary is exactly "run this program with these arguments", which is the whole
/// injection surface closed at the point the value is written.
/// </remarks>
public sealed record ReviewGate(string Id, IReadOnlyList<string> Run, bool Blocking = true);

/// <summary>The unit-coverage floor a change must clear.</summary>
/// <param name="FileLinePercent">Minimum line coverage across files.</param>
/// <param name="ClassLinePercent">Minimum line coverage across classes.</param>
public sealed record ReviewCoverage(double FileLinePercent = 0, double ClassLinePercent = 0)
{
    /// <summary>Whether the host declared a floor at all.</summary>
    public bool IsDeclared => FileLinePercent > 0 || ClassLinePercent > 0;
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

    /// <summary>The unit-coverage floor.</summary>
    public ReviewCoverage Coverage { get; init; } = new();

    /// <summary>The rules the verdict engine may not override.</summary>
    public ReviewPolicy Policy { get; init; } = ReviewPolicy.ProductDefaults;

    /// <summary>Product defaults for an unconfigured host.</summary>
    public static ReviewConfig ProductDefaults { get; } = new();
}
