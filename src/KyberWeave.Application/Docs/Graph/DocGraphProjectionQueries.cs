namespace KyberWeave.Core.Docs.Graph;

/// <summary>Relationship lookups over a built <see cref="DocGraphProjection"/>.</summary>
public static class DocGraphProjectionQueries
{
    /// <summary>
    /// True when two documents share a projected concept, direct document relationship,
    /// overlapping source root, or approved one-hop CodeGraph relationship.
    /// </summary>
    public static bool AreDocumentsRelated(
        this DocGraphProjection projection,
        string leftDocumentId,
        string rightDocumentId)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentException.ThrowIfNullOrWhiteSpace(leftDocumentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightDocumentId);

        if (StringComparer.Ordinal.Equals(leftDocumentId, rightDocumentId))
            return projection.DocumentIds.Contains(leftDocumentId);

        return projection.RelatedDocumentIds.TryGetValue(leftDocumentId, out IReadOnlySet<string>? related)
               && related.Contains(rightDocumentId);
    }

    /// <summary>
    /// Returns the pre-indexed graph neighborhood for a document. The document itself is
    /// not included; callers that compare claims within one document handle that bucket
    /// directly.
    /// </summary>
    public static IReadOnlySet<string> GetRelatedDocumentIds(
        this DocGraphProjection projection,
        string documentId)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return projection.RelatedDocumentIds.TryGetValue(documentId, out IReadOnlySet<string>? related)
            ? related
            : new HashSet<string>(StringComparer.Ordinal);
    }
}
