using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Review;

/// <summary>
/// Reads a seat's findings list as the flat schema the seat contract defines, when strict YAML refuses it.
/// </summary>
/// <remarks>
/// <para>
/// The seat schema is flat: a list of findings, each a mapping of one-word keys to text. Seats write those values
/// the way a person writes a sentence, and two common forms are not valid plain YAML scalars: a value that starts
/// with a backtick, and a value that contains <c>": "</c>. A strict reader refuses the whole file for either, which
/// throws away every finding in it. This reader takes a field's value as the rest of its line, joined with any more
/// deeply indented continuation lines, or as a <c>|</c> or <c>&gt;</c> block scalar, so what the seat wrote is
/// what the verdict reads.
/// </para>
/// <para>
/// It is deliberately not a YAML parser. It knows list items, fields, block scalars, quoted values and document
/// separators, and refuses anything else with the line and column, so a seat whose shape is wrong is still told
/// where.
/// </para>
/// </remarks>
internal static partial class SeatFlatYamlReader
{
    private const string DocumentStart = "---";
    private const string DocumentEnd = "...";

    /// <summary>Reads every finding in <paramref name="yaml"/>.</summary>
    /// <param name="seatLens">The lens the seat ran, used in refusal messages.</param>
    /// <param name="yaml">The seat's YAML lines with their file line numbers.</param>
    /// <exception cref="SeatOutputException">A line is neither a finding, a field, nor a field's continuation.</exception>
    public static IReadOnlyList<SeatItem> Read(string seatLens, IReadOnlyList<SeatLine> yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        List<SeatItem> items = [];
        ItemBuilder? item = null;
        FieldBuilder? field = null;

        foreach (SeatLine line in yaml)
        {
            string text = line.Text;
            int indent = Indent(text);

            if (field is { IsBlock: true } && (text.Trim().Length == 0 || (item is not null && indent > item.KeyIndent)))
            {
                field.Lines.Add(text);
                continue;
            }

            if (text.Trim().Length == 0)
            {
                field?.Lines.Add(string.Empty);
                continue;
            }

            if (indent == 0 && text.TrimEnd() is DocumentStart or DocumentEnd)
            {
                Close(items, ref item, ref field);
                continue;
            }

            Match start = ItemStart().Match(text);
            if (start.Success && (item is null || start.Groups["indent"].Length == item.ItemIndent))
            {
                Close(items, ref item, ref field);
                item = new ItemBuilder(line.Number, indent + 1, indent, indent + 2);
                field = item.Open(start.Groups["key"].Value, start.Groups["rest"].Value);
                continue;
            }

            if (item is null)
            {
                throw new SeatOutputException(
                    $"The {seatLens} seat output has text where a finding should start " +
                    $"(line {line.Number}, column {indent + 1}). Start each finding with '- id: <lens>/<slug>'.");
            }

            Match key = FieldLine().Match(text);
            if (key.Success && indent == item.KeyIndent)
            {
                field = item.Open(key.Groups["key"].Value, key.Groups["rest"].Value);
                continue;
            }

            if (field is not null && indent > item.KeyIndent)
            {
                field.Lines.Add(text);
                continue;
            }

            throw new SeatOutputException(
                $"The {seatLens} seat output has a line that is not a field of its finding " +
                $"(line {line.Number}, column {indent + 1}). Write each field as '<key>: <value>', indented under " +
                "its finding.");
        }

        Close(items, ref item, ref field);
        return items;
    }

    private static void Close(List<SeatItem> items, ref ItemBuilder? item, ref FieldBuilder? field)
    {
        if (item is not null)
            items.Add(item.Build());

        item = null;
        field = null;
    }

    private static int Indent(string text)
    {
        int indent = 0;
        while (indent < text.Length && text[indent] == ' ')
            indent++;

        return indent;
    }

    /// <summary>A list item that opens a finding: <c>- key: value</c>.</summary>
    [GeneratedRegex(
        @"^(?<indent> *)-[ ]+(?<key>[A-Za-z_][A-Za-z0-9_-]*):(?:[ \t]+(?<rest>.*)|[ \t]*$)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex ItemStart();

    /// <summary>A field of the current finding: <c>key: value</c>, at the finding's key indent.</summary>
    [GeneratedRegex(
        @"^ *(?<key>[A-Za-z_][A-Za-z0-9_-]*):(?:[ \t]+(?<rest>.*)|[ \t]*$)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex FieldLine();

    /// <summary>A block scalar header: <c>|</c> or <c>&gt;</c>, with optional chomping and indentation indicators.</summary>
    [GeneratedRegex(@"^(?<style>[|>])[+-]?[0-9]?[+-]?\s*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex BlockHeader();

    private sealed class ItemBuilder(int line, int column, int itemIndent, int keyIndent)
    {
        private readonly List<FieldBuilder> _fields = [];

        public int ItemIndent { get; } = itemIndent;

        public int KeyIndent { get; } = keyIndent;

        public FieldBuilder Open(string key, string rest)
        {
            Match block = BlockHeader().Match(rest);
            FieldBuilder field = block.Success
                ? new FieldBuilder(key, null, block.Groups["style"].Value[0])
                : new FieldBuilder(key, rest.Trim(), null);
            _fields.Add(field);
            return field;
        }

        public SeatItem Build()
        {
            Dictionary<string, SeatField> fields = new(StringComparer.Ordinal);
            foreach (FieldBuilder field in _fields)
                fields[field.Key] = new SeatField(field.Value(), IsText: true);

            return new SeatItem(line, column, fields);
        }
    }

    private sealed class FieldBuilder(string key, string? first, char? blockStyle)
    {
        public string Key { get; } = key;

        public bool IsBlock => blockStyle is not null;

        public List<string> Lines { get; } = [];

        public string? Value()
        {
            if (blockStyle is { } style)
                return Block(style);

            // A plain value continues on more deeply indented lines, folded into one line with spaces, as YAML
            // folds a multi-line plain scalar.
            IEnumerable<string> parts = new[] { first ?? string.Empty }
                .Concat(Lines.Select(line => line.Trim()))
                .Where(part => part.Length > 0);
            return Unquote(string.Join(' ', parts));
        }

        /// <summary>A block scalar's lines, with the indentation of its first non-blank line removed.</summary>
        private string Block(char style)
        {
            List<string> lines = [.. Lines];
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);

            string? firstContent = lines.Find(line => line.Trim().Length > 0);
            int indent = firstContent is null ? 0 : Indent(firstContent);
            List<string> content = [.. lines.Select(line => line.Length >= indent ? line[indent..] : line.TrimStart())];

            if (style == '|')
                return string.Join('\n', content);

            // Folded: lines of a paragraph join with a space, and a blank line separates paragraphs.
            StringBuilder folded = new();
            bool startOfParagraph = true;
            foreach (string line in content)
            {
                if (line.Trim().Length == 0)
                {
                    folded.Append('\n');
                    startOfParagraph = true;
                    continue;
                }

                if (!startOfParagraph)
                    folded.Append(' ');

                folded.Append(line);
                startOfParagraph = false;
            }

            return folded.ToString();
        }

        /// <summary>
        /// The text of a value the seat quoted whole, or the value as written. Only a value that both starts and
        /// ends with the same quote is unquoted, so a sentence that merely begins with a quoted word is kept.
        /// </summary>
        private static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                return value[1..^1]
                    .Replace("\\\\", "\u0000", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\t", "\t", StringComparison.Ordinal)
                    .Replace("\u0000", "\\", StringComparison.Ordinal);
            }

            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                return value[1..^1].Replace("''", "'", StringComparison.Ordinal);

            return value;
        }
    }
}
