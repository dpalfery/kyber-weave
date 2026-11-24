using System.Text.Json;

namespace KyberDash.Data.Collector;

/// <summary>
/// Extracts invoked skill names from canonical raw attributes. Shared by the ingest pipeline and
/// the OTLP log mapper so both ingestion paths recognize, trim, and deduplicate the same skill
/// set for equivalent attributes.
/// </summary>
public static class SkillExtraction
{
    private static readonly string[] SkillAttributeKeys =
    [
        "gen_ai.skill.name",
        "skill.name",
        "skill_name",
        "kyber.skills.invoked",
    ];

    private static readonly string[] ToolNameKeys =
    [
        "gen_ai.tool.name",
        "opencode.tool.name",
        "factory.tool.name",
        "tool.name",
        "tool_name",
    ];

    private static readonly string[] ToolArgumentKeys =
    [
        "gen_ai.tool.call.arguments",
        "gen_ai.tool.arguments",
        "tool.arguments",
        "tool_arguments",
        "arguments",
        "input",
        "opencode.tool.input",
        "tool.input",
    ];

    private static readonly string[] SkillArgumentPropertyKeys = ["skill", "skill_name", "name"];

    /// <summary>
    /// Collects the invoked skill names on a record: every skill attribute element's names, plus
    /// the skill a <c>Skill</c> tool named in its arguments. <paramref name="resolvedToolName"/>
    /// is the tool name the caller already resolved; without one the attributes are searched.
    /// </summary>
    /// <param name="attributes">Raw span or log attributes; null or empty yields no skills.</param>
    /// <param name="resolvedToolName">The tool name known to the caller, if any.</param>
    /// <returns>The invoked skill names in encounter order, deduplicated.</returns>
    public static IReadOnlyList<string> ExtractInvoked(
        IReadOnlyDictionary<string, JsonElement>? attributes,
        string? resolvedToolName = null)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return [];
        }

        List<string> skills = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string key in SkillAttributeKeys)
        {
            if (attributes.TryGetValue(key, out JsonElement element))
            {
                AddSkillsFromElement(element, skills, seen);
            }
        }

        string? toolName = !string.IsNullOrWhiteSpace(resolvedToolName)
            ? resolvedToolName
            : ExtractSafeAttributeString(attributes, ToolNameKeys);
        if (string.Equals(toolName, "Skill", StringComparison.OrdinalIgnoreCase))
        {
            ExtractSkillFromToolArguments(attributes, skills, seen);
        }

        return skills;
    }

    private static string? ExtractSafeAttributeString(
        IReadOnlyDictionary<string, JsonElement>? attributes,
        IReadOnlyList<string> keys)
    {
        if (attributes is null)
        {
            return null;
        }

        foreach (string key in keys)
        {
            if (attributes.TryGetValue(key, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? text = el.GetString();
                if (!string.IsNullOrWhiteSpace(text) && !JsonDocumentText.IsDocument(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static void AddSkillsFromElement(JsonElement element, List<string> skills, HashSet<string> seen)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            string trimmed = raw.Trim();
            if (trimmed.StartsWith('['))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in doc.RootElement.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                TryAddSkill(item.GetString(), skills, seen);
                            }
                        }

                        return;
                    }
                }
                catch (JsonException)
                {
                    return;
                }
            }

            TryAddSkill(trimmed, skills, seen);
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    TryAddSkill(item.GetString(), skills, seen);
                }
            }
        }
    }

    private static void ExtractSkillFromToolArguments(
        IReadOnlyDictionary<string, JsonElement> attributes,
        List<string> skills,
        HashSet<string> seen)
    {
        foreach (string key in ToolArgumentKeys)
        {
            if (!attributes.TryGetValue(key, out JsonElement element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                AddSkillPropertyFromObject(element, skills, seen);
                return;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                string? raw = element.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string trimmed = raw.Trim();
                if (!trimmed.StartsWith('{'))
                {
                    continue;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        AddSkillPropertyFromObject(doc.RootElement, skills, seen);
                        return;
                    }
                }
                catch (JsonException)
                {
                    return;
                }
            }
        }
    }

    private static void AddSkillPropertyFromObject(JsonElement obj, List<string> skills, HashSet<string> seen)
    {
        foreach (string propKey in SkillArgumentPropertyKeys)
        {
            if (obj.TryGetProperty(propKey, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
            {
                if (TryAddSkill(prop.GetString(), skills, seen))
                {
                    return;
                }
            }
        }
    }

    private static bool TryAddSkill(string? raw, List<string> skills, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(raw) || JsonDocumentText.IsDocument(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (seen.Add(trimmed))
        {
            skills.Add(trimmed);
        }

        return true;
    }
}
