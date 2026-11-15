using System.Globalization;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>The fields of the finding a refutation dispatch carries in its prompt.</summary>
/// <remarks>
/// The finding is one block of the prompt's text, among prose, and model-written values
/// are often not valid YAML (an unquoted <c>claim</c> holding a colon). So the block is
/// read line by line from its <c>id:</c>, rather than handed to a YAML parser that would
/// lose the whole finding to one such value.
/// </remarks>
internal sealed record RefuteFinding(string? File, int? Line, string? Excerpt, string? Claim)
{
    private const string IdKey = "id:";

    /// <summary>Reads the finding whose <c>id</c> is <paramref name="id"/>; null when the prompt has none.</summary>
    public static RefuteFinding? Parse(string prompt, string id)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(id);

        string[] lines = prompt.ReplaceLineEndings("\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            int keyColumn = IdKeyColumn(lines[index], id);
            if (keyColumn >= 0)
            {
                Dictionary<string, string> fields = ReadFields(lines, index + 1, keyColumn);
                return new RefuteFinding(
                    fields.GetValueOrDefault("file"),
                    int.TryParse(fields.GetValueOrDefault("line"), NumberStyles.None, CultureInfo.InvariantCulture, out int line)
                        ? line
                        : null,
                    fields.GetValueOrDefault("excerpt"),
                    fields.GetValueOrDefault("claim"));
            }
        }

        return null;
    }

    /// <summary>The column of the <c>id:</c> key on a line naming <paramref name="id"/>, or -1.</summary>
    private static int IdKeyColumn(string line, string id)
    {
        int column = Indent(line);
        if (string.CompareOrdinal(line, column, "- ", 0, 2) == 0)
        {
            column += 2;
            column += Indent(line[column..]);
        }

        if (string.CompareOrdinal(line, column, IdKey, 0, IdKey.Length) != 0)
            return -1;

        string value = Unquote(line[(column + IdKey.Length)..].Trim());
        return string.Equals(value, id, StringComparison.Ordinal) ? column : -1;
    }

    private static Dictionary<string, string> ReadFields(string[] lines, int start, int keyColumn)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        int index = start;
        while (index < lines.Length)
        {
            string line = lines[index];
            if (IsBlank(line))
            {
                index++;
                continue;
            }

            int colon = line.IndexOf(':', Math.Min(keyColumn, line.Length));
            if (Indent(line) != keyColumn || colon <= keyColumn || !IsKey(line.AsSpan(keyColumn, colon - keyColumn)))
                break;

            string key = line[keyColumn..colon];
            string value = line[(colon + 1)..].Trim();
            bool block = value.StartsWith('|') || value.StartsWith('>');
            index++;
            int end = index;
            while (end < lines.Length && (Indent(lines[end]) > keyColumn && !IsBlank(lines[end]) || (block && IsBlank(lines[end]))))
                end++;

            while (block && end > index && IsBlank(lines[end - 1]))
                end--;

            ArraySegment<string> body = new(lines, index, end - index);
            fields[key] = block
                ? BlockScalar(body)
                : Unquote(string.Join(' ', body.Select(item => item.Trim()).Prepend(value)).Trim());
            index = end;
        }

        return fields;
    }

    private static string BlockScalar(IReadOnlyCollection<string> lines)
    {
        int indent = lines.Where(line => !IsBlank(line)).Select(Indent).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(line => line.Length > indent ? line[indent..] : string.Empty));
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static bool IsKey(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
        {
            if (character is not (>= 'a' and <= 'z' or '_'))
                return false;
        }

        return true;
    }

    private static int Indent(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ')
            count++;
        return count;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\''
            ? value[1..^1]
            : value;
}
