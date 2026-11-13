using System.Text.RegularExpressions;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The dispatch header block (design section 5): the run of lines at the very start
/// of the prompt in which every line matches
/// <c>^(KYBER-ARBITER|PLAN_FILE|TASK|LENS|REFUTE):[ \t]*(\S.*?)[ \t]*$</c>.
/// The block ends at the first line that does not match. The set is closed:
/// there is no <c>FILES:</c> header.
/// </summary>
/// <remarks>
/// Names are case-sensitive and each appears at most once. A duplicate name or a
/// malformed value leaves that fact absent: the header line still counts toward
/// <see cref="LineCount"/> (it matched the line grammar), but the name is missing
/// from <see cref="Headers"/> so the rules decide on the absence.
/// </remarks>
public sealed partial class HeaderBlock
{
    /// <summary>The closed header set.</summary>
    public static IReadOnlySet<string> ClosedNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "KYBER-ARBITER",
        "PLAN_FILE",
        "TASK",
        "LENS",
        "REFUTE",
        "REVIEW_RUN",
    };

    /// <summary>The fifteen lens names a <c>LENS</c> header or <c>REFUTE</c> lens may name.</summary>
    public static IReadOnlySet<string> LensNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "authz-tenancy",
        "blast-radius-revertibility",
        "correctness",
        "dependency-supply-chain",
        "di-composition",
        "duplicate-implementation",
        "infra-workflow",
        "intent-alignment",
        "model-placement",
        "performance",
        "prior-art",
        "security",
        "static-analysis-triage",
        "supportability",
        "test-adequacy",
    };

    /// <summary>The valid headers, in prompt order.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The raw header lines consumed, including duplicates whose fact is absent.</summary>
    public int LineCount { get; }

    /// <summary>
    /// The names whose line matched the header grammar but whose value failed validation or
    /// which appeared more than once. The values are not kept, so a rejected prompt never
    /// carries its own text into the decision log.
    /// </summary>
    public IReadOnlySet<string> RejectedNames { get; }

    /// <summary>
    /// The raw value of every closed header name the prompt writes in its first lines, keyed by the canonical
    /// name. It keeps what <see cref="Headers"/> discards: a value that failed validation (a <c>PLAN_FILE</c>
    /// with a space or an absolute path), a repeated name (first sighting), a name in another case
    /// (<c>plan_file:</c>), and a header line written after a line that ended the block. It is evidence for a
    /// model question and a lenient plan lookup, never a gate: nothing blocks on it.
    /// </summary>
    public IReadOnlyDictionary<string, string> RawValues { get; }

    /// <summary>Whether the block carries the routing marker.</summary>
    public bool IsMarked => Headers.ContainsKey("KYBER-ARBITER");

    /// <summary>How many leading lines the raw tier reads. Headers sit at the top of a dispatch.</summary>
    private const int RawScanLines = 40;

    private HeaderBlock(
        IReadOnlyDictionary<string, string> headers,
        int lineCount,
        IReadOnlySet<string> rejectedNames,
        IReadOnlyDictionary<string, string> rawValues)
    {
        Headers = headers;
        LineCount = lineCount;
        RejectedNames = rejectedNames;
        RawValues = rawValues;
    }

    /// <summary>Parses the header block at the start of <paramref name="prompt"/>.</summary>
    public static HeaderBlock Parse(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        string[] lines = prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        HashSet<string> rejected = new(StringComparer.Ordinal);
        int count = 0;

        foreach (string line in lines)
        {
            Match match = HeaderLineRegex().Match(line);
            if (!match.Success)
                break;

            count++;
            string name = match.Groups["name"].Value;
            string value = match.Groups["value"].Value.Trim();

            if (headers.ContainsKey(name) || rejected.Contains(name))
            {
                // A duplicate leaves the fact absent, whichever sighting was valid.
                headers.Remove(name);
                rejected.Add(name);
                continue;
            }

            if (!IsValidValue(name, value))
            {
                rejected.Add(name);
                continue;
            }

            headers[name] = value;
        }

        return new HeaderBlock(headers, count, rejected, ReadRawValues(lines, headers));
    }

    /// <summary>
    /// The raw tier: a validated header keeps its value; otherwise the first line among the leading lines whose
    /// name matches a closed name, ignoring case and treating <c>-</c> and <c>_</c> alike, supplies the raw value.
    /// </summary>
    private static Dictionary<string, string> ReadRawValues(string[] lines, IReadOnlyDictionary<string, string> headers)
    {
        Dictionary<string, string> raw = new(headers, StringComparer.Ordinal);
        foreach (string line in lines.Take(RawScanLines))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
                continue;

            string name = CanonicalName(line[..colon].Trim().TrimStart('-', '*', ' ').Trim('*', '`', ' '));
            string value = line[(colon + 1)..].Trim().Trim('`');
            if (name.Length > 0 && value.Length > 0 && !raw.ContainsKey(name))
                raw[name] = value;
        }

        return raw;
    }

    private static string CanonicalName(string written)
    {
        string normalized = written.Replace('-', '_').ToUpperInvariant();
        foreach (string name in ClosedNames)
        {
            if (string.Equals(name.Replace('-', '_'), normalized, StringComparison.Ordinal))
                return name;
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns the prompt without the header block and the one blank line after it
    /// (Req 25.1). Prompts without a block are returned unchanged.
    /// </summary>
    public static string Strip(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        HeaderBlock block = Parse(prompt);
        if (block.LineCount == 0)
            return prompt;

        int offset = 0;
        for (int line = 0; line < block.LineCount && offset < prompt.Length; line++)
        {
            int newline = prompt.IndexOf('\n', offset);
            if (newline < 0)
            {
                offset = prompt.Length;
                break;
            }

            offset = newline + 1;
        }

        string rest = prompt[offset..];
        if (rest.Length == 0)
            return rest;

        int firstNewline = rest.IndexOf('\n', StringComparison.Ordinal);
        string firstLine = firstNewline < 0 ? rest : rest.Substring(0, firstNewline);
        if (firstLine.Trim().Length == 0)
            return firstNewline < 0 ? string.Empty : rest.Substring(firstNewline + 1);

        return rest;
    }

    /// <summary>Tries to get the valid value of <paramref name="name"/>.</summary>
    public bool TryGet(string name, out string? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Headers.TryGetValue(name, out string? found))
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }

    private static bool IsValidValue(string name, string value) => name switch
    {
        "KYBER-ARBITER" => string.Equals(value, "true", StringComparison.Ordinal),
        "PLAN_FILE" => IsValidPlanFile(value),
        "TASK" => PlanTaskIdRegex().IsMatch(value) || SpecTaskIdRegex().IsMatch(value),
        "LENS" => LensNames.Contains(value),
        "REFUTE" => IsValidRefute(value),
        // Any non-empty value: the store keys it by digest, so its characters and length name no path.
        "REVIEW_RUN" => value.Length > 0,
        _ => false,
    };

    private static bool IsValidPlanFile(string value)
    {
        if (value.Length == 0
            || value.StartsWith('/')
            || value.Contains('\\', StringComparison.Ordinal)
            || value.AsSpan().IndexOfAny(' ', '\t') >= 0)
            return false;

        foreach (string segment in value.Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, "..", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool IsValidRefute(string value)
    {
        int slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == value.Length - 1)
            return false;

        string lens = value.Substring(0, slash);
        string slug = value.Substring(slash + 1);
        return LensNames.Contains(lens) && RefuteSlugRegex().IsMatch(slug);
    }

    [GeneratedRegex(
        @"^(?<name>KYBER-ARBITER|PLAN_FILE|TASK|LENS|REFUTE|REVIEW_RUN):[ \t]*(?<value>\S.*?)[ \t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderLineRegex();

    [GeneratedRegex(@"^T\d+[a-z]?(?:-[A-Za-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlanTaskIdRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SpecTaskIdRegex();

    [GeneratedRegex(@"^[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RefuteSlugRegex();
}
