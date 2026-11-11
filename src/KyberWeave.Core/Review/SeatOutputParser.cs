using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Review;

/// <summary>Parses one council seat's output file into its findings and status.</summary>
/// <remarks>
/// <para>
/// A seat writes for a reader, not for a serializer: a YAML list of findings, usually in a code fence and
/// sometimes not, followed by closing lines such as the <c>REVIEW.md sections checked:</c> attestation. Real
/// seats put those closing lines inside the fence as often as outside it, so they are lifted out by their
/// prefix, at the start of a line, before the YAML is read rather than by their position.
/// </para>
/// <para>
/// Seats also write plain values that strict YAML refuses: a claim that starts with a backtick, or a sentence
/// that contains <c>": "</c>. Refusing those cost a council cycle each, so when strict YAML cannot read the
/// list, <see cref="SeatFlatYamlReader"/> reads it as the flat schema the seat contract defines, taking the
/// rest of a field's line, its continuation lines and its block scalars as the value.
/// </para>
/// <para>
/// The parser keeps what the seat wrote. Severity and confidence are read exactly as written, a missing
/// <c>claim</c> stays absent, and a missing <c>lens</c> is the id prefix before the slash, because the id is
/// lens-qualified by contract. Whether an incomplete finding counts is the verdict engine's decision.
/// </para>
/// </remarks>
public static partial class SeatOutputParser
{
    private const string NoFindingsPrefix = "NO FINDINGS:";
    private const string SkippedPrefix = "SKIPPED:";
    private const string IndexFallbackPrefix = "Index fallback:";
    private const string DiscoveryPrefix = "Discovery:";
    private const string AttestationPrefix = "REVIEW.md sections checked:";
    private const char FenceCharacter = '`';
    private const int MinimumFenceLength = 3;
    private const int MinimumConfidence = 1;
    private const int MaximumConfidence = 10;

    /// <summary>The fields the parser reads as text.</summary>
    private static readonly HashSet<string> TextFields = new(StringComparer.Ordinal)
    {
        "id", "lens", "severity", "confidence", "file", "line", "excerpt", "claim", "evidence",
        "failure_scenario", "failureScenario", "suggestion"
    };

    /// <summary>Parses the text a seat wrote for one lens.</summary>
    /// <param name="lens">The lens the seat ran, taken from the file name.</param>
    /// <param name="text">The seat's output file contents.</param>
    /// <exception cref="SeatOutputException">
    /// The text holds no recognisable outcome, declares an outcome and also holds findings, or its findings
    /// cannot be read. The message names the line and column where the reading failed.
    /// </exception>
    public static SeatOutput Parse(string lens, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lens);
        ArgumentNullException.ThrowIfNull(text);

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        string? attestation = null;
        string? indexFallback = null;
        SeatDiscovery? discovery = null;
        SeatOutputKind? declared = null;
        int declaredLine = 0;
        List<SeatLine> body = [];

        // Closing lines are recognised only at the start of a line. An indented one is inside a finding's
        // excerpt or evidence, and lifting it out would silently change what the seat wrote.
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (line.StartsWith(AttestationPrefix, StringComparison.Ordinal))
                attestation = trimmed;
            else if (line.StartsWith(IndexFallbackPrefix, StringComparison.Ordinal))
                indexFallback = trimmed[IndexFallbackPrefix.Length..].Trim();
            else if (line.StartsWith(DiscoveryPrefix, StringComparison.Ordinal))
                discovery = ReadDiscovery(trimmed);
            else if (line.StartsWith(NoFindingsPrefix, StringComparison.Ordinal)
                     || line.StartsWith(SkippedPrefix, StringComparison.Ordinal))
            {
                SeatOutputKind outcome = line.StartsWith(NoFindingsPrefix, StringComparison.Ordinal)
                    ? SeatOutputKind.NoFindings
                    : SeatOutputKind.Skipped;
                if (declared is { } earlier && earlier != outcome)
                {
                    throw new SeatOutputException(
                        $"The {lens} seat output declares both '{NoFindingsPrefix}' and '{SkippedPrefix}' (lines " +
                        $"{declaredLine} and {index + 1}). A seat reports one outcome.");
                }

                if (declared is null)
                {
                    declared = outcome;
                    declaredLine = index + 1;
                }
            }
            else
                body.Add(new SeatLine(line, index + 1));
        }

        List<SeatLine> yaml = YamlLines(body);

        // A seat that says it found nothing, or did not run, writes prose after that line, not YAML. A file that
        // declares that and also holds a findings list contradicts itself, and counting it clean would lose the
        // findings, so it is refused.
        if (declared is { } kind)
        {
            if (yaml.Find(line => FindingItemStart().IsMatch(line.Text)) is { } item)
            {
                string outcome = kind == SeatOutputKind.NoFindings ? NoFindingsPrefix : SkippedPrefix;
                int column = item.Text.IndexOf('-', StringComparison.Ordinal) + 1;
                throw new SeatOutputException(
                    $"The {lens} seat output declares '{outcome}' (line {declaredLine}, column 1) but also holds a " +
                    $"finding (line {item.Number}, column {column}). A seat reports findings or an outcome line, " +
                    "not both.");
            }

            return new SeatOutput(lens, kind, [], attestation, indexFallback, discovery);
        }

        IReadOnlyList<ReviewFinding> findings = ReadFindings(lens, yaml);
        if (findings.Count == 0)
        {
            throw new SeatOutputException(
                $"The {lens} seat output has no findings list, no '{NoFindingsPrefix}' line and no " +
                $"'{SkippedPrefix}' line.");
        }

        return new SeatOutput(lens, SeatOutputKind.Findings, findings, attestation, indexFallback, discovery);
    }

    /// <summary>
    /// The lines of the YAML a seat wrote. When the seat fenced its list, only fenced lines are read, so a sentence
    /// of prose before the fence is not mistaken for YAML; an unfenced seat is read whole.
    /// </summary>
    /// <remarks>
    /// Only a fence at the start of a line delimits, and a fence closes only on a run of at least as many
    /// backticks as opened it, as in Markdown. An excerpt that quotes a fence is indented under its field, so it
    /// stays content instead of ending the list early and dropping every later finding.
    /// </remarks>
    private static List<SeatLine> YamlLines(List<SeatLine> body)
    {
        if (!body.Exists(line => FenceLength(line.Text) > 0))
            return body;

        List<SeatLine> inside = [];
        int open = 0;
        foreach (SeatLine line in body)
        {
            int fence = FenceLength(line.Text);
            if (open == 0 && fence > 0)
            {
                open = fence;
                continue;
            }

            if (open > 0 && fence >= open && line.Text.TrimEnd().Length == fence)
            {
                open = 0;
                continue;
            }

            if (open > 0)
                inside.Add(line);
        }

        return inside;
    }

    /// <summary>The length of the backtick run a line opens with at column 0, or zero when it is not a fence.</summary>
    private static int FenceLength(string line)
    {
        int length = 0;
        while (length < line.Length && line[length] == FenceCharacter)
            length++;

        return length >= MinimumFenceLength ? length : 0;
    }

    /// <summary>
    /// The counts on a discovery line, or null when the line is not in the agreed shape. A malformed line reads
    /// as no discovery evidence, which KW-REVIEW-044 reports, rather than as an unreadable seat.
    /// </summary>
    private static SeatDiscovery? ReadDiscovery(string line)
    {
        Match match = DiscoveryPattern().Match(line);
        if (!match.Success
            || !int.TryParse(match.Groups["codegraph"].Value, CultureInfo.InvariantCulture, out int codeGraph)
            || !int.TryParse(match.Groups["docs"].Value, CultureInfo.InvariantCulture, out int docs)
            || !int.TryParse(match.Groups["reads"].Value, CultureInfo.InvariantCulture, out int reads))
        {
            return null;
        }

        return new SeatDiscovery(codeGraph, docs, reads);
    }

    /// <summary>
    /// Reads every finding in every YAML document the seat wrote: strictly first, and as the flat seat schema when
    /// strict YAML refuses the text.
    /// </summary>
    private static IReadOnlyList<ReviewFinding> ReadFindings(string seatLens, List<SeatLine> yaml)
    {
        IReadOnlyList<SeatItem> items;
        try
        {
            items = ReadStrict(seatLens, yaml);
        }
        // YamlDotNet reports some malformed flow collections, such as an unclosed '[', as an invalid operation
        // rather than a YamlException, so both are the seat's YAML being unreadable as strict YAML.
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            items = SeatFlatYamlReader.Read(seatLens, yaml);
        }

        // Strict YAML reads a value such as `[Fact]` as a list. A seat that wrote it in a text field meant the text,
        // so such a seat is read flat, where the value is the text as written.
        if (items.Any(item => item.Fields.Any(field => field.Value.IsFlow && TextFields.Contains(field.Key))))
            items = SeatFlatYamlReader.Read(seatLens, yaml);

        List<ReviewFinding> findings = new(items.Count);
        for (int index = 0; index < items.Count; index++)
            findings.Add(ReadFinding(seatLens, index + 1, items[index]));

        return findings;
    }

    private static List<SeatItem> ReadStrict(string seatLens, List<SeatLine> yaml)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(string.Join('\n', yaml.Select(line => line.Text))));

        List<SeatItem> items = [];
        foreach (YamlDocument document in stream.Documents)
        {
            if (document.RootNode is YamlScalarNode { Value: null or "" })
                continue;

            if (document.RootNode is not YamlSequenceNode sequence)
            {
                throw new SeatOutputException(
                    $"The {seatLens} seat output is not a YAML list of findings " +
                    $"({Position(yaml, document.RootNode.Start)}).");
            }

            foreach (YamlNode child in sequence.Children)
            {
                if (child is not YamlMappingNode mapping)
                {
                    throw new SeatOutputException(
                        $"Finding {items.Count + 1} in the {seatLens} seat output is not a mapping of fields " +
                        $"({Position(yaml, child.Start)}).");
                }

                items.Add(ToItem(yaml, mapping));
            }
        }

        return items;
    }

    private static SeatItem ToItem(List<SeatLine> yaml, YamlMappingNode mapping)
    {
        Dictionary<string, SeatField> fields = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            if (entry.Key is not YamlScalarNode { Value: { } key })
                continue;

            fields[key] = entry.Value switch
            {
                YamlScalarNode scalar => new SeatField(scalar.Value, IsText: true),
                YamlSequenceNode { Style: SequenceStyle.Flow } or YamlMappingNode { Style: MappingStyle.Flow } =>
                    new SeatField(null, IsText: false, IsFlow: true),
                _ => new SeatField(null, IsText: false)
            };
        }

        (int line, int column) = SourcePosition(yaml, mapping.Start);
        return new SeatItem(line, column, fields);
    }

    /// <summary>The file line and column of a position in the YAML text, which skips the fence and closing lines.</summary>
    private static (int Line, int Column) SourcePosition(List<SeatLine> yaml, Mark mark)
    {
        if (yaml.Count == 0)
            return (1, 1);

        int index = Math.Clamp((int)mark.Line - 1, 0, yaml.Count - 1);
        return (yaml[index].Number, (int)Math.Max(mark.Column, 1));
    }

    private static string Position(List<SeatLine> yaml, Mark mark)
    {
        (int line, int column) = SourcePosition(yaml, mark);
        return $"line {line}, column {column}";
    }

    private static ReviewFinding ReadFinding(string seatLens, int position, SeatItem item)
    {
        string at = $"line {item.Line}, column {item.Column}";
        string where = $"Finding {position} in the {seatLens} seat output ({at})";
        string id = Required(item, "id", where);
        where = $"Finding '{id}' in the {seatLens} seat output ({at})";

        string lens = Optional(item, "lens", where) is { Length: > 0 } written
            ? written
            : LensFromId(id, seatLens);

        string severityText = Required(item, "severity", where);
        if (!Enum.TryParse(severityText, ignoreCase: true, out ReviewSeverity severity)
            || !Enum.IsDefined(severity)
            || int.TryParse(severityText, CultureInfo.InvariantCulture, out _))
        {
            throw new SeatOutputException($"{where} has severity '{Clip(severityText)}'; use critical, major or minor.");
        }

        int confidence = RequiredNumber(item, "confidence", where);
        if (confidence is < MinimumConfidence or > MaximumConfidence)
        {
            throw new SeatOutputException(
                $"{where} has confidence {confidence}; it must be from {MinimumConfidence} to {MaximumConfidence}.");
        }

        return new ReviewFinding(
            id,
            lens,
            severity,
            confidence,
            Required(item, "file", where),
            RequiredNumber(item, "line", where),
            Optional(item, "excerpt", where),
            Optional(item, "claim", where),
            Optional(item, "evidence", where),
            Optional(item, "failure_scenario", where) ?? Optional(item, "failureScenario", where),
            Optional(item, "suggestion", where));
    }

    private static string LensFromId(string id, string seatLens)
    {
        int slash = id.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? id[..slash] : seatLens;
    }

    private static string Required(SeatItem item, string key, string where) =>
        Optional(item, key, where) is { Length: > 0 } value
            ? value
            : throw new SeatOutputException($"{where} has no '{key}'.");

    private static int RequiredNumber(SeatItem item, string key, string where)
    {
        string value = Required(item, key, where);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : throw new SeatOutputException($"{where} has '{key}: {Clip(value)}', which is not a whole number.");
    }

    /// <summary>A value from the seat's text, cut short so a runaway value cannot flood a diagnostic.</summary>
    private static string Clip(string value) => value.Length <= 200 ? value : value[..200] + "…";

    /// <summary>The trimmed text of a scalar field, or null when the field is absent or empty.</summary>
    private static string? Optional(SeatItem item, string key, string where)
    {
        if (!item.Fields.TryGetValue(key, out SeatField? field))
            return null;

        if (!field.IsText)
            throw new SeatOutputException($"{where} has a '{key}' that is not text.");

        string? value = field.Text?.TrimEnd();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    [GeneratedRegex(
        @"^Discovery:\s*codegraph\s+(?<codegraph>\d+)\s*,\s*docs\s+(?<docs>\d+)\s*,\s*direct reads\s+(?<reads>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex DiscoveryPattern();

    /// <summary>A list item that opens a finding with any field of the seat schema.</summary>
    [GeneratedRegex(
        @"^\s*-\s+(id|lens|severity|confidence|file|line|excerpt|claim|evidence|failure_scenario|suggestion|guidance):",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex FindingItemStart();
}

/// <summary>One line of a seat's output and its one-based line number in the file.</summary>
/// <param name="Text">The line, without its line ending.</param>
/// <param name="Number">The one-based line number in the seat file.</param>
internal sealed record SeatLine(string Text, int Number);

/// <summary>One finding as read from the seat's YAML, before its fields are checked.</summary>
/// <param name="Line">The file line the finding starts on.</param>
/// <param name="Column">The column the finding starts at.</param>
/// <param name="Fields">The finding's fields by key.</param>
internal sealed record SeatItem(int Line, int Column, IReadOnlyDictionary<string, SeatField> Fields);

/// <summary>One field's value as read from the seat's YAML.</summary>
/// <param name="Text">The value text, or null when the field is empty or not text.</param>
/// <param name="IsText">Whether the value is text rather than a list or a mapping.</param>
/// <param name="IsFlow">Whether strict YAML read the value as a flow collection, such as <c>[Fact]</c>.</param>
internal sealed record SeatField(string? Text, bool IsText, bool IsFlow = false);

/// <summary>A council seat's output could not be read; the message says why.</summary>
public sealed class SeatOutputException : FormatException
{
    /// <summary>Creates the error with no message.</summary>
    public SeatOutputException()
    {
    }

    /// <summary>Creates the error with the reason the output could not be read.</summary>
    /// <param name="message">The reason.</param>
    public SeatOutputException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the error with the reason and the failure that caused it.</summary>
    /// <param name="message">The reason.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SeatOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What one council seat produced.</summary>
/// <param name="Lens">The lens the seat ran.</param>
/// <param name="Kind">Whether the seat reported findings, no findings, or was skipped.</param>
/// <param name="Findings">The findings the seat reported, empty when none.</param>
/// <param name="Attestation">The seat's attestation line, when it wrote one.</param>
/// <param name="IndexFallback">Why the seat fell back from the code index, when it did.</param>
/// <param name="Discovery">How the seat found its evidence, when it recorded it.</param>
public sealed record SeatOutput(
    string Lens,
    SeatOutputKind Kind,
    IReadOnlyList<ReviewFinding> Findings,
    string? Attestation,
    string? IndexFallback,
    SeatDiscovery? Discovery);

/// <summary>The outcome a council seat reports.</summary>
public enum SeatOutputKind
{
    /// <summary>The seat reported one or more findings.</summary>
    Findings,

    /// <summary>The seat ran and reported no findings.</summary>
    NoFindings,

    /// <summary>The seat did not run its lens.</summary>
    Skipped
}

/// <summary>How many lookups a seat made through each discovery route.</summary>
/// <param name="CodeGraph">Lookups answered by the code index.</param>
/// <param name="Docs">Lookups answered by the documentation index.</param>
/// <param name="DirectReads">Lookups answered by reading files directly.</param>
public sealed record SeatDiscovery(int CodeGraph, int Docs, int DirectReads);
