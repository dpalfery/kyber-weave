using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Validation;

namespace KyberWeave.Core.Skills.Authoring;

/// <summary>One line of the governance inventory <c>skill catalog</c> prints.</summary>
/// <param name="Name">The skill's name, or its directory name when the front matter names none.</param>
/// <param name="Version">The <c>version</c> metadata, or an em dash.</param>
/// <param name="Author">The <c>author</c> metadata, or an em dash.</param>
/// <param name="Score">The description routing score, 0 to 100.</param>
/// <param name="Tokens">The approximate token count of the skill body.</param>
/// <param name="Resources">How many bundled files the skill carries.</param>
public sealed record SkillCatalogRow(string Name, string Version, string Author, int Score, int Tokens, int Resources)
{
    /// <summary>The inventory of <paramref name="set"/>, ordered by name.</summary>
    /// <param name="set">The skills to inventory.</param>
    public static IReadOnlyList<SkillCatalogRow> Of(SkillSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        return set.Skills.Select(s =>
        {
            Dictionary<string, string>? meta = s.Frontmatter.Metadata;
            return new SkillCatalogRow(
                s.Frontmatter.Name ?? s.DirectoryName,
                meta is not null && meta.TryGetValue("version", out string? v) ? v : "—",
                meta is not null && meta.TryGetValue("author", out string? a) ? a : "—",
                DescriptionScorer.Score(s).Total,
                s.ApproximateBodyTokens,
                s.Resources.Count);
        }).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }
}
