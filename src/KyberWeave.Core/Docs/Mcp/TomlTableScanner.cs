using System.Text;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>Whether a TOML document could be classified.</summary>
public enum TomlScanStatus
{
    /// <summary>
    /// Every line was read, so <see cref="TomlScanReport.Servers"/> is the complete answer: a
    /// server missing from it is absent from the document.
    /// </summary>
    Classified,

    /// <summary>
    /// The document holds a region this classifier cannot read. No server list is reported, not
    /// even the tables found before the uncertainty: a caller handed a partial list would treat
    /// "not in that list" as "absent", which is the answer the refusal exists to avoid.
    /// </summary>
    Refused
}

/// <summary>What a TOML document declares under <c>[mcp_servers.&lt;name&gt;]</c>.</summary>
/// <param name="Status">Whether the document could be classified.</param>
/// <param name="Servers">
/// The <c>&lt;name&gt;</c> segment of every <c>[mcp_servers.&lt;name&gt;]</c> table, in
/// first-declared order. Always empty when <paramref name="Status"/> is
/// <see cref="TomlScanStatus.Refused"/>.
/// </param>
/// <param name="Reason">Why the scan refused, when it did.</param>
public sealed record TomlScanReport(
    TomlScanStatus Status,
    IReadOnlyList<string> Servers,
    string? Reason);

/// <summary>
/// A string-region-aware, line-oriented classifier for a Codex <c>config.toml</c>: it answers one
/// question — which <c>[mcp_servers.&lt;name&gt;]</c> tables does this document declare? — and
/// refuses everything it cannot answer.
/// </summary>
/// <remarks>
/// <para>
/// There is no TOML parser in this repository and no dependency slot for one: Core takes Markdig
/// and YamlDotNet, and a hand-written full parser would be the duplicate-key corruption risk the
/// write bounds forbid. So this is a classifier, not a parser. It understands exactly what it must
/// to find a table header and refuses to guess at the rest.
/// </para>
/// <para>
/// The direction of every refusal is deliberate. A false "absent" tells the operator a working
/// configuration is broken; a false "present" lets a writer add a table to a document it never
/// understood, and lets the check confirm a wiring nobody has. Where both are possible the scanner
/// refuses, and the caller reports a gap rather than a pass.
/// </para>
/// <para>
/// Regions that are prose rather than TOML are the reason this cannot be a substring search. A
/// <c>[mcp_servers.ghost]</c> line inside a <c>"""</c> note, inside a single-line string, or
/// behind a <c>#</c> is not a table, and each of those shapes appears in real files — the first as
/// documentation, the last as the residue of a setup attempt that was abandoned.
/// </para>
/// </remarks>
public static class TomlTableScanner
{
    private const string MultilineBasicDelimiter = "\"\"\"";

    private const string MultilineLiteralDelimiter = "'''";

    /// <summary>The TOML table whose child tables declare MCP servers.</summary>
    private const string ServerTableRoot = "mcp_servers";

    /// <summary>Reads a document and reports the server tables it declares.</summary>
    /// <param name="text">The document's text. An empty or blank document declares nothing.</param>
    public static TomlScanReport Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<string> servers = [];
        bool inMultilineBasic = false;
        bool inMultilineLiteral = false;

        foreach (string line in text.Split('\n'))
        {
            string content = line.TrimEnd('\r');
            int index = 0;
            int firstToken = FirstTokenIndex(content);

            while (index < content.Length)
            {
                if (inMultilineBasic)
                {
                    int close = content.IndexOf(MultilineBasicDelimiter, index, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        index = content.Length;
                        break;
                    }

                    inMultilineBasic = false;
                    index = close + MultilineBasicDelimiter.Length;
                    continue;
                }

                if (inMultilineLiteral)
                {
                    int close = content.IndexOf(MultilineLiteralDelimiter, index, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        index = content.Length;
                        break;
                    }

                    inMultilineLiteral = false;
                    index = close + MultilineLiteralDelimiter.Length;
                    continue;
                }

                char current = content[index];

                if (current == '#')
                    break;

                if (current == '"' && StartsWith(content, index, MultilineBasicDelimiter))
                {
                    inMultilineBasic = true;
                    index += MultilineBasicDelimiter.Length;
                    continue;
                }

                if (current == '\'' && StartsWith(content, index, MultilineLiteralDelimiter))
                {
                    inMultilineLiteral = true;
                    index += MultilineLiteralDelimiter.Length;
                    continue;
                }

                if (current == '"' || current == '\'')
                {
                    int close = ClosingQuote(content, index + 1, current);
                    if (close < 0)
                    {
                        return Refused(
                            $"a string opened on '{Summarize(content)}' is never closed on its own line, so the rest of the document is unreadable");
                    }

                    index = close + 1;
                    continue;
                }

                if (current == '[' && index == firstToken)
                {
                    string? refusal = ReadHeader(content, index, servers, out int next);
                    if (refusal is not null)
                        return Refused(refusal);

                    index = next;
                    continue;
                }

                index++;
            }
        }

        if (inMultilineBasic || inMultilineLiteral)
            return Refused("a multi-line string is opened and never closed, so any later header may be prose");

        return new TomlScanReport(TomlScanStatus.Classified, servers, null);
    }

    /// <summary>
    /// Classifies one table header. Returns a refusal reason, or null when the header is either
    /// recorded or is not this scanner's business.
    /// </summary>
    private static string? ReadHeader(string line, int start, List<string> servers, out int next)
    {
        next = line.Length;

        bool arrayOfTables = start + 1 < line.Length && line[start + 1] == '[';
        int open = arrayOfTables ? start + 1 : start;

        int close = ClosingBracket(line, open + 1);
        if (close < 0)
            return $"the header on '{Summarize(line)}' is never closed, so the document stops being classifiable";

        next = arrayOfTables ? close + 2 : close + 1;
        if (arrayOfTables && (close + 1 >= line.Length || line[close + 1] != ']'))
            return $"the array-of-tables header on '{Summarize(line)}' is malformed";

        if (arrayOfTables)
            return null;

        List<TablePathSegment> segments = SplitTablePath(line[(open + 1)..close]);
        if (segments.Count == 0 || !segments[0].Value.Equals(ServerTableRoot, StringComparison.Ordinal))
            return null;

        if (segments.Count == 1)
            return $"[{ServerTableRoot}] is declared as a parent table; this classifier reads [mcp_servers.<name>] headers and cannot enumerate the inline server keys under it";

        foreach (TablePathSegment segment in segments)
        {
            if (!IsKey(segment))
                return $"'{segment.Value}' in the header on '{Summarize(line)}' is not a TOML key, so the server it names cannot be read";
        }

        string name = segments[1].Value;
        if (!servers.Contains(name, StringComparer.Ordinal))
            servers.Add(name);

        // A deeper header — [mcp_servers.<name>.env] — implicitly creates the server's table, so
        // the server is declared; only the name is reported.
        return null;
    }

    /// <summary>
    /// Splits a table path on its dots, keeping quoted segments intact. Dots inside a quoted key
    /// are part of the name, not separators.
    /// </summary>
    private static List<TablePathSegment> SplitTablePath(string path)
    {
        List<TablePathSegment> segments = [];
        StringBuilder current = new();
        bool quoted = false;
        bool inQuote = false;
        char quote = '\0';

        foreach (char character in path)
        {
            if (inQuote)
            {
                if (character == quote)
                {
                    inQuote = false;
                    continue;
                }

                current.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                inQuote = true;
                quoted = true;
                quote = character;
                continue;
            }

            if (character == '.')
            {
                segments.Add(Trim(current, quoted));
                current.Clear();
                quoted = false;
                continue;
            }

            current.Append(character);
        }

        segments.Add(Trim(current, quoted));
        return segments;

        static TablePathSegment Trim(StringBuilder builder, bool wasQuoted) =>
            new(wasQuoted ? builder.ToString() : builder.ToString().Trim(), wasQuoted);
    }

    /// <summary>
    /// Whether a segment can be a TOML key. A quoted key may hold anything but a control
    /// character; a bare key is limited to ASCII letters, digits, underscores and dashes, so a
    /// name with a space is refused rather than repaired into one the document never declared.
    /// </summary>
    private static bool IsKey(TablePathSegment segment)
    {
        if (segment.Value.Length == 0)
            return false;

        if (segment.Quoted)
            return !segment.Value.Any(char.IsControl);

        foreach (char character in segment.Value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_' && character != '-')
                return false;
        }

        return true;
    }

    /// <summary>Finds the closing <c>]</c> of a header, ignoring brackets inside a quoted key.</summary>
    private static int ClosingBracket(string line, int start)
    {
        char quote = '\0';

        for (int index = start; index < line.Length; index++)
        {
            char character = line[index];

            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character == ']')
                return index;
        }

        return -1;
    }

    /// <summary>
    /// Finds the quote that closes a single-line string, skipping escaped quotes. A basic string
    /// cannot span lines, so reaching the end of the line means it was never closed.
    /// </summary>
    private static int ClosingQuote(string line, int start, char quote)
    {
        for (int index = start; index < line.Length; index++)
        {
            char character = line[index];

            if (quote == '"' && character == '\\')
            {
                index++;
                continue;
            }

            if (character == quote)
                return index;
        }

        return -1;
    }

    private static bool StartsWith(string line, int index, string token) =>
        line.Length - index >= token.Length &&
        string.CompareOrdinal(line, index, token, 0, token.Length) == 0;

    private static int FirstTokenIndex(string line)
    {
        for (int index = 0; index < line.Length; index++)
        {
            if (!char.IsWhiteSpace(line[index]))
                return index;
        }

        return -1;
    }

    /// <summary>
    /// A short form of a line for a refusal reason: a diagnostic that quotes 200 characters of
    /// TOML is one nobody reads.
    /// </summary>
    private static string Summarize(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length <= 40 ? trimmed : trimmed[..40] + "…";
    }

    private static TomlScanReport Refused(string reason) =>
        new(TomlScanStatus.Refused, [], reason);

    /// <summary>One dotted segment of a table header, and whether the document quoted it.</summary>
    private readonly record struct TablePathSegment(string Value, bool Quoted);
}
