using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Domain.CodeGraph;
using System.Collections.ObjectModel;

namespace KyberWeave.Core.Docs.Graph;

/// <summary>
/// Builds the shared <see cref="DocGraphProjection"/>, binding the CodeGraph ports for callers
/// holding an <see cref="ICodeGraphResolver"/>.
/// </summary>
public static class DocGraphProjectionBuilder
{
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

        List<DocGraphContribution>? contributions = null;
        if (contributors is not null)
        {
            contributions = new List<DocGraphContribution>(contributors.Count);
            foreach (IDocGraphContributor contributor in contributors)
            {
                ArgumentNullException.ThrowIfNull(contributor);
                contributions.Add(contributor.Contribute(documents, codeGraph)
                                  ?? throw new InvalidOperationException(
                                      "A DocGraph contributor returned null."));
            }
        }

        return Build(
            documents,
            codeGraph.ResolveSymbol,
            codeGraph.ResolveRoute,
            codeGraph is ICodeGraphNeighborhoodProvider neighborhoods ? neighborhoods.GetEdges : null,
            maxCodeNeighbors,
            contributions);
    }

    private static readonly HashSet<string> SharedTargetLabels =
        ["DOCUMENTS", "DESCRIBES", "REFERENCES", "EXPOSES"];

    private static readonly HashSet<string> DirectDocumentLabels =
        ["LINKS_TO", "DECIDED_BY", "SUPERSEDES"];

    private static readonly HashSet<string> TraversedCodeEdgeKinds =
        ["contains", "calls", "references", "instantiates", "extends", "implements"];


    /// <summary>Builds the shared projection and optional one-hop CodeGraph neighborhood.</summary>
    /// <remarks>
    /// The CodeGraph is reached through delegates so the projection needs no port: callers
    /// holding an <c>ICodeGraphResolver</c> bind its lookups (the overload above does this). <paramref name="getNeighborEdges"/> is
    /// null when the index offers no neighborhood provider, in which case only
    /// document-side relationships are projected.
    /// </remarks>
    public static DocGraphProjection Build(
        DocumentSet documents,
        Func<string, IReadOnlyList<CodeGraphNode>> resolveSymbol,
        Func<string, IReadOnlyList<CodeGraphNode>> resolveRoute,
        Func<IReadOnlyCollection<string>, int, IReadOnlyList<CodeGraphEdge>>? getNeighborEdges,
        int maxCodeNeighbors = 50,
        IReadOnlyList<DocGraphContribution>? contributions = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(resolveSymbol);
        ArgumentNullException.ThrowIfNull(resolveRoute);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCodeNeighbors);

        List<DocGraphNode> nodes = new();
        List<DocGraphEdge> edges = new();
        HashSet<string> emittedNodeIds = new(StringComparer.Ordinal);
        HashSet<string> documentIds = new(StringComparer.Ordinal);
        Dictionary<string, string> pathToId = new(StringComparer.Ordinal);

        foreach (DocumentModel document in documents.Documents)
            pathToId.TryAdd(document.RelativePath, DocId(document.Subject));

        foreach (DocumentModel document in documents.Documents)
        {
            // Claim.DocumentIdentity is document.Subject, which falls back to RelativePath
            // when frontmatter has no id. Register the same node so graph candidacy can
            // find intra-document and shared-concept pairs instead of querying a missing id.
            string documentId = DocId(document.Subject);
            documentIds.Add(documentId);

#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
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
#pragma warning restore CA1308

            AddConcept(nodes, emittedNodeIds, "Component", document.Frontmatter.Component);
            AddConcept(nodes, emittedNodeIds, "Team", document.Frontmatter.Owner);

            AddEdge(edges, "DOCUMENTS", documentId,
                ConceptId("Component", document.Frontmatter.Component));
            AddEdge(edges, "OWNED_BY", documentId,
                ConceptId("Team", document.Frontmatter.Owner));

            if (!string.IsNullOrWhiteSpace(document.Frontmatter.SourceRoot))
                edges.Add(new DocGraphEdge(
                    "DESCRIBES", documentId, $"path:{document.Frontmatter.SourceRoot}"));

            foreach (string symbol in document.CodeRefs())
                foreach (CodeGraphNode node in resolveSymbol(symbol))
                    edges.Add(new DocGraphEdge("REFERENCES", documentId, node.Id));

            foreach (string endpoint in document.ApiEndpoints)
                foreach (CodeGraphNode node in resolveRoute(endpoint))
                    edges.Add(new DocGraphEdge("EXPOSES", documentId, node.Id));

            foreach (string adr in document.DecidedBy)
                edges.Add(new DocGraphEdge("DECIDED_BY", documentId, DocId(adr)));

            foreach (string superseded in document.Supersedes)
                edges.Add(new DocGraphEdge("SUPERSEDES", documentId, DocId(superseded)));

            foreach (string link in document.BodyLinks)
            {
                string? target = DocumentLinkPaths.Resolve(document.RelativePath, link);
                if (target is not null
                    && pathToId.TryGetValue(target, out string? targetId)
                    && !StringComparer.Ordinal.Equals(targetId, documentId))
                {
                    edges.Add(new DocGraphEdge("LINKS_TO", documentId, targetId));
                }
            }
        }

        if (contributions is not null)
        {
            foreach (DocGraphContribution contribution in contributions)
            {
                ArgumentNullException.ThrowIfNull(contribution);
                foreach (DocGraphNode node in contribution.Nodes)
                    AddNode(nodes, emittedNodeIds, Copy(node));
                foreach (DocGraphEdge edge in contribution.Edges)
                    edges.Add(edge with { });
            }
        }

        DocumentSectionGraph sectionGraph = DocumentSectionGraph.Build(documents.Documents);
        foreach (DocGraphNode node in sectionGraph.Contribution.Nodes)
            AddNode(nodes, emittedNodeIds, Copy(node));
        foreach (DocGraphEdge edge in sectionGraph.Contribution.Edges)
            edges.Add(edge with { });

        DocGraphEdge[] distinctEdges = edges.Distinct().ToArray();
        IReadOnlySet<DocumentPair> related = BuildDocumentRelationships(
            documentIds,
            distinctEdges,
            getNeighborEdges,
            maxCodeNeighbors);

        return new DocGraphProjection(
            nodes,
            distinctEdges,
            documentIds,
            BuildRelationshipIndex(documentIds, related));
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
        Func<IReadOnlyCollection<string>, int, IReadOnlyList<CodeGraphEdge>>? getNeighborEdges,
        int maxCodeNeighbors)
    {
        HashSet<DocumentPair> related = new();

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

        if (getNeighborEdges is null)
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

        CodeGraphEdge[] codeEdges = getNeighborEdges(codeToDocuments.Keys.ToArray(), maxCodeNeighbors)
            .Where(edge => TraversedCodeEdgeKinds.Contains(edge.Kind))
            .Distinct()
            .ToArray();

        // Providers enforce the cap against the full index. Enforcing it again makes the
        // optional port safe for simpler fakes and alternative providers as well.
        Dictionary<string, int> returnedDegree = new(StringComparer.Ordinal);
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

    private static bool PathsOverlap(string left, string right)
    {
        return left == "."
               || right == "."
               || StringComparer.OrdinalIgnoreCase.Equals(left, right)
               || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase)
               || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim().TrimEnd('/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
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
#pragma warning restore CA1308

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

    private static DocGraphNode Copy(DocGraphNode node)
    {
        return new DocGraphNode(node.Id, node.Label,
            new Dictionary<string, string?>(node.Properties, StringComparer.Ordinal));
    }

    private static string DocId(string id)
    {
        return $"doc:{id}";
    }

#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
    private static string? ConceptId(string label, string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? null : $"{label.ToLowerInvariant()}:{name}";
    }
#pragma warning restore CA1308

    private readonly record struct DocumentPair(string Left, string Right)
    {
        public static DocumentPair Create(string left, string right)
        {
            return StringComparer.Ordinal.Compare(left, right) <= 0
                ? new DocumentPair(left, right)
                : new DocumentPair(right, left);
        }
    }
}
