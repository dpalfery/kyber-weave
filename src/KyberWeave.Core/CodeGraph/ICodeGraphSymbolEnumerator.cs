namespace KyberWeave.Core.CodeGraph;

/// <summary>
/// Optional CodeGraph port for enumerating every indexed symbol of one kind.
/// </summary>
/// <remarks>
/// Separate from <see cref="ICodeGraphResolver"/> for the same reason
/// <see cref="ICodeGraphNeighborhoodProvider"/> is: existing resolver implementations stay
/// source-compatible, and consumers feature-detect the port rather than requiring it.
/// <para>
/// The resolver's own lookups are all name-anchored — you ask about a symbol you already
/// know. Duplicate detection has no such anchor: the question is which bodies in the whole
/// index match each other, so it needs the population rather than a lookup.
/// </para>
/// </remarks>
public interface ICodeGraphSymbolEnumerator
{
    /// <summary>
    /// Every indexed node of the given kind, in index order. Returns an empty list when the
    /// index is unavailable or holds no node of that kind.
    /// </summary>
    IReadOnlyList<CodeGraphNode> NodesOfKind(string kind);
}
