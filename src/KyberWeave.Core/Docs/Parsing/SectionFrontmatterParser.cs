using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Parsing;

namespace KyberWeave.Core.Docs.Parsing;

/// <summary>
///     Reads <c>lead</c> and <c>sections</c> frontmatter onto <see cref="DocumentFrontmatter" />.
/// </summary>
internal static class SectionFrontmatterParser
{
    private const string PreamblePlaceholder = "__kyber_preamble_slug__";

    internal static void Apply(string yaml, DocumentFrontmatter frontmatter)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return;
        }

        string normalized = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);
        string patched = normalized
            .Replace("\"#\":", PreamblePlaceholder + ":", StringComparison.Ordinal)
            .Replace("'#':", PreamblePlaceholder + ":", StringComparison.Ordinal);

        SectionFrontmatterOverlay overlay;
        try
        {
            overlay = MarkdownFrontmatterReader.Deserializer.Deserialize<SectionFrontmatterOverlay>(patched);
        }
        catch (Exception)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(overlay.Lead))
        {
            frontmatter.Lead = overlay.Lead.Trim();
        }

        if (overlay.Sections is not { Count: > 0 })
        {
            return;
        }

        Dictionary<string, DocumentSectionVocabulary> sections = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, DocumentSectionVocabulary> entry in overlay.Sections)
        {
            string slug = string.Equals(entry.Key, PreamblePlaceholder, StringComparison.Ordinal) ? "#" : entry.Key;
            sections[slug] = entry.Value;
        }

        frontmatter.Sections = sections;
    }

    private sealed class SectionFrontmatterOverlay
    {
        public string? Lead { get; init; }

        public Dictionary<string, DocumentSectionVocabulary>? Sections { get; init; }
    }
}
