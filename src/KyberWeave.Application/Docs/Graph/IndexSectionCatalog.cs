using System.Globalization;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Graph;

/// <summary>
/// Inventory sections whose body is a link table, not answer text (plan D18).
/// </summary>
internal static class IndexSectionCatalog
{
    private sealed record Entry(string PathSuffix, string Heading);

    private static readonly Entry[] Entries =
    [
        new($"{DocsLayout.Plans}/README.md", "Active Plans"),
        new($"{DocsLayout.Plans}/README.md", "Archived Plans"),
        new($"{OntologyConfig.ArchiveSegment}/{DocsLayout.Plans}/README.md", "Archived Plans"),
        new($"{DocsLayout.Adr}/README.md", "Inventory"),
        new($"{DocsLayout.Todos}/README.md", "Todo inventory"),
        new("kyber-squad/skill-resource-dispositions.md", "Policy-line ledger"),
        new($"{DocsLayout.Reference}/README.md", "Inventory")
    ];

    internal static bool IsIndexSection(string relativePath, string? sectionHeading)
    {
        if (string.IsNullOrWhiteSpace(sectionHeading))
        {
            return false;
        }

        string normalized = NormalizeRelativePath(relativePath);
        foreach (Entry entry in Entries)
        {
            if (PathMatches(normalized, entry.PathSuffix)
                && string.Equals(sectionHeading, entry.Heading, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace('\\', '/').Trim();
    }

    private static bool PathMatches(string normalizedPath, string pathSuffix)
    {
        return string.Equals(normalizedPath, pathSuffix, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.EndsWith(
                   string.Create(CultureInfo.InvariantCulture, $"/{pathSuffix}"),
                   StringComparison.OrdinalIgnoreCase);
    }
}
