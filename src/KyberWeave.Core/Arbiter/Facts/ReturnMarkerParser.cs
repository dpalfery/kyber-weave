using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// Reads the markers a returning worker's report carries, for the ledger's
/// <c>returns</c> field. The worker writes them; no agent writes the ledger (D3).
/// </summary>
internal static partial class ReturnMarkerParser
{
    /// <summary>
    /// Parses <paramref name="output"/>, which is the report text or a harness's JSON
    /// envelope around it. A marker that appears more than once takes its last value,
    /// because the digest closes a report and earlier text may quote the template.
    /// </summary>
    /// <param name="output">The dispatch output, if the event has one.</param>
    /// <param name="readAttestations">
    /// Whether <c>ATTESTED:</c> counts. Only a planner return may attest (D31).
    /// </param>
    /// <returns>The markers, or null when the output carries none.</returns>
    public static ArbiterReturnMarkers? Parse(string? output, bool readAttestations)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string text = Flatten(output);
        string? review = LastGroup(ResultRegex(), text);
        string? status = LastGroup(StatusRegex(), text);
        IReadOnlyList<string>? attested = readAttestations ? ReadAttested(text) : null;
        return review is null && status is null && attested is null
            ? null
            : new ArbiterReturnMarkers(review, status, attested);
    }

    private static IReadOnlyList<string>? ReadAttested(string text)
    {
        string? line = LastGroup(AttestedRegex(), text);
        if (line is null)
        {
            return null;
        }

        List<string> pairs = [.. line
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        return pairs.Count == 0 ? null : pairs;
    }

    private static string? LastGroup(Regex regex, string text)
    {
        MatchCollection matches = regex.Matches(text);
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value.Trim();
    }

    /// <summary>
    /// A foreground harness hands back its tool response as JSON, where a report's line
    /// breaks are escaped. Every string in it is joined so a marker starts a line again.
    /// </summary>
    internal static string Flatten(string output)
    {
        string trimmed = output.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return output;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            StringBuilder builder = new();
            AppendStrings(document.RootElement, builder);
            return builder.ToString();
        }
        catch (JsonException)
        {
            return output;
        }
    }

    private static void AppendStrings(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                builder.Append(element.GetString()).Append('\n');
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AppendStrings(item, builder);
                }

                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    AppendStrings(property.Value, builder);
                }

                break;
        }
    }

    [GeneratedRegex(@"^RESULT:\s+(PASS|FAIL)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ResultRegex();

    [GeneratedRegex(@"^STATUS:[ \t]*(\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StatusRegex();

    [GeneratedRegex(@"^ATTESTED:[ \t]*(.+?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AttestedRegex();
}
