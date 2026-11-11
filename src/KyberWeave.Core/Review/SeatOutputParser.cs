using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Review;

/// <summary>Parses one council seat's output file into its findings and status.</summary>
/// <remarks>
/// <para>
/// A seat writes for a reader, not for a serializer: a YAML list of findings, usually in a code fence and
/// sometimes not, followed by closing lines such as the <c>REVIEW.md sections checked:</c> attestation. Real
/// seats put those closing lines inside the fence as often as outside it, so they are lifted out by their
/// prefix before the YAML is read rather than by their position.
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
    private const string Fence = "```";

    /// <summary>Parses the text a seat wrote for one lens.</summary>
    /// <param name="lens">The lens the seat ran, taken from the file name.</param>
    /// <param name="text">The seat's output file contents.</param>
    /// <exception cref="SeatOutputException">The text holds no recognisable outcome, or its YAML cannot be read.</exception>
    public static SeatOutput Parse(string lens, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lens);
        ArgumentNullException.ThrowIfNull(text);

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        string? attestation = null;
        string? indexFallback = null;
        SeatDiscovery? discovery = null;
        SeatOutputKind? declared = null;
        List<string> body = [];

        // Closing lines are recognised only at the start of a line. An indented one is inside a finding's
        // excerpt or evidence, and lifting it out would silently change what the seat wrote.
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (line.StartsWith(AttestationPrefix, StringComparison.Ordinal))
                attestation = trimmed;
            else if (line.StartsWith(IndexFallbackPrefix, StringComparison.Ordinal))
                indexFallback = trimmed[IndexFallbackPrefix.Length..].Trim();
            else if (line.StartsWith(DiscoveryPrefix, StringComparison.Ordinal))
                discovery = ReadDiscovery(trimmed);
            else if (line.StartsWith(NoFindingsPrefix, StringComparison.Ordinal))
                declared ??= SeatOutputKind.NoFindings;
            else if (line.StartsWith(SkippedPrefix, StringComparison.Ordinal))
                declared ??= SeatOutputKind.Skipped;
            else
                body.Add(line);
        }

        // A seat that says it found nothing, or did not run, writes prose after that line, not YAML.
        if (declared is { } kind)
            return new SeatOutput(lens, kind, [], attestation, indexFallback, discovery);

        IReadOnlyList<ReviewFinding> findings = ReadFindings(lens, YamlText(body));
        if (findings.Count == 0)
        {
            throw new SeatOutputException(
                $"The {lens} seat output has no findings list, no '{NoFindingsPrefix}' line and no " +
                $"'{SkippedPrefix}' line.");
        }

        return new SeatOutput(lens, SeatOutputKind.Findings, findings, attestation, indexFallback, discovery);
    }

    /// <summary>
    /// The YAML a seat wrote. When the seat fenced its list, only fenced lines are read, so a sentence of
    /// prose before the fence is not mistaken for YAML; an unfenced seat is read whole.
    /// </summary>
    private static string YamlText(List<string> body)
    {
        bool fenced = body.Exists(line => line.TrimStart().StartsWith(Fence, StringComparison.Ordinal));
        if (!fenced)
            return string.Join('\n', body);

        List<string> inside = [];
        bool open = false;
        foreach (string line in body)
        {
            if (line.TrimStart().StartsWith(Fence, StringComparison.Ordinal))
            {
                open = !open;
                continue;
            }

            if (open)
                inside.Add(line);
        }

        return string.Join('\n', inside);
    }

    /// <summary>
    /// The counts on a discovery line, or null when the line is not in the agreed shape. A malformed line reads
    /// as no discovery evidence, which KW-REVIEW-044 reports, rather than as an unreadable seat.
    /// </summary>
    private static SeatDiscovery? ReadDiscovery(string line)
    {
        Match match = DiscoveryPattern().Match(line);
        if (!match.Success)
            return null;

        return new SeatDiscovery(
            int.Parse(match.Groups["codegraph"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["docs"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["reads"].Value, CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<ReviewFinding> ReadFindings(string seatLens, string yaml)
    {
        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        // YamlDotNet reports some malformed flow collections, such as an unclosed '[', as an invalid operation
        // rather than a YamlException, so both are the seat's YAML being unreadable.
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            throw new SeatOutputException($"The {seatLens} seat output is not valid YAML: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0)
            return [];

        if (stream.Documents[0].RootNode is not YamlSequenceNode sequence)
            throw new SeatOutputException($"The {seatLens} seat output is not a YAML list of findings.");

        List<ReviewFinding> findings = new(sequence.Children.Count);
        for (int index = 0; index < sequence.Children.Count; index++)
        {
            if (sequence.Children[index] is not YamlMappingNode mapping)
            {
                throw new SeatOutputException(
                    $"Finding {index + 1} in the {seatLens} seat output is not a mapping of fields.");
            }

            findings.Add(ReadFinding(seatLens, index + 1, mapping));
        }

        return findings;
    }

    private static ReviewFinding ReadFinding(string seatLens, int position, YamlMappingNode mapping)
    {
        string where = $"Finding {position} in the {seatLens} seat output";
        string id = Required(mapping, "id", where);
        where = $"Finding '{id}' in the {seatLens} seat output";

        string lens = Optional(mapping, "lens", where) is { Length: > 0 } written
            ? written
            : LensFromId(id, seatLens);

        string severityText = Required(mapping, "severity", where);
        if (!Enum.TryParse(severityText, ignoreCase: true, out ReviewSeverity severity)
            || !Enum.IsDefined(severity)
            || int.TryParse(severityText, CultureInfo.InvariantCulture, out _))
        {
            throw new SeatOutputException($"{where} has severity '{severityText}'; use critical, major or minor.");
        }

        return new ReviewFinding(
            id,
            lens,
            severity,
            RequiredNumber(mapping, "confidence", where),
            Required(mapping, "file", where),
            RequiredNumber(mapping, "line", where),
            Optional(mapping, "excerpt", where),
            Optional(mapping, "claim", where),
            Optional(mapping, "evidence", where),
            Optional(mapping, "failure_scenario", where) ?? Optional(mapping, "failureScenario", where),
            Optional(mapping, "suggestion", where));
    }

    private static string LensFromId(string id, string seatLens)
    {
        int slash = id.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? id[..slash] : seatLens;
    }

    private static string Required(YamlMappingNode mapping, string key, string where) =>
        Optional(mapping, key, where) is { Length: > 0 } value
            ? value
            : throw new SeatOutputException($"{where} has no '{key}'.");

    private static int RequiredNumber(YamlMappingNode mapping, string key, string where)
    {
        string value = Required(mapping, key, where);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : throw new SeatOutputException($"{where} has '{key}: {value}', which is not a whole number.");
    }

    /// <summary>The trimmed text of a scalar field, or null when the field is absent or empty.</summary>
    private static string? Optional(YamlMappingNode mapping, string key, string where)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node))
            return null;

        if (node is not YamlScalarNode scalar)
            throw new SeatOutputException($"{where} has a '{key}' that is not text.");

        string? value = scalar.Value?.TrimEnd();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    [GeneratedRegex(
        @"^Discovery:\s*codegraph\s+(?<codegraph>\d+)\s*,\s*docs\s+(?<docs>\d+)\s*,\s*direct reads\s+(?<reads>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex DiscoveryPattern();
}

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
