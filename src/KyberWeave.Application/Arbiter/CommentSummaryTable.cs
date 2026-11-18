using System.Text.RegularExpressions;

namespace KyberWeave.Core.Arbiter;

/// <summary>One data row of a bot summary's findings table: the path and line it cites, and the issue it states.</summary>
/// <param name="Path">The repository-relative path, with backticks and any <c>:line</c> or <c>#L</c> suffix removed.</param>
/// <param name="Line">The 1-based start line of the cited range.</param>
/// <param name="Issue">The issue text the row states.</param>
internal sealed record CommentSummaryRow(string Path, int Line, string Issue);

/// <summary>
/// Reads the findings table a bot writes into a PR-level summary comment.
/// </summary>
/// <remarks>
/// Fail-safe: a comment with no table, or with any malformed data row, yields no rows. The caller then keeps the
/// whole comment real, because a table that cannot be read in full cannot be trusted row by row. The header is
/// matched by name, case-insensitively, so a bot that reorders columns still reads; <c>File</c> and <c>Issue</c>
/// are required, and each row's line comes from the <c>Line</c> column or the file cell.
/// </remarks>
internal static partial class CommentSummaryTable
{
    private const char Pipe = '|';
    private const char Backtick = '`';

    /// <summary>Reads every data row of the first findings table in <paramref name="body"/>, or null when none reads whole.</summary>
    public static IReadOnlyList<CommentSummaryRow>? Parse(string body)
    {
        string[] lines = body.Split('\n');
        for (int index = 0; index + 1 < lines.Length; index++)
        {
            string[]? header = CellsOf(lines[index]);
            if (header is null || !IsSeparator(lines[index + 1]))
            {
                continue;
            }

            int file = IndexOfHeader(header, "file");
            int issue = IndexOfHeader(header, "issue");
            int line = IndexOfHeader(header, "line");
            if (file < 0 || issue < 0)
            {
                return null;
            }

            List<CommentSummaryRow> rows = [];
            for (int row = index + 2; row < lines.Length; row++)
            {
                string[]? cells = CellsOf(lines[row]);
                if (cells is null)
                {
                    break;
                }

                CommentSummaryRow? parsed = RowOf(cells, file, issue, line);
                if (parsed is null)
                {
                    return null;
                }

                rows.Add(parsed);
            }

            return rows;
        }

        return null;
    }

    private static CommentSummaryRow? RowOf(string[] cells, int file, int issue, int line)
    {
        if (Math.Max(file, Math.Max(issue, line)) >= cells.Length)
        {
            return null;
        }

        string fileCell = Unquote(cells[file]);
        Match location = PathWithLine().Match(fileCell);
        string path = location.Success ? location.Groups["path"].Value : fileCell;
        int? number = line >= 0 ? LineNumber(cells[line]) : LineFromSuffix(location);
        string text = Unquote(cells[issue]);
        if (path.Length == 0 || text.Length == 0 || number is not int start || start < 1)
        {
            return null;
        }

        return new CommentSummaryRow(path, start, text);
    }

    private static int? LineFromSuffix(Match location) =>
        location.Success && location.Groups["line"].Success && int.TryParse(location.Groups["line"].Value, out int value)
            ? value
            : null;

    private static int? LineNumber(string cell)
    {
        Match match = LineCell().Match(Unquote(cell));
        return match.Success && int.TryParse(match.Groups["start"].Value, out int value) ? value : null;
    }

    private static int IndexOfHeader(string[] header, string name)
    {
        for (int index = 0; index < header.Length; index++)
        {
            if (string.Equals(header[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSeparator(string line)
    {
        string[]? cells = CellsOf(line);
        return cells is { Length: > 0 } && cells.All(cell => SeparatorCell().IsMatch(cell));
    }

    /// <summary>The trimmed cells of a table row, or null when the line is not a table row.</summary>
    private static string[]? CellsOf(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != Pipe)
        {
            return null;
        }

        return trimmed.Trim(Pipe).Split(Pipe).Select(cell => cell.Trim()).ToArray();
    }

    private static string Unquote(string cell) => cell.Replace(Backtick.ToString(), string.Empty, StringComparison.Ordinal).Trim();

    [GeneratedRegex(@"^:?-{3,}:?$", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorCell();

    [GeneratedRegex(@"^(?<path>.+?)(?::(?<line>\d+)|#L(?<line>\d+)(?:-L?\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathWithLine();

    [GeneratedRegex(@"^L?(?<start>\d+)(?:\s*-\s*L?\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineCell();
}
