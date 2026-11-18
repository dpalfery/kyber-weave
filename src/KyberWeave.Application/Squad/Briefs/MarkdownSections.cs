using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>One heading of a Markdown file with the lines that follow it, up to the next heading of any level.</summary>
/// <param name="Level">1 to 6, or 0 for text before the first heading.</param>
/// <param name="Heading">The heading text, or null for text before the first heading.</param>
/// <param name="Parent">The index of the nearest enclosing heading, or -1.</param>
/// <param name="Text">The heading line and its own lines, with their line endings.</param>
public sealed record MarkdownSection(int Level, string? Heading, int Parent, string Text);

/// <summary>Splits Markdown into headed sections and matches headings the way a brief map spells them.</summary>
public static partial class MarkdownSections
{
    /// <summary>Splits <paramref name="markdown"/>, ignoring <c>#</c> lines inside code fences.</summary>
    public static IReadOnlyList<MarkdownSection> Split(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        List<(int Level, string? Heading, StringBuilder Text)> raw = [];
        StringBuilder current = new();
        int level = 0;
        string? heading = null;
        char fence = '\0';

        foreach (string line in LinesWithEndings(markdown))
        {
            string trimmed = line.TrimStart(' ');
            bool indented = line.Length - trimmed.Length <= 3;
            if (indented && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                fence = fence == '\0' ? trimmed[0] : fence == trimmed[0] ? '\0' : fence;
            }

            Match match = fence == '\0' ? HeadingLine().Match(line) : Match.Empty;
            if (match.Success)
            {
                if (current.Length > 0 || heading is not null)
                {
                    raw.Add((level, heading, current));
                }

                current = new StringBuilder();
                level = match.Groups["marks"].Length;
                heading = match.Groups["text"].Value.Trim();
            }

            current.Append(line);
        }

        if (current.Length > 0 || heading is not null)
        {
            raw.Add((level, heading, current));
        }

        List<MarkdownSection> sections = [];
        Stack<int> open = new();
        for (int index = 0; index < raw.Count; index++)
        {
            while (open.Count > 0 && raw[open.Peek()].Level >= raw[index].Level)
            {
                open.Pop();
            }

            int parent = raw[index].Level == 0 || open.Count == 0 ? -1 : open.Peek();
            sections.Add(new MarkdownSection(raw[index].Level, raw[index].Heading, parent, raw[index].Text.ToString()));
            if (raw[index].Level > 0)
            {
                open.Push(index);
            }
        }

        return sections;
    }

    /// <summary>
    /// The form two spellings of one heading share: upper case, with emphasis marks and punctuation
    /// removed and white space collapsed.
    /// </summary>
    public static string Normalize(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        StringBuilder builder = new();
        bool gap = false;
        foreach (char character in heading.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (gap && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                gap = false;
                builder.Append(character);
            }
            else if (character is not ('*' or '_' or '`'))
            {
                gap = true;
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> LinesWithEndings(string text)
    {
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline + 1;
            yield return text[start..end];
            start = end;
        }
    }

    [GeneratedRegex(@"^ {0,3}(?<marks>#{1,6})[ \t]+(?<text>.*?)(?:[ \t]+#+)?[ \t]*\r?\n?$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();
}
