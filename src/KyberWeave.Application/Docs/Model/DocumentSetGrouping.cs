namespace KyberWeave.Core.Docs.Model;

/// <summary>Groupings over a <see cref="DocumentSet"/>.</summary>
public static class DocumentSetGrouping
{
    /// <summary>doc-type by component, for the catalog command.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<DocumentModel>> ByComponent(this DocumentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return set.Documents
            .GroupBy(d => d.Frontmatter.Component ?? "(none)", StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<DocumentModel>)g.OrderBy(d => d.RelativePath, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
    }
}
