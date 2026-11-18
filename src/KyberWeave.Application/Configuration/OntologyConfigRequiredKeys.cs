using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Configuration;

/// <summary>Which frontmatter keys an <see cref="OntologyConfig"/> requires.</summary>
public static class OntologyConfigRequiredKeys
{
    /// <summary>Whether <paramref name="key"/> is required of every document.</summary>
    public static bool IsRequiredForAll(this OntologyConfig config, string key)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return config.BaseRequiredKeys.Contains(key, StringComparer.Ordinal);
    }

    /// <summary>Whether <paramref name="key"/> is required of a document of <paramref name="docType"/>, beyond the base set.</summary>
    public static bool IsRequired(this OntologyConfig config, DocType docType, string key)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!config.RequiredKeysByType.TryGetValue(docType, out IReadOnlyList<string>? keys))
            return false;

        return keys.Contains(key, StringComparer.Ordinal);
    }
}
