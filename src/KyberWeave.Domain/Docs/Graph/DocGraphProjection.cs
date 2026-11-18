using System.Collections.ObjectModel;

namespace KyberWeave.Core.Docs.Graph;

/// <summary>
/// Immutable, reusable projection of document identity, ownership, links, and code joins.
/// </summary>
public sealed class DocGraphProjection
{
    public DocGraphProjection(
        IReadOnlyList<DocGraphNode> nodes,
        IReadOnlyList<DocGraphEdge> edges,
        IReadOnlySet<string> documentIds,
        IReadOnlyDictionary<string, IReadOnlySet<string>> relatedDocumentIds)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(documentIds);
        ArgumentNullException.ThrowIfNull(relatedDocumentIds);

        Nodes = new ReadOnlyCollection<DocGraphNode>(nodes.ToArray());
        Edges = new ReadOnlyCollection<DocGraphEdge>(edges.ToArray());
        DocumentIds = new ReadOnlySet<string>(
            new HashSet<string>(documentIds, StringComparer.Ordinal));
        RelatedDocumentIds = new ReadOnlyDictionary<string, IReadOnlySet<string>>(
            relatedDocumentIds.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<string>)new ReadOnlySet<string>(
                    new HashSet<string>(pair.Value, StringComparer.Ordinal)),
                StringComparer.Ordinal));
    }

    /// <summary>Snapshot of exportable DocGraph nodes.</summary>
    public IReadOnlyList<DocGraphNode> Nodes { get; }

    /// <summary>Snapshot of exportable DocGraph edges.</summary>
    public IReadOnlyList<DocGraphEdge> Edges { get; }

    /// <summary>The ids of the projected document nodes.</summary>
    public IReadOnlySet<string> DocumentIds { get; }

    /// <summary>
    /// For each document id, the ids of the other documents it shares a projected concept,
    /// direct relationship, overlapping source root, or approved one-hop CodeGraph
    /// relationship with. A document does not list itself.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> RelatedDocumentIds { get; }
}
