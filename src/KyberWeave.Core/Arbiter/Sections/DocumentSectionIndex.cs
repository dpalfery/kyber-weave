using System.Text;

namespace KyberWeave.Core.Arbiter.Sections;

/// <summary>
/// Cuts a markdown document into sections at its headings. The split is deterministic and bounded: headings
/// inside fenced code are text, not sections, and a document with more headings than <see cref="MaxSections"/>
/// ends its last section at the end of the document so no text is dropped.
/// </summary>
public static class DocumentSectionIndex
{
    /// <summary>The id of the section made of the front matter and any text before the first heading.</summary>
    public const string FrontMatterId = "front-matter";

    /// <summary>The most sections one document is split into.</summary>
    public const int MaxSections = 200;

    /// <summary>The most lines the split scans. A longer document is scanned to this line and its last section is marked truncated.</summary>
    public const int MaxScannedLines = 50_000;

    /// <summary>The most characters one section's text carries. A longer section is cut and marked truncated.</summary>
    public const int MaxSectionCharacters = 16_000;

    private const string FrontMatterTitle = "Front matter";

    /// <summary>Splits <paramref name="markdown"/> into sections, in document order.</summary>
    /// <param name="markdown">The document text.</param>
    /// <returns>The sections; empty for an empty document.</returns>
    public static IReadOnlyList<DocumentSection> Split(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        List<string> lines = SplitLines(markdown);
        if (lines.Count == 0)
        {
            return [];
        }

        bool scanCut = lines.Count > MaxScannedLines;
        if (scanCut)
        {
            lines = lines.GetRange(0, MaxScannedLines);
        }

        int bodyStart = FrontMatterEnd(lines);
        List<(int Line, string Id, string Title)> starts = [];
        Stack<(int Level, string Id)> parents = new();
        HashSet<string> used = new(StringComparer.Ordinal);
        bool inFence = false;
        string? fence = null;
        for (int index = bodyStart; index < lines.Count; index++)
        {
            string line = lines[index];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                string marker = trimmed[..3];
                if (!inFence)
                {
                    inFence = true;
                    fence = marker;
                }
                else if (fence == marker)
                {
                    inFence = false;
                    fence = null;
                }

                continue;
            }

            if (inFence || !TryHeading(line, out int level, out string title))
            {
                continue;
            }

            while (parents.Count > 0 && parents.Peek().Level >= level)
            {
                parents.Pop();
            }

            string slug = Slug(title);
            string candidate = parents.Count == 0 ? slug : parents.Peek().Id + "/" + slug;
            string id = candidate;
            for (int suffix = 2; !used.Add(id); suffix++)
            {
                id = candidate + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            parents.Push((level, id));
            starts.Add((index + 1, id, title));
        }

        // Text before the first heading, front matter included, is the title section the selector always keeps.
        if (starts.Count == 0 || starts[0].Line > 1)
        {
            starts.Insert(0, (1, FrontMatterId, FrontMatterTitle));
        }

        int count = Math.Min(starts.Count, MaxSections);
        List<DocumentSection> sections = new(count);
        for (int i = 0; i < count; i++)
        {
            int start = starts[i].Line;
            int end = i + 1 < count ? starts[i + 1].Line - 1 : lines.Count;
            if (i == count - 1 && count == MaxSections)
            {
                end = lines.Count;
            }

            string text = string.Join('\n', lines.GetRange(start - 1, end - start + 1));
            bool cut = text.Length > MaxSectionCharacters;
            bool scanEnded = scanCut && i == count - 1;
            sections.Add(new DocumentSection(
                starts[i].Id,
                starts[i].Title,
                start,
                end,
                cut ? text[..MaxSectionCharacters] : text,
                cut || scanEnded));
        }

        return sections;
    }

    /// <summary>
    /// Lines of the document. A trailing newline ends the last line rather than starting a new one.
    /// </summary>
    private static List<string> SplitLines(string markdown)
    {
        if (markdown.Length == 0)
        {
            return [];
        }

        string normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return [.. normalized.Split('\n')];
    }

    /// <summary>The index of the first line after the front matter block, or 0 when the document has none.</summary>
    private static int FrontMatterEnd(List<string> lines)
    {
        if (lines.Count == 0 || lines[0].TrimEnd() != "---")
        {
            return 0;
        }

        for (int index = 1; index < lines.Count; index++)
        {
            if (lines[index].TrimEnd() == "---")
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static bool TryHeading(string line, out int level, out string title)
    {
        level = 0;
        title = string.Empty;
        int count = 0;
        while (count < line.Length && line[count] == '#')
        {
            count++;
        }

        if (count is < 1 or > 6 || count >= line.Length || line[count] != ' ')
        {
            return false;
        }

        level = count;
        title = line[count..].Trim();
        return title.Length > 0;
    }

    private static string Slug(string title)
    {
        StringBuilder builder = new(title.Length);
        bool dash = false;
        foreach (char raw in title)
        {
            char c = char.ToLowerInvariant(raw);
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                dash = false;
            }
            else if (!dash && builder.Length > 0)
            {
                builder.Append('-');
                dash = true;
            }
        }

        string slug = builder.ToString().TrimEnd('-');
        return slug.Length == 0 ? "section" : slug;
    }
}
