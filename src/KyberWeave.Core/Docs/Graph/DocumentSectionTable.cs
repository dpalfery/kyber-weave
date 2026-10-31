using System.Text;

namespace KyberWeave.Core.Docs.Graph;

/// <summary>Pipe-table parsing for section bodies.</summary>
internal static class DocumentSectionTable
{
    internal static List<string[]>? ReadDataRows(string sectionBody)
    {
        List<string> lines = sectionBody
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();
        List<string> tableLines = new();
        bool inFence = false;
        foreach (string line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && line.Length > 0 && line[0] == '|')
            {
                tableLines.Add(line);
            }
        }

        if (tableLines.Count < 2)
        {
            return null;
        }

        List<string[]> rows = new();
        for (int index = 0; index < tableLines.Count; index++)
        {
            List<string> cells = SplitRowCells(tableLines[index]);
            if (cells.Count > 0 && cells[0].Length == 0)
            {
                cells.RemoveAt(0);
            }

            if (cells.Count > 0 && cells[^1].Length == 0)
            {
                cells.RemoveAt(cells.Count - 1);
            }

            if (cells.Count > 0 && cells.All(IsSeparatorCell))
            {
                continue;
            }

            if (index == 0)
            {
                continue;
            }

            rows.Add(cells.ToArray());
        }

        return rows;
    }

    private static List<string> SplitRowCells(string line)
    {
        List<string> cells = new();
        StringBuilder current = new();
        int bracketDepth = 0;
        int parenDepth = 0;
        bool inCode = false;
        int codeTickCount = 0;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (!inCode && character == '\\' && index + 1 < line.Length && line[index + 1] == '|')
            {
                current.Append('|');
                index++;
                continue;
            }

            if (character == '`')
            {
                int run = 1;
                while (index + run < line.Length && line[index + run] == '`')
                {
                    run++;
                }

                if (!inCode)
                {
                    inCode = true;
                    codeTickCount = run;
                    current.Append('`', run);
                    index += run - 1;
                    continue;
                }

                if (run == codeTickCount)
                {
                    inCode = false;
                    codeTickCount = 0;
                    current.Append('`', run);
                    index += run - 1;
                    continue;
                }

                current.Append(character);
                continue;
            }

            if (!inCode)
            {
                if (character == '[')
                {
                    bracketDepth++;
                }
                else if (character == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                }
                else if (character == '(' && (parenDepth > 0 || (index > 0 && line[index - 1] == ']')))
                {
                    parenDepth++;
                }
                else if (character == ')' && parenDepth > 0)
                {
                    parenDepth--;
                }
                else if (character == '|' && bracketDepth == 0 && parenDepth == 0)
                {
                    cells.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
            }

            current.Append(character);
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static bool IsSeparatorCell(string cell)
    {
        string trimmed = cell.Trim(':');
        return trimmed.Length >= 3 && trimmed.All(character => character == '-');
    }
}
