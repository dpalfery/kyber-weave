using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>Writes a <see cref="BriefResult"/> as the text a model reads, or as JSON.</summary>
public static class BriefFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// The text form: a one-line header naming the facts used and the sections left out, a diagnostics line
    /// only when something is unmapped, missing or unlisted, then each included file under a rule line.
    /// </summary>
    public static string ToText(BriefResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        StringBuilder text = new();
        text.Append("BRIEF ").Append(result.Agent).Append(" | facts: ").Append(FactsLine(result.Facts));
        text.Append(" | left out: ").Append(LeftOutLine(result));
        text.Append(" | ").Append(result.BriefCharacters.ToString(CultureInfo.InvariantCulture))
            .Append(" of ").Append(result.FullCharacters.ToString(CultureInfo.InvariantCulture)).Append(" characters\n");

        string diagnostics = DiagnosticsLine(result);
        if (diagnostics.Length > 0)
        {
            text.Append("BRIEF diagnostics | ").Append(diagnostics).Append('\n');
        }

        foreach (BriefPart part in result.Parts.Where(part => part.WholeFileLeftOut is null))
        {
            text.Append("\n=== ").Append(part.File).Append(" ===\n\n").Append(part.Text);
        }

        return text.ToString();
    }

    /// <summary>The JSON form, with the text and every diagnostic as fields.</summary>
    public static string ToJson(BriefResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(
            new
            {
                agent = result.Agent,
                planStatus = result.Facts.PlanStatus,
                facts = BriefFacts.Names.ToDictionary(
                    name => name,
                    name => new { value = result.Facts.Get(name), source = Source(result.Facts, name) }),
                characters = new { full = result.FullCharacters, brief = result.BriefCharacters },
                files = result.Parts.Select(part => new
                {
                    file = part.File,
                    readFrom = part.ReadFrom,
                    included = part.WholeFileLeftOut is null,
                    leftOutBecause = part.WholeFileLeftOut,
                    leftOut = part.LeftOut.Select(item => new { heading = item.Heading, condition = item.Condition }),
                    unmapped = part.Unmapped,
                    missingFromFile = part.MissingFromFile,
                    characters = part.Text.Length,
                    fullCharacters = part.FullCharacters,
                }),
                unlistedFiles = result.UnlistedFiles,
                text = ToText(result),
            },
            JsonOptions);
    }

    private static string FactsLine(BriefFacts facts)
    {
        IEnumerable<string> known = BriefFacts.Names.Select(name =>
            facts.Get(name) is { } value
                ? $"{name}={value} ({Source(facts, name)})"
                : $"{name}=unknown (all kept)");
        return string.Join(", ", facts.PlanStatus is null ? known : known.Append($"plan-status={facts.PlanStatus}"));
    }

    private static string Source(BriefFacts facts, string name) =>
        facts.Sources is not null && facts.Sources.TryGetValue(name, out string? source) ? source : "given";

    private static string LeftOutLine(BriefResult result)
    {
        List<string> items = [];
        foreach (BriefPart part in result.Parts)
        {
            if (part.WholeFileLeftOut is not null)
            {
                items.Add($"{part.File} ({part.WholeFileLeftOut})");
            }

            items.AddRange(part.LeftOut.Select(item => $"{part.File} § {item.Heading} ({item.Condition})"));
        }

        return items.Count == 0 ? "nothing" : string.Join("; ", items);
    }

    private static string DiagnosticsLine(BriefResult result)
    {
        List<string> items = [];
        foreach (BriefPart part in result.Parts)
        {
            items.AddRange(part.Unmapped.Select(heading => $"unmapped, included: {part.File} § {heading}"));
            items.AddRange(part.MissingFromFile.Select(heading => $"mapped heading not in file: {part.File} § {heading}"));
        }

        items.AddRange(result.UnlistedFiles.Select(file => $"not in the map, not in the brief, read whole: {file}"));
        return string.Join("; ", items);
    }
}
