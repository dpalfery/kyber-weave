using System.Globalization;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Parsing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Docs.Parsing;

/// <summary>
///     Reads <c>lead</c> and <c>sections</c> frontmatter onto <see cref="DocumentFrontmatter" />.
/// </summary>
internal static class SectionFrontmatterParser
{
    private const string PreamblePlaceholder = "__kyber_preamble_slug__";

    internal static string? Apply(string yaml, DocumentFrontmatter frontmatter)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null;
        }

        string normalized = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);
        string? shapeError = ValidateSectionsShape(normalized);
        if (shapeError is not null)
        {
            return shapeError;
        }

        string patched = normalized
            .Replace("\"#\":", PreamblePlaceholder + ":", StringComparison.Ordinal)
            .Replace("'#':", PreamblePlaceholder + ":", StringComparison.Ordinal);

        SectionFrontmatterOverlay overlay;
        try
        {
            overlay = MarkdownFrontmatterReader.Deserializer.Deserialize<SectionFrontmatterOverlay>(patched);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(overlay.Lead))
        {
            frontmatter.Lead = overlay.Lead.Trim();
        }

        if (overlay.Sections is not { Count: > 0 })
        {
            return null;
        }

        Dictionary<string, DocumentSectionVocabulary> sections = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, DocumentSectionVocabulary> entry in overlay.Sections)
        {
            string slug = string.Equals(entry.Key, PreamblePlaceholder, StringComparison.Ordinal) ? "#" : entry.Key;
            sections[slug] = entry.Value;
        }

        frontmatter.Sections = sections;
        return null;
    }

    private static string? ValidateSectionsShape(string yaml)
    {
        try
        {
            YamlStream stream = new();
            using StringReader reader = new(yaml);
            stream.Load(reader);
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return null;
            }

            if (!root.Children.TryGetValue(new YamlScalarNode("sections"), out YamlNode? sectionsNode))
            {
                return null;
            }

            if (sectionsNode is YamlScalarNode)
            {
                return "sections must be a mapping keyed by section slug, not a scalar.";
            }

            if (sectionsNode is not YamlMappingNode sectionsMap)
            {
                return "sections must be a mapping keyed by section slug.";
            }

            foreach (KeyValuePair<YamlNode, YamlNode> entry in sectionsMap.Children)
            {
                if (entry.Value is not YamlMappingNode sectionMap)
                {
                    return "Each sections entry must be a mapping with optional keywords and code-refs.";
                }

                foreach (KeyValuePair<YamlNode, YamlNode> property in sectionMap.Children)
                {
                    if (property.Key is not YamlScalarNode { Value: { } key })
                    {
                        continue;
                    }

                    if (!string.Equals(key, "keywords", StringComparison.Ordinal)
                        && !string.Equals(key, "code-refs", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (property.Value is YamlSequenceNode)
                    {
                        continue;
                    }

                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"sections.{PlainScalar(entry.Key)}.{key} must be a list.");
                }
            }
        }
        catch (YamlException ex)
        {
            return ex.Message;
        }

        return null;
    }

    private static string PlainScalar(YamlNode node)
    {
        return node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : string.Empty;
    }

    private sealed class SectionFrontmatterOverlay
    {
        public string? Lead { get; init; }

        public Dictionary<string, DocumentSectionVocabulary>? Sections { get; init; }
    }
}
