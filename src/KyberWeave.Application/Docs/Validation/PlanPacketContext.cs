using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>One row of a plan's markdown table, kept as written beside its cells.</summary>
/// <param name="Raw">The row line as authored.</param>
/// <param name="Cells">The row's cells, trimmed.</param>
internal sealed record PlanTableRow(string Raw, IReadOnlyList<string> Cells);

/// <summary>A plan table: its header and separator lines as written, and its rows.</summary>
/// <param name="Header">The header line as authored.</param>
/// <param name="Separator">The separator line as authored.</param>
/// <param name="Rows">The data rows.</param>
internal sealed record PlanTable(string Header, string Separator, IReadOnlyList<PlanTableRow> Rows);

/// <summary>A decision row with the table it came from, so it can be copied with its own header.</summary>
/// <param name="Id">The normalized id, such as <c>D5</c>.</param>
/// <param name="Table">The table's header and separator.</param>
/// <param name="Row">The row as authored.</param>
internal sealed record PlanDecision(string Id, PlanTable Table, PlanTableRow Row);

/// <summary>A heading section of a plan: its heading and everything under it up to the next heading of the same or a higher level.</summary>
/// <param name="Level">The heading level, 1 to 6.</param>
/// <param name="Heading">The heading text.</param>
/// <param name="Body">The section text below the heading line, subsections included.</param>
/// <param name="Line">The heading's zero-based line number.</param>
internal sealed record PlanSection(int Level, string Heading, string Body, int Line);

/// <summary>
/// What a task packet needs from the rest of its plan and a worker cannot open: the plan's Decisions table, its Test
/// contract table and its headed sections, read as written so they can be copied into a packet verbatim.
/// </summary>
internal sealed partial class PlanPacketContext
{
    private readonly Dictionary<string, PlanDecision> _decisions = new(StringComparer.Ordinal);
    private readonly List<(PlanTable Table, PlanTableRow Row, IReadOnlyList<string> TaskIds)> _contract = [];

    private PlanPacketContext(IReadOnlyList<PlanSection> sections)
    {
        Sections = sections;
    }

    /// <summary>Every headed section, in document order.</summary>
    public IReadOnlyList<PlanSection> Sections { get; }

    /// <summary>Reads the plan text.</summary>
    public static PlanPacketContext Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<(int Level, string Text, int Line)> headings = [];
        bool fenced = false;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            Match match = HeadingRegex().Match(line);
            if (match.Success)
            {
                headings.Add((match.Groups["hashes"].Length, match.Groups["text"].Value.Trim(), index));
            }
        }

        List<PlanSection> sections = [];
        for (int h = 0; h < headings.Count; h++)
        {
            (int level, string text, int line) = headings[h];
            int end = lines.Length;
            for (int next = h + 1; next < headings.Count; next++)
            {
                if (headings[next].Level <= level)
                {
                    end = headings[next].Line;
                    break;
                }
            }

            sections.Add(new PlanSection(level, text, string.Join('\n', lines[(line + 1)..end]).Trim('\n'), line));
        }

        PlanPacketContext context = new(sections);
        foreach (PlanSection section in sections)
        {
            string normalized = section.Heading.Replace('-', ' ');
            if (normalized.Contains("decision", StringComparison.OrdinalIgnoreCase))
            {
                context.ReadDecisions(section);
            }

            if (normalized.Contains("test contract", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("verification contract", StringComparison.OrdinalIgnoreCase))
            {
                context.ReadContract(section);
            }
        }

        return context;
    }

    /// <summary>Whether the plan's Decisions table has a row for <paramref name="id"/> (normalized, such as <c>D5</c>).</summary>
    public bool TryGetDecision(string id, out PlanDecision decision) => _decisions.TryGetValue(id, out decision!);

    /// <summary>The Test contract table and its rows that name <paramref name="taskId"/> in their first cell.</summary>
    public IReadOnlyList<(PlanTable Table, PlanTableRow Row)> ContractRowsFor(string taskId) =>
    [
        .. _contract.Where(entry => entry.TaskIds.Contains(taskId, StringComparer.Ordinal)).Select(entry => (entry.Table, entry.Row))
    ];

    /// <summary>The decision ids <paramref name="text"/> cites, normalized and in order: <c>D5</c>, <c>D-5</c>, <c>decision 5</c>, and ranges such as <c>D5-D7</c>.</summary>
    public static IReadOnlyList<string> CitedDecisionIds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> ids = [];
        foreach (Match match in DecisionMentionRegex().Matches(text))
        {
            int from = int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
            int to = match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture) : from;
            if (to < from || to - from > 20)
            {
                to = from;
            }

            for (int number = from; number <= to; number++)
            {
                Add(ids, "D" + number.ToString(CultureInfo.InvariantCulture));
            }
        }

        foreach (Match match in DecisionWordRegex().Matches(text))
        {
            foreach (Match number in DigitsRegex().Matches(match.Groups["list"].Value))
            {
                Add(ids, "D" + int.Parse(number.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            }
        }

        return ids;
    }

    /// <summary>The decision id a table's first cell names, normalized to <c>D</c> and its number, or null when it names none.</summary>
    public static string? NormalizeDecisionId(string cell)
    {
        string plain = Regex.Replace(cell, @"[\s*_`]", string.Empty);
        Match match = DecisionCellRegex().Match(plain);
        return match.Success
            ? "D" + int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Every table in <paramref name="body"/>; fenced examples do not count.</summary>
    internal static List<PlanTable> TablesIn(string body)
    {
        List<PlanTable> tables = [];
        List<string> run = [];
        bool fenced = false;
        foreach (string line in body.Split('\n').Append(string.Empty))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }

            if (!fenced && line.TrimStart().StartsWith('|'))
            {
                run.Add(line.TrimEnd());
                continue;
            }

            if (run.Count >= 2 && SeparatorRegex().IsMatch(run[1]))
            {
                tables.Add(new PlanTable(run[0], run[1], [.. run.Skip(2).Select(row => new PlanTableRow(row, SplitCells(row)))]));
            }

            run.Clear();
        }

        return tables;
    }

    private static List<string> SplitCells(string row)
    {
        string trimmed = row.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return [.. trimmed.Split('|').Select(cell => cell.Trim())];
    }

    private static void Add(List<string> ids, string id)
    {
        if (!ids.Contains(id, StringComparer.Ordinal))
        {
            ids.Add(id);
        }
    }

    private void ReadDecisions(PlanSection section)
    {
        foreach (PlanTable table in TablesIn(section.Body))
        {
            foreach (PlanTableRow row in table.Rows)
            {
                if (row.Cells.Count > 0 && NormalizeDecisionId(row.Cells[0]) is { } id)
                {
                    _decisions.TryAdd(id, new PlanDecision(id, table, row));
                }
            }
        }
    }

    private void ReadContract(PlanSection section)
    {
        foreach (PlanTable table in TablesIn(section.Body))
        {
            foreach (PlanTableRow row in table.Rows)
            {
                if (row.Cells.Count == 0)
                {
                    continue;
                }

                string[] ids = [.. Regex.Split(row.Cells[0].Replace("`", string.Empty, StringComparison.Ordinal).Replace("*", string.Empty, StringComparison.Ordinal), @"\s*(?:,|;|/|&|\band\b)\s*|\s+")
                    .Where(token => token.Length > 0)];
                _contract.Add((table, row, ids));
            }
        }
    }

    /// <summary>The table lines followed by nothing else: header, separator and rows, as authored.</summary>
    public static string Render(PlanTable table, IEnumerable<PlanTableRow> rows)
    {
        StringBuilder text = new();
        text.Append(table.Header).Append('\n').Append(table.Separator);
        foreach (PlanTableRow row in rows)
        {
            text.Append('\n').Append(row.Raw);
        }

        return text.ToString();
    }

    [GeneratedRegex(@"^(?<hashes>#{1,6})[ \t]+(?<text>.+?)[ \t#]*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])D-?(?<from>\d{1,3})(?:\s*[-–—]\s*D?-?(?<to>\d{1,3}))?(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex DecisionMentionRegex();

    [GeneratedRegex(@"\bdecisions?\s+#?(?<list>\d+(?:\s*(?:,|and|&)\s*#?\d+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecisionWordRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"^(?:decision)?D?-?(?<n>\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecisionCellRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)*\|?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRegex();
}
