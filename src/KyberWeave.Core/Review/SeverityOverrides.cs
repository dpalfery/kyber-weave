using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
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
/// reported by <see cref="Validate"/> (<c>docs validate</c>), so a typo never fails a review.
/// </para>
/// <para>
/// Delivery fails open: a technology whose files the technology map does not name by glob (or a change whose files
/// git could not list) counts as present, because an override that silently never reaches a seat is the worse error.
/// </para>
/// </remarks>
public sealed partial class SeverityOverrides
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

    private const string DefaultGuidancePath = "REVIEW.md";
    private const string Subject = "review-guidance";

    /// <summary>The technologies the product ships a review reference for; a host technology map can add more.</summary>
    private static readonly string[] ShippedTechnologies =
    [
        "azure", "csharp", "github-actions", "go", "pulumi", "python", "react", "rust", "sql", "tauri", "typescript",
        "shell", "agent-prompts",
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "against", "being", "every", "finding", "findings", "from", "other", "their", "there",
        "these", "which", "while", "with", "without", "where", "change", "changes", "changed",
    };

    private readonly List<SeverityOverrideRow> _rows;
    private readonly List<Diagnostic> _problems;
    private readonly Dictionary<string, List<string>> _technologyGlobs;

    private SeverityOverrides(
        List<SeverityOverrideRow> rows, List<Diagnostic> problems, Dictionary<string, List<string>> technologyGlobs)
    {
        _rows = rows;
        _problems = problems;
        _technologyGlobs = technologyGlobs;
    }

    /// <summary>No overrides: the file or the section is absent.</summary>
    public static SeverityOverrides None { get; } = new([], [], new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The rows that were read, in table order.</summary>
    public IReadOnlyList<SeverityOverrideRow> Rows => _rows;

    /// <summary>What <c>docs validate</c> reports about the section; empty when it is valid or absent.</summary>
    public IReadOnlyList<Diagnostic> Problems => _problems;

    /// <summary>The review guidance file the registry declares (default <c>REVIEW.md</c>), or null when it is missing.</summary>
    public static string? GuidancePath(string root, KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(config);
        string declared = config.ConfigReg.Additions
            .FirstOrDefault(entry => string.Equals(entry.Name, "review-guidance", StringComparison.Ordinal))?.Path
            ?? DefaultGuidancePath;
        string path = Path.GetFullPath(Path.Combine(root, declared));
        return File.Exists(path) ? path : null;
    }

    /// <summary>The overrides of the declared guidance file; <see cref="None"/> when it is missing or unreadable.</summary>
    public static SeverityOverrides Load(string root, KyberWeaveConfig config)
    {
        string? path = GuidancePath(root, config);
        if (path is null)
            return None;

        try
        {
            return Parse(File.ReadAllText(path), Path.GetRelativePath(root, path).Replace('\\', '/'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return None;
        }
    }

    /// <summary>Reads the <c>Severity overrides</c> section and the technology map from a guidance file's text.</summary>
    /// <param name="text">The guidance file's contents.</param>
    /// <param name="file">The file's repository-relative path, named in the problems.</param>
    public static SeverityOverrides Parse(string text, string file = DefaultGuidancePath)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = Visible(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        Dictionary<string, List<string>> globs = TechnologyGlobs(lines);
        List<SeverityOverrideRow> rows = [];
        List<Diagnostic> problems = [];

        (int start, int end) = Section(lines);
        if (start < 0)
            return new SeverityOverrides(rows, problems, globs);

        HashSet<string> technologies = new(ShippedTechnologies.Concat(globs.Keys), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int>? columns = null;
        int dataRows = 0;
        for (int index = start; index < end; index++)
        {
            string line = lines[index].Trim();
            if (!line.StartsWith('|'))
            {
                columns = line.Length == 0 ? columns : null;
                continue;
            }

            string[] cells = Cells(line);
            if (cells.All(cell => cell.Length == 0 || cell.Trim('-', ':', ' ').Length == 0))
                continue;

            int number = index + 1;
            if (columns is null)
            {
                columns = Columns(cells);
                if (!columns.ContainsKey("class") || !columns.ContainsKey("severity"))
                {
                    problems.Add(Problem(EmptyClassCode, Severity.Error, file, number,
                        $"The {SectionTitle} table has no 'Finding class' or no 'Severity' column, so none of its rows is read.",
                        "Give the table the header | Id | Finding class | Severity | Applies to | Why |."));
                    columns = new Dictionary<string, int>(StringComparer.Ordinal) { ["invalid"] = 0 };
                }

                continue;
            }

            if (columns.ContainsKey("invalid"))
                continue;

            dataRows++;
            string id = Cell(cells, columns, "id") is { Length: > 0 } given ? given : $"row-{dataRows}";
            string findingClass = Cell(cells, columns, "class");
            string severityText = Cell(cells, columns, "severity");
            bool valid = true;

            if (findingClass.Length == 0)
            {
                valid = false;
                problems.Add(Problem(EmptyClassCode, Severity.Error, file, number,
                    $"Severity override '{id}' has no finding class, so no finding can match it.",
                    "Describe the class of finding in a few plain words, such as 'public API change without a documentation update'."));
            }

            if (!TrySeverity(severityText, out ReviewSeverity severity))
            {
                valid = false;
                problems.Add(Problem(UnknownSeverityCode, Severity.Error, file, number,
                    $"Severity override '{id}' has severity '{severityText}'; use critical, major, minor or nitpick.",
                    "Suppress a finding through review.policy.suppressions instead of a severity."));
            }

            if (seen.TryGetValue(id, out int first))
            {
                valid = false;
                problems.Add(Problem(DuplicateIdCode, Severity.Error, file, number,
                    $"Severity override id '{id}' is used again (first on line {first}); a finding cites one row by its id.",
                    "Give each row its own id."));
            }
            else
            {
                seen[id] = number;
            }

            List<string> lenses = [];
            List<string> scopedTechnologies = [];
            foreach (string token in Scope(Cell(cells, columns, "scope")))
            {
                if (ReviewLensCatalogue.Names.Contains(token, StringComparer.Ordinal))
                    lenses.Add(token);
                else if (technologies.Contains(token))
                    scopedTechnologies.Add(token);
                else
                {
                    valid = false;
                    problems.Add(Problem(UnknownScopeCode, Severity.Error, file, number,
                        $"Severity override '{id}' applies to '{token}', which is neither a review lens nor a technology.",
                        $"Name lenses ({string.Join(", ", ReviewLensCatalogue.Names)}) or technologies " +
                        $"({string.Join(", ", technologies.Order(StringComparer.Ordinal))}), or leave the cell empty for every seat."));
                }
            }

            if (!valid)
                continue;

            SeverityOverrideRow row = new(
                id, findingClass, severity, lenses, scopedTechnologies, Cell(cells, columns, "why") is { Length: > 0 } why ? why : null, number);
            rows.Add(row);
            if (LowersShippedCritical(row) is { } lens)
            {
                problems.Add(Problem(LowersCriticalCode, Severity.Warning, file, number,
                    $"Severity override '{id}' grades '{findingClass}' {SeverityName(severity)}; the shipped {lens} lens guidance " +
                    "appears to grade that class critical. Allowed: the host owns the decision.",
                    "Keep the row if lowering it is deliberate; this is an advisory, matched on the wording of the lens file."));
            }
        }

        return new SeverityOverrides(rows, problems, globs);
    }

    /// <summary>The problems in the declared guidance file's section, for <c>docs validate</c>; empty without a file.</summary>
    public static DiagnosticReport Validate(string root, KyberWeaveConfig config)
    {
        DiagnosticReport report = new();
        report.AddRange(Load(root, config).Problems);
        return report;
    }

    /// <summary>The row with this id, or null.</summary>
    public SeverityOverrideRow? Find(string id) =>
        _rows.Find(row => string.Equals(row.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The rows a seat of <paramref name="lens"/> is given: rows with no scope, rows scoped to the lens, and rows scoped
    /// to a technology a changed file belongs to. A null changed list counts every technology as present.
    /// </summary>
    public IReadOnlyList<SeverityOverrideRow> RowsFor(string lens, IReadOnlyList<string>? changedPaths) =>
        [.. _rows.Where(row => LensMatches(row, lens)
                               && (row.Technologies.Count == 0 || changedPaths is null
                                   || row.Technologies.Any(technology => changedPaths.Any(path => BelongsTo(technology, path)))))];

    /// <summary>The rows a finding of <paramref name="lens"/> in <paramref name="file"/> can be matched against.</summary>
    public IReadOnlyList<SeverityOverrideRow> RowsForFinding(string lens, string file) =>
        [.. _rows.Where(row => LensMatches(row, lens)
                               && (row.Technologies.Count == 0 || row.Technologies.Any(technology => BelongsTo(technology, file))))];

    /// <summary>Who matched an applied override: the seat that cited it.</summary>
    public const string SeatSource = "seat";

    /// <summary>Who matched an applied override: the decision model.</summary>
    public const string ModelSource = "decision model";

    /// <summary>
    /// Applies each override a seat cited and the verdict has not applied yet: the finding takes the row's severity and
    /// records the row and the seat's own severity. A cited id no row has is reported and the seat's severity stands.
    /// </summary>
    public IReadOnlyList<ReviewFinding> ApplyCited(IReadOnlyList<ReviewFinding> findings, ICollection<Diagnostic> notes)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(notes);
        List<ReviewFinding> applied = new(findings.Count);
        foreach (ReviewFinding finding in findings)
        {
            if (finding.Override is not { } cited || finding.OverrideSource is not null)
            {
                applied.Add(finding);
                continue;
            }

            if (Find(cited) is not { } row)
            {
                notes.Add(new Diagnostic(
                    AppliedCode,
                    Severity.Warning,
                    $"'{finding.Id}' cites severity override '{cited}', which the review guidance does not define; the seat's severity " +
                    $"'{SeverityName(finding.Severity)}' stands.",
                    finding.Lens,
                    finding.File,
                    "Cite an id from the Severity overrides section, or none.",
                    finding.Line));
                applied.Add(finding with { Override = null });
                continue;
            }

            ReviewFinding result = Apply(finding, row, SeatSource);
            notes.Add(Note(result, row, null));
            applied.Add(result);
        }

        return applied;
    }

    /// <summary>The finding graded at <paramref name="row"/>'s severity, recording the row, who matched it and the seat's severity.</summary>
    public static ReviewFinding Apply(ReviewFinding finding, SeverityOverrideRow row, string source)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(row);
        return finding with
        {
            Severity = row.Severity,
            Override = row.Id,
            OverrideSource = source,
            SeatSeverity = finding.Severity == row.Severity ? null : finding.Severity,
        };
    }

    /// <summary>The report line for an applied override: <c>severity set by host override SO-3</c>, and how.</summary>
    public static Diagnostic Note(ReviewFinding applied, SeverityOverrideRow row, string? matched)
    {
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(row);
        string change = applied.SeatSeverity is { } seat
            ? $"{SeverityName(seat)} → {SeverityName(row.Severity)}"
            : $"{SeverityName(row.Severity)}, as the seat graded it";
        string how = matched ?? (applied.SeatSeverity is null ? "cited by the seat" : "cited by the seat, which graded it differently");
        return new Diagnostic(
            AppliedCode,
            Severity.Info,
            $"'{applied.Id}': severity set by host override {row.Id} ({change}; {how}).",
            applied.Lens,
            applied.File,
            StartLine: applied.Line);
    }

    /// <summary>A severity as REVIEW.md and seat files write it.</summary>
    public static string SeverityName(ReviewSeverity severity) => ReviewVerdictDecisions.SeverityName(severity);

    private static bool LensMatches(SeverityOverrideRow row, string lens) =>
        row.Lenses.Count == 0 || row.Lenses.Contains(lens, StringComparer.Ordinal);

    /// <summary>Whether a path belongs to a technology; a technology the map names no glob for counts as present.</summary>
    private bool BelongsTo(string technology, string path) =>
        !_technologyGlobs.TryGetValue(technology, out List<string>? globs) || globs.Count == 0
        || globs.Any(glob => PathGlob.IsMatch(glob, path));

    private static bool TrySeverity(string text, out ReviewSeverity severity)
    {
        severity = ReviewSeverity.Minor;
        string? name = Enum.GetNames<ReviewSeverity>().FirstOrDefault(candidate =>
            string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out severity);
    }

    /// <summary>
    /// The lens whose embedded charter grades, in a sentence that names <c>critical</c>, the class the row describes,
    /// when the row grades it lower. A word-overlap heuristic over the charter's wording, so only an advisory.
    /// </summary>
    private static string? LowersShippedCritical(SeverityOverrideRow row)
    {
        if (row.Severity == ReviewSeverity.Critical)
            return null;

        string[] words = SignificantWords(row.FindingClass);
        if (words.Length < 2)
            return null;

        foreach (string lens in row.Lenses.Count > 0 ? row.Lenses : ReviewLensCatalogue.Names)
        {
            string? charter = ArbiterSquadCatalog.EmbeddedLensText(lens);
            if (charter is null)
                continue;

            foreach (string sentence in SentenceSplit().Split(charter).Where(sentence =>
                         sentence.Contains("`critical`", StringComparison.OrdinalIgnoreCase)))
            {
                int shared = words.Count(word => sentence.Contains(word, StringComparison.OrdinalIgnoreCase));
                if (shared * 10 >= words.Length * 6)
                    return lens;
            }
        }

        return null;
    }

    private static string[] SignificantWords(string text) =>
        [.. WordPattern().Matches(text).Select(match => match.Value)
            .Where(word => word.Length >= 5 && !StopWords.Contains(word)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The text with HTML comments and fenced code blocks blanked, line numbers kept.</summary>
    private static string[] Visible(string[] lines)
    {
        string[] visible = new string[lines.Length];
        bool comment = false;
        string? fence = null;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.TrimStart();
            if (!comment && fence is null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                fence = trimmed[..3];
                visible[index] = string.Empty;
                continue;
            }

            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                    fence = null;
                visible[index] = string.Empty;
                continue;
            }

            System.Text.StringBuilder kept = new();
            int at = 0;
            while (at < line.Length)
            {
                if (comment)
                {
                    int close = line.IndexOf("-->", at, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        at = line.Length;
                        break;
                    }

                    comment = false;
                    at = close + 3;
                    continue;
                }

                int open = line.IndexOf("<!--", at, StringComparison.Ordinal);
                if (open < 0)
                {
                    kept.Append(line, at, line.Length - at);
                    break;
                }

                kept.Append(line, at, open - at);
                comment = true;
                at = open + 4;
            }

            visible[index] = kept.ToString();
        }

        return visible;
    }

    /// <summary>The line range of the section's body: after its heading, up to the next heading of its level or higher.</summary>
    private static (int Start, int End) Section(string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            Match heading = Heading().Match(lines[index]);
            if (!heading.Success
                || !string.Equals(heading.Groups["title"].Value.Trim(), SectionTitle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int level = heading.Groups["hashes"].Length;
            int end = index + 1;
            while (end < lines.Length
                   && !(Heading().Match(lines[end]) is { Success: true } next && next.Groups["hashes"].Length <= level))
            {
                end++;
            }

            return (index + 1, end);
        }

        return (-1, -1);
    }

    /// <summary>
    /// Each technology the guidance file's technology map names, with the globs its <c>Files</c> cell gives in
    /// backticks. A bare <c>*.cs</c> means a file of that name anywhere, so it gains a leading <c>**/</c>.
    /// </summary>
    private static Dictionary<string, List<string>> TechnologyGlobs(string[] lines)
    {
        Dictionary<string, List<string>> map = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int>? columns = null;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (!line.StartsWith('|'))
            {
                columns = null;
                continue;
            }

            string[] cells = Cells(line);
            if (cells.All(cell => cell.Trim('-', ':', ' ').Length == 0))
                continue;

            if (columns is null)
            {
                columns = cells.Select((cell, position) => (cell, position))
                    .Where(pair => pair.cell.Equals("Technology", StringComparison.OrdinalIgnoreCase)
                                   || pair.cell.Equals("Files", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        pair => pair.cell.Equals("Technology", StringComparison.OrdinalIgnoreCase) ? "technology" : "files",
                        pair => pair.position,
                        StringComparer.Ordinal);
                if (columns.Count < 2)
                    columns = new Dictionary<string, int>(StringComparer.Ordinal) { ["none"] = 0 };
                continue;
            }

            if (!columns.TryGetValue("technology", out int technologyColumn) || !columns.TryGetValue("files", out int filesColumn)
                || technologyColumn >= cells.Length || filesColumn >= cells.Length)
            {
                continue;
            }

            string technology = cells[technologyColumn].Trim('`', ' ');
            if (technology.Length == 0)
                continue;

            map[technology] = [.. BacktickPattern().Matches(cells[filesColumn])
                .Select(match => match.Groups["glob"].Value.Trim())
                .Where(glob => glob.Contains('*', StringComparison.Ordinal) || glob.Contains('.', StringComparison.Ordinal) || glob.Contains('/', StringComparison.Ordinal))
                .Select(glob => glob.Contains('/', StringComparison.Ordinal) ? glob : "**/" + glob)];
        }

        return map;
    }

    private static Dictionary<string, int> Columns(string[] header)
    {
        Dictionary<string, int> columns = new(StringComparer.Ordinal);
        for (int position = 0; position < header.Length; position++)
        {
            string name = header[position].Trim('*', '`', ' ').ToUpperInvariant();
            string? key = name switch
            {
                "ID" => "id",
                "FINDING CLASS" or "CLASS" => "class",
                "SEVERITY" => "severity",
                "APPLIES TO" or "SCOPE" => "scope",
                "WHY" or "REASON" => "why",
                _ => null,
            };
            if (key is not null)
                columns.TryAdd(key, position);
        }

        return columns;
    }

    private static string Cell(string[] cells, Dictionary<string, int> columns, string key) =>
        columns.TryGetValue(key, out int position) && position < cells.Length ? cells[position].Trim('`', ' ') : string.Empty;

    private static IEnumerable<string> Scope(string cell) =>
        cell.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim('`', ' '))
            .Where(token => token.Length > 0 && token is not ("-" or "—" or "any" or "all"));

    /// <summary>The cells of one table row; an escaped <c>\|</c> stays inside its cell.</summary>
    private static string[] Cells(string line)
    {
        string body = line.Trim();
        if (body.StartsWith('|'))
            body = body[1..];
        if (body.EndsWith('|') && !body.EndsWith("\\|", StringComparison.Ordinal))
            body = body[..^1];

        return [.. CellSplit().Split(body).Select(cell => cell.Replace("\\|", "|", StringComparison.Ordinal).Trim())];
    }

    private static Diagnostic Problem(string code, Severity severity, string file, int line, string message, string hint) =>
        new(code, severity, message, Subject, file, hint, line);

    [GeneratedRegex(@"^(?<hashes>#{1,6})[ \t]+(?<title>.+?)[ \t#]*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex Heading();

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex CellSplit();

    [GeneratedRegex(@"`(?<glob>[^`]+)`", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex BacktickPattern();

    [GeneratedRegex(@"[A-Za-z][A-Za-z-]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceSplit();
}
