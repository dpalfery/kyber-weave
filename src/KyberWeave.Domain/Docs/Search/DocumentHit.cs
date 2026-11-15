using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Search;

/// <summary>A ranked retrieval result.</summary>
/// <param name="Document">The document that matched.</param>
/// <param name="Score">Relevance, higher is better.</param>
/// <param name="Excerpt">The prose returned for this document.</param>
/// <param name="CodeJoins">That document's resolved joins to the code graph.</param>
public sealed record DocumentHit(
    DocumentModel Document,
    double Score,
    DocumentExcerpt Excerpt,
    IReadOnlyList<CodeJoin> CodeJoins);
