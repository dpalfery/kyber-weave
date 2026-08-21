using System.Collections.ObjectModel;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Graph;

/// <summary>
/// Immutable, reusable projection of document identity, ownership, links, and code joins.
/// </summary>
public sealed class DocGraphProjection
{
    private static readonly HashSet<string> SharedTargetLabels =
        ["DOCUMENTS", "DESCRIBES", "REFERENCES", "EXPOSES"];

    private static readonly HashSet<string> DirectDocumentLabels =
        ["LINKS_TO", "DECIDED_BY", "SUPERSEDES"];

    private static readonly HashSet<string> TraversedCodeEdgeKinds =
        ["contains", "calls", "references", "instantiates", "extends", "implements"];

    private readonly IReadOnlySet<string> _documentIds;
    private readonly IReadOnlySet<DocumentPair> _relatedDocuments;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _relatedDocumentIds;

    private DocGraphProjection(
        IReadOnlyList<DocGraphNode> nodes,
        IReadOnlyList<DocGraphEdge> edges,
        IReadOnlySet<string> documentIds,
        IReadOnlySet<DocumentPair> relatedDocuments)
    {
        Nodes = new ReadOnlyCollection<DocGraphNode>(nodes.ToArray());
        Edges = new ReadOnlyCollection<DocGraphEdge>(edges.ToArray());
        _documentIds = new ReadOnlySet<string>(
            new HashSet<string>(documentIds, StringComparer.Ordinal));
        _relatedDocuments = new ReadOnlySet<DocumentPair>(
            new HashSet<DocumentPair>(relatedDocuments));
        _relatedDocumentIds = BuildRelationshipIndex(documentIds, relatedDocuments);
    }

    /// <summary>Snapshot of exportable DocGraph nodes.</summary>
    public IReadOnlyList<DocGraphNode> Nodes { get; }

    /// <summary>Snapshot of exportable DocGraph edges.</summary>
    public IReadOnlyList<DocGraphEdge> Edges { get; }

    /// <summary>Builds the shared projection and optional one-hop CodeGraph neighborhood.</summary>
    public static DocGraphProjection Build(
        DocumentSet documents,
        ICodeGraphResolver codeGraph,
        int maxCodeNeighbors = 50,
        IReadOnlyList<IDocGraphContributor>? contributors = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(codeGraph);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCodeNeighbors);

        List<DocGraphNode> nodes = new List<DocGraphNode>();
        List<DocGraphEdge> edges = new List<DocGraphEdge>();
        HashSet<string> emittedNodeIds = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> documentIds = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, string> pathToId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (DocumentModel document in documents.Documents)
            pathToId.TryAdd(document.RelativePath, DocId(document.Subject));

        foreach (DocumentModel document in documents.Documents)
        {
            // Claim.DocumentIdentity is document.Subject, which falls back to RelativePath
            // when frontmatter has no id. Register the same node so graph candidacy can
            // find intra-document and shared-concept pairs instead of querying a missing id.
            string documentId = DocId(document.Subject);
            documentIds.Add(documentId);

            AddNode(nodes, emittedNodeIds, new DocGraphNode(
                documentId,
                "Document",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["docType"] = document.DocType.ToString().ToLowerInvariant(),
                    ["status"] = document.Status.ToString().ToLowerInvariant(),
                    ["title"] = document.Frontmatter.Title,
                    ["path"] = document.RelativePath
                }));

            AddConcept(nodes, emittedNodeIds, "Component", document.Frontmatter.Component);
            AddConcept(nodes, emittedNodeIds, "Team", document.Frontmatter.Owner);

            AddEdge(edges, "DOCUMENTS", documentId,
                ConceptId("Component", document.Frontmatter.Component));
            AddEdge(edges, "OWNED_BY", documentId,
                ConceptId("Team", document.Frontmatter.Owner));

            if (!string.IsNullOrWhiteSpace(document.Frontmatter.SourceRoot))
                edges.Add(new DocGraphEdge(
                    "DESCRIBES", documentId, $"path:{document.Frontmatter.SourceRoot}"));

            foreach (string symbol in document.CodeRefs)
                foreach (CodeGraphNode node in codeGraph.ResolveSymbol(symbol))
                    edges.Add(new DocGraphEdge("REFERENCES", documentId, node.Id));

            foreach (string endpoint in document.ApiEndpoints)
                foreach (CodeGraphNode node in codeGraph.ResolveRoute(endpoint))
                    edges.Add(new DocGraphEdge("EXPOSES", documentId, node.Id));

            foreach (string adr in document.DecidedBy)
                edges.Add(new DocGraphEdge("DECIDED_BY", documentId, DocId(adr)));

            foreach (string superseded in document.Supersedes)
                edges.Add(new DocGraphEdge("SUPERSEDES", documentId, DocId(superseded)));

            foreach (string link in document.BodyLinks)
            {
                string? target = ResolveLink(document.RelativePath, link);
                if (target is not null
                    && pathToId.TryGetValue(target, out string? targetId)
                    && !StringComparer.Ordinal.Equals(targetId, documentId))
                {
                    edges.Add(new DocGraphEdge("LINKS_TO", documentId, targetId));
                }
            }
        }

        if (contributors is not null)
        {
            foreach (IDocGraphContributor contributor in contributors)
            {
                ArgumentNullException.ThrowIfNull(contributor);
                DocGraphContribution contribution = contributor.Contribute(documents, codeGraph)
                    ?? throw new InvalidOperationException("A DocGraph contributor returned null.");

                foreach (DocGraphNode node in contribution.Nodes)
                    AddNode(nodes, emittedNodeIds, Copy(node));
                foreach (DocGraphEdge edge in contribution.Edges)
                    edges.Add(edge with { });
            }
        }

        DocGraphEdge[] distinctEdges = edges.Distinct().ToArray();
        IReadOnlySet<DocumentPair> related = BuildDocumentRelationships(
            documentIds,
            distinctEdges,
            codeGraph,
            maxCodeNeighbors);

        return new DocGraphProjection(nodes, distinctEdges, documentIds, related);
    }

    /// <summary>
    /// True when two documents share a projected concept, direct document relationship,
    /// overlapping source root, or approved one-hop CodeGraph relationship.
    /// </summary>
    public bool AreDocumentsRelated(string leftDocumentId, string rightDocumentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftDocumentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightDocumentId);

        if (StringComparer.Ordinal.Equals(leftDocumentId, rightDocumentId))
            return _documentIds.Contains(leftDocumentId);

        return _relatedDocuments.Contains(DocumentPair.Create(leftDocumentId, rightDocumentId));
    }

    /// <summary>
    /// Returns the pre-indexed graph neighborhood for a document. The document itself is
    /// not included; callers that compare claims within one document handle that bucket
    /// directly.
    /// </summary>
    public IReadOnlySet<string> GetRelatedDocumentIds(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return _relatedDocumentIds.TryGetValue(documentId, out IReadOnlySet<string>? related)
            ? related
            : new ReadOnlySet<string>(new HashSet<string>(StringComparer.Ordinal));
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildRelationshipIndex(
        IReadOnlySet<string> documentIds,
        IReadOnlySet<DocumentPair> relationships)
    {
        Dictionary<string, HashSet<string>> index = documentIds.ToDictionary(
            documentId => documentId,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (DocumentPair relationship in relationships)
        {
            index[relationship.Left].Add(relationship.Right);
            index[relationship.Right].Add(relationship.Left);
        }

        return new ReadOnlyDictionary<string, IReadOnlySet<string>>(
            index.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<string>)new ReadOnlySet<string>(pair.Value),
                StringComparer.Ordinal));
    }

    private static IReadOnlySet<DocumentPair> BuildDocumentRelationships(
        IReadOnlySet<string> documentIds,
        IReadOnlyList<DocGraphEdge> edges,
        ICodeGraphResolver codeGraph,
        int maxCodeNeighbors)
    {
        HashSet<DocumentPair> related = new HashSet<DocumentPair>();

        foreach (IGrouping<string, DocGraphEdge> group in edges
                     .Where(edge => SharedTargetLabels.Contains(edge.Label)
                         && documentIds.Contains(edge.From))
                     .GroupBy(edge => edge.To, StringComparer.Ordinal))
        {
            RelateEveryPair(group.Select(edge => edge.From), related);
        }

        foreach (DocGraphEdge edge in edges.Where(edge => DirectDocumentLabels.Contains(edge.Label)))
        {
            if (documentIds.Contains(edge.From) && documentIds.Contains(edge.To))
                related.Add(DocumentPair.Create(edge.From, edge.To));
        }

        (string DocumentId, string Path)[] sourceRoots = edges
            .Where(edge => edge.Label == "DESCRIBES"
                && documentIds.Contains(edge.From)
                && edge.To.StartsWith("path:", StringComparison.Ordinal))
            .Select(edge => (DocumentId: edge.From, Path: NormalizePath(edge.To["path:".Length..])))
            .Where(item => item.Path.Length > 0)
            .ToArray();

        for (int left = 0; left < sourceRoots.Length; left++)
        {
            for (int right = left + 1; right < sourceRoots.Length; right++)
            {
                if (PathsOverlap(sourceRoots[left].Path, sourceRoots[right].Path))
                    related.Add(DocumentPair.Create(
                        sourceRoots[left].DocumentId,
                        sourceRoots[right].DocumentId));
            }
        }

        if (codeGraph is not ICodeGraphNeighborhoodProvider neighborhoods)
            return related;

        Dictionary<string, string[]> codeToDocuments = edges
            .Where(edge => edge.Label is "REFERENCES" or "EXPOSES"
                && documentIds.Contains(edge.From))
            .GroupBy(edge => edge.To, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.From).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        if (codeToDocuments.Count == 0) return related;

        CodeGraphEdge[] codeEdges = neighborhoods.GetEdges(codeToDocuments.Keys.ToArray(), maxCodeNeighbors)
            .Where(edge => TraversedCodeEdgeKinds.Contains(edge.Kind))
            .Distinct()
            .ToArray();

        // Providers enforce the cap against the full index. Enforcing it again makes the
        // optional port safe for simpler fakes and alternative providers as well.
        Dictionary<string, int> returnedDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CodeGraphEdge edge in codeEdges)
        {
            IncrementDegree(returnedDegree, edge.SourceId);
            if (!StringComparer.Ordinal.Equals(edge.SourceId, edge.TargetId))
                IncrementDegree(returnedDegree, edge.TargetId);
        }

        foreach (CodeGraphEdge edge in codeEdges)
        {
            if (returnedDegree[edge.SourceId] > maxCodeNeighbors
                || returnedDegree[edge.TargetId] > maxCodeNeighbors
                || !codeToDocuments.TryGetValue(edge.SourceId, out string[]? sources)
                || !codeToDocuments.TryGetValue(edge.TargetId, out string[]? targets))
            {
                continue;
            }

            foreach (string source in sources)
                foreach (string target in targets)
                {
                    if (!StringComparer.Ordinal.Equals(source, target))
                        related.Add(DocumentPair.Create(source, target));
                }
        }

        return related;
    }

    private static void IncrementDegree(IDictionary<string, int> degrees, string nodeId)
    {
        degrees.TryGetValue(nodeId, out int degree);
        degrees[nodeId] = degree + 1;
    }

    private static void RelateEveryPair(
        IEnumerable<string> documentIds,
        ISet<DocumentPair> related)
    {
        string[] ids = documentIds.Distinct(StringComparer.Ordinal).ToArray();
        for (int left = 0; left < ids.Length; left++)
            for (int right = left + 1; right < ids.Length; right++)
                related.Add(DocumentPair.Create(ids[left], ids[right]));
    }

    private static bool PathsOverlap(string left, string right) =>
        left == "."
        || right == "."
        || StringComparer.OrdinalIgnoreCase.Equals(left, right)
        || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase)
        || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim().TrimEnd('/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static void AddConcept(
        ICollection<DocGraphNode> nodes,
        ISet<string> emittedNodeIds,
        string label,
        string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        AddNode(nodes, emittedNodeIds, new DocGraphNode(
            $"{label.ToLowerInvariant()}:{name}",
            label,
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["name"] = name }));
    }

    private static void AddNode(
        ICollection<DocGraphNode> nodes,
        ISet<string> emittedNodeIds,
        DocGraphNode node)
    {
        if (emittedNodeIds.Add(node.Id)) nodes.Add(node);
    }

    private static void AddEdge(
        ICollection<DocGraphEdge> edges,
        string label,
        string from,
        string? to)
    {
        if (!string.IsNullOrWhiteSpace(to)) edges.Add(new DocGraphEdge(label, from, to));
    }

    private static DocGraphNode Copy(DocGraphNode node) =>
        new(node.Id, node.Label, new Dictionary<string, string?>(node.Properties, StringComparer.Ordinal));

    private static string DocId(string id) => $"doc:{id}";

    private static string? ConceptId(string label, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : $"{label.ToLowerInvariant()}:{name}";

    /// <summary>Resolves a relative link against the linking document's directory.</summary>
    internal static string? ResolveLink(string fromRelativePath, string link)
    {
        string directory = Path.GetDirectoryName(fromRelativePath)?.Replace('\\', '/') ?? string.Empty;
        string combined = string.IsNullOrEmpty(directory) ? link : $"{directory}/{link}";

        List<string> parts = new List<string>();
        foreach (string segment in combined.Split('/'))
        {
            if (segment is "." or "") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private readonly record struct DocumentPair(string Left, string Right)
    {
        public static DocumentPair Create(string left, string right) =>
            StringComparer.Ordinal.Compare(left, right) <= 0
                ? new DocumentPair(left, right)
                : new DocumentPair(right, left);
    }
}
