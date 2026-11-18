using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Review;

/// <summary>
/// One row of the host's <c>Severity overrides</c> table in its review guidance file: a class of finding and the
/// severity the host grades it at. A row outranks the shipped lens guidance and the general severity definitions.
/// </summary>
/// <param name="Id">The id a finding cites (<c>override: SO-3</c>); <c>row-N</c> when the row gives none.</param>
/// <param name="FindingClass">The plain description a finding is matched against.</param>
/// <param name="Severity">The severity a matching finding is graded at.</param>
/// <param name="Lenses">The lenses the row is scoped to; empty for every lens.</param>
/// <param name="Technologies">The technologies the row is scoped to; empty for every technology.</param>
/// <param name="Why">The host's reason, when it gave one.</param>
/// <param name="Line">The one-based line of the row in the guidance file.</param>
public sealed record SeverityOverrideRow(
    string Id,
    string FindingClass,
    ReviewSeverity Severity,
    IReadOnlyList<string> Lenses,
    IReadOnlyList<string> Technologies,
    string? Why,
    int Line);

/// <summary>
/// The host's severity overrides, read from the <c>Severity overrides</c> section of the review guidance file the
/// registry declares as <c>&lt;review-guidance&gt;</c>, with the technology globs its technology map names.
/// </summary>
/// <remarks>
/// <para>
/// The section is a Markdown table: <c>| Id | Finding class | Severity | Applies to | Why |</c>. Columns are found by
/// their header, so their order is free; only <c>Finding class</c> and <c>Severity</c> are required. <c>Applies to</c>
/// is a comma list of lens names and technology names; empty means every seat. A row that names lenses and
/// technologies applies only where both hold: one of its lenses, and a changed file of one of its technologies.
/// </para>
/// <para>
/// Most repositories have no such section, so an absent file or section is zero rows. Rows inside an HTML comment or
/// a code fence are not rows, so a commented example stays an example. A row that cannot be read is left out here and
/// reported by <c>ISeverityOverrideSource.Validate</c> (<c>docs validate</c>), so a typo never fails a review.
/// </para>
/// <para>
/// Delivery fails open: a technology whose files the technology map does not name by glob (or a change whose files
/// git could not list) counts as present, because an override that silently never reaches a seat is the worse error.
/// </para>
/// </remarks>
public sealed class SeverityOverrides
{
    /// <summary>Severity-override application at verdict time: applied, corrected or an unknown id.</summary>
    public const string AppliedCode = "KW-REVIEW-057";

    /// <summary>A row whose severity is not critical, major, minor or nitpick.</summary>
    public const string UnknownSeverityCode = "KW-REVIEW-058";

    /// <summary>Two rows with one id.</summary>
    public const string DuplicateIdCode = "KW-REVIEW-059";

    /// <summary>A row scoped to a lens or technology that does not exist.</summary>
    public const string UnknownScopeCode = "KW-REVIEW-060";

    /// <summary>A row with no finding class, or a table missing a required column.</summary>
    public const string EmptyClassCode = "KW-REVIEW-061";

    /// <summary>Advisory: a row that looks like it lowers a class the shipped lens guidance grades critical.</summary>
    public const string LowersCriticalCode = "KW-REVIEW-062";

    /// <summary>The section heading the table sits under.</summary>
    public const string SectionTitle = "Severity overrides";

    /// <summary>The guidance file read when the registry declares none.</summary>
    public const string DefaultGuidancePath = "REVIEW.md";

    private readonly IReadOnlyList<SeverityOverrideRow> _rows;
    private readonly IReadOnlyList<Diagnostic> _problems;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _technologyGlobs;

    /// <summary>Creates the overrides read from a guidance file.</summary>
    /// <param name="rows">The rows that were read, in table order.</param>
    /// <param name="problems">What <c>docs validate</c> reports about the section.</param>
    /// <param name="technologyGlobs">The globs the technology map names for each technology.</param>
    public SeverityOverrides(
        IReadOnlyList<SeverityOverrideRow> rows,
        IReadOnlyList<Diagnostic> problems,
        IReadOnlyDictionary<string, IReadOnlyList<string>> technologyGlobs)
    {
        _rows = rows;
        _problems = problems;
        _technologyGlobs = technologyGlobs;
    }

    /// <summary>No overrides: the file or the section is absent.</summary>
    public static SeverityOverrides None { get; } = new([], [], new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The rows that were read, in table order.</summary>
    public IReadOnlyList<SeverityOverrideRow> Rows => _rows;

    /// <summary>What <c>docs validate</c> reports about the section; empty when it is valid or absent.</summary>
    public IReadOnlyList<Diagnostic> Problems => _problems;

    /// <summary>The globs the technology map names for each technology, keyed case-insensitively.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TechnologyGlobs => _technologyGlobs;

    /// <summary>Who matched an applied override: the seat that cited it.</summary>
    public const string SeatSource = "seat";

    /// <summary>Who matched an applied override: the decision model.</summary>
    public const string ModelSource = "decision model";
}
