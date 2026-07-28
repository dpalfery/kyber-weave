using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Export;

/// <summary>
/// Emits the documentation graph as newline-delimited JSON.
/// </summary>
/// <remarks>
/// Code nodes are referenced by their CodeGraph id and are deliberately <b>not</b>
/// duplicated into the export. CodeGraph stays the single store for code structure; this
/// export carries document nodes and the document-to-code join edges only. An external
/// ingester consumes it alongside the CodeGraph index, not instead of it.
/// </remarks>
public sealed class DocGraphExporter
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly ICodeGraphResolver _resolver;

    public DocGraphExporter(ICodeGraphResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public DocGraphExportResult Export(DocumentSet set, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        var nodesPath = Path.Combine(outputDirectory, "nodes.jsonl");
        var edgesPath = Path.Combine(outputDirectory, "edges.jsonl");

        var nodeLines = new List<string>();
        var edgeLines = new List<string>();

        var emittedConceptNodes = new HashSet<string>(StringComparer.Ordinal);
        var pathToId = set.Documents
            .Where(d => !string.IsNullOrWhiteSpace(d.Frontmatter.Id))
            .ToDictionary(d => d.RelativePath, d => DocId(d.Frontmatter.Id!), StringComparer.Ordinal);

        foreach (var doc in set.Documents)
        {
            if (string.IsNullOrWhiteSpace(doc.Frontmatter.Id)) continue;

            var id = DocId(doc.Frontmatter.Id!);

            nodeLines.Add(Line(new JsonObject
            {
                ["type"] = "node",
                ["id"] = id,
                ["label"] = "Document",
                ["docType"] = doc.DocType.ToString().ToLowerInvariant(),
                ["status"] = doc.Status.ToString().ToLowerInvariant(),
                ["title"] = doc.Frontmatter.Title,
                ["path"] = doc.RelativePath
            }));

            AddConceptNode(nodeLines, emittedConceptNodes, "Component", doc.Frontmatter.Component);
            AddConceptNode(nodeLines, emittedConceptNodes, "Team", doc.Frontmatter.Owner);

            if (!string.IsNullOrWhiteSpace(doc.Frontmatter.Component))
                edgeLines.Add(Edge("DOCUMENTS", id, ConceptId("Component", doc.Frontmatter.Component)));

            if (!string.IsNullOrWhiteSpace(doc.Frontmatter.Owner))
                edgeLines.Add(Edge("OWNED_BY", id, ConceptId("Team", doc.Frontmatter.Owner)));

            if (!string.IsNullOrWhiteSpace(doc.Frontmatter.SourceRoot))
                edgeLines.Add(Edge("DESCRIBES", id, $"path:{doc.Frontmatter.SourceRoot}"));

            foreach (var symbol in doc.CodeRefs)
                foreach (var node in _resolver.ResolveSymbol(symbol))
                    edgeLines.Add(Edge("REFERENCES", id, node.Id));

            foreach (var endpoint in doc.ApiEndpoints)
                foreach (var node in _resolver.ResolveRoute(endpoint))
                    edgeLines.Add(Edge("EXPOSES", id, node.Id));

            foreach (var adr in doc.DecidedBy)
                edgeLines.Add(Edge("DECIDED_BY", id, DocId(adr)));

            foreach (var superseded in doc.Supersedes)
                edgeLines.Add(Edge("SUPERSEDES", id, DocId(superseded)));

            foreach (var link in doc.BodyLinks)
            {
                var target = ResolveLink(doc.RelativePath, link);
                if (target is not null && pathToId.TryGetValue(target, out var targetId) && targetId != id)
                {
                    edgeLines.Add(Edge("LINKS_TO", id, targetId));
                }
            }
        }

        edgeLines = edgeLines.Distinct(StringComparer.Ordinal).ToList();

        File.WriteAllLines(nodesPath, nodeLines);
        File.WriteAllLines(edgesPath, edgeLines);

        return new DocGraphExportResult(nodeLines.Count, edgeLines.Count, nodesPath, edgesPath);
    }

    private static void AddConceptNode(List<string> lines, HashSet<string> emitted, string label, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = ConceptId(label, name);
        if (!emitted.Add(id)) return;

        lines.Add(Line(new JsonObject
        {
            ["type"] = "node",
            ["id"] = id,
            ["label"] = label,
            ["name"] = name
        }));
    }

    internal static string DocId(string id) => $"doc:{id}";

    private static string ConceptId(string label, string? name) =>
        $"{label.ToLowerInvariant()}:{name}";

    private static string Edge(string label, string from, string to) => Line(new JsonObject
    {
        ["type"] = "edge",
        ["label"] = label,
        ["from"] = from,
        ["to"] = to
    });

    private static string Line(JsonNode node) => node.ToJsonString(Compact);

    /// <summary>Resolves a relative link against the linking document's directory.</summary>
    internal static string? ResolveLink(string fromRelativePath, string link)
    {
        var directory = Path.GetDirectoryName(fromRelativePath)?.Replace('\\', '/') ?? string.Empty;
        var combined = string.IsNullOrEmpty(directory) ? link : $"{directory}/{link}";

        var parts = new List<string>();
        foreach (var segment in combined.Split('/'))
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
}
