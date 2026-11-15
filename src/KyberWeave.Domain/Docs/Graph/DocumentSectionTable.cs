using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace KyberWeave.Core.Docs.Graph;

/// <summary>Pipe-table parsing for section bodies.</summary>
/// <remarks>
/// Cell boundaries follow GitHub-flavored Markdown via Markdig, so link destinations,
/// code spans, and escaped pipes stay intact without a second splitter.
/// </remarks>
internal static class DocumentSectionTable
{
    private static readonly MarkdownPipeline TablePipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UsePreciseSourceLocation()
        .Build();

    internal static List<string[]>? ReadDataRows(string sectionBody)
    {
        MarkdownDocument document = Markdown.Parse(sectionBody, TablePipeline);
        List<string[]> rows = new();
        foreach (Table table in document.Descendants<Table>())
        {
            foreach (TableRow row in table.OfType<TableRow>())
            {
                if (row.IsHeader)
                {
                    continue;
                }

                List<string> cells = new();
                foreach (TableCell cell in row.OfType<TableCell>())
                {
                    cells.Add(CellText(sectionBody, cell));
                }

                if (cells.Count > 0)
                {
                    rows.Add(cells.ToArray());
                }
            }
        }

        return rows.Count == 0 ? null : rows;
    }

    private static string CellText(string markdown, TableCell cell)
    {
        if (cell.Span.Start < 0 || cell.Span.End < cell.Span.Start || cell.Span.Start >= markdown.Length)
        {
            return string.Empty;
        }

        int length = Math.Min(markdown.Length - cell.Span.Start, cell.Span.End - cell.Span.Start + 1);
        string slice = markdown.Substring(cell.Span.Start, length).Trim();
        return slice.Replace("\\|", "|", StringComparison.Ordinal);
    }
}
