namespace KyberWeave.Core.Docs.Graph;

/// <summary>Additional nodes and edges contributed to a DocGraph projection.</summary>
public sealed record DocGraphContribution(
    IReadOnlyList<DocGraphNode> Nodes,
    IReadOnlyList<DocGraphEdge> Edges);
