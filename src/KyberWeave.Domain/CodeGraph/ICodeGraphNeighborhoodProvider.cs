namespace KyberWeave.Core.CodeGraph;

/// <summary>
/// Optional CodeGraph port for batched, one-hop relationships between resolved nodes.
/// </summary>
/// <remarks>
/// This is separate from <see cref="ICodeGraphResolver"/> so existing resolver
/// implementations remain source-compatible. Consumers feature-detect this port and
/// retain document-only relationships when it is unavailable.
/// </remarks>
public interface ICodeGraphNeighborhoodProvider
{
    /// <summary>
    /// Returns approved edges between the requested nodes, excluding every node whose
    /// total approved-edge degree exceeds <paramref name="maxDegree"/>.
    /// </summary>
    IReadOnlyList<CodeGraphEdge> GetEdges(
        IReadOnlyCollection<string> nodeIds,
        int maxDegree);
}
