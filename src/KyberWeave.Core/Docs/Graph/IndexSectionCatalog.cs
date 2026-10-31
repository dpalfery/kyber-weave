namespace KyberWeave.Core.Docs.Graph;

/// <summary>
/// Inventory sections whose body is a link table, not answer text (plan D18).
/// </summary>
internal static class IndexSectionCatalog
{
    private sealed record Entry(string PathSuffix, string Heading);

    private static readonly Entry[] Entries =
    [
        new("plans/README.md", "Active Plans"),
        new("plans/README.md", "Archived Plans"),
        new("adr/README.md", "Inventory"),
        new("todo/README.md", "Todo inventory"),
        new("kyber-squad/skill-resource-dispositions.md", "Policy-line ledger"),
        new("reference/README.md", "Inventory")
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
            if (normalized.EndsWith(entry.PathSuffix, StringComparison.OrdinalIgnoreCase)
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
}
