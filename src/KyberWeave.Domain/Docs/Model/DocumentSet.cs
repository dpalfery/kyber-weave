namespace KyberWeave.Core.Docs.Model;

/// <summary>
/// The parsed documentation corpus, plus the catalog vocabularies its documents are
/// validated against.
/// </summary>
public sealed class DocumentSet
{
    public required IReadOnlyList<DocumentModel> Documents { get; init; }

    /// <summary>Component names from the catalog's Component column.</summary>
    public IReadOnlySet<string> Components { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Owner names from the catalog's Owner column.</summary>
    public IReadOnlySet<string> Owners { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}
