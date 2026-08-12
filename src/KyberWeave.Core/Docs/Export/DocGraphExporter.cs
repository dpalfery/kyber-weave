using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Graph;
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
        => Export(set, outputDirectory, contributors: null);

    /// <summary>Exports documents plus independently governed graph contributions.</summary>
    public DocGraphExportResult Export(
        DocumentSet set,
        string outputDirectory,
        IReadOnlyList<IDocGraphContributor>? contributors)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        var nodesPath = Path.Combine(outputDirectory, "nodes.jsonl");
        var edgesPath = Path.Combine(outputDirectory, "edges.jsonl");

        var projection = DocGraphProjection.Build(set, _resolver, contributors: contributors);
        var nodeLines = projection.Nodes.Select(Node).ToList();
        var edgeLines = projection.Edges.Select(Edge).ToList();

        File.WriteAllLines(nodesPath, nodeLines);
        File.WriteAllLines(edgesPath, edgeLines);

        return new DocGraphExportResult(nodeLines.Count, edgeLines.Count, nodesPath, edgesPath);
    }

    private static string Node(DocGraphNode node)
    {
        var json = new JsonObject
        {
            ["type"] = "node",
            ["id"] = node.Id,
            ["label"] = node.Label
        };

        foreach (var property in node.Properties)
        {
            if (property.Key is "type" or "id" or "label") continue;
            json[property.Key] = property.Value;
        }

        return Line(json);
    }

    private static string Edge(DocGraphEdge edge) => Line(new JsonObject
    {
        ["type"] = "edge",
        ["label"] = edge.Label,
        ["from"] = edge.From,
        ["to"] = edge.To
    });

    private static string Line(JsonNode node) => node.ToJsonString(Compact);

    /// <summary>Resolves a relative link against the linking document's directory.</summary>
    internal static string? ResolveLink(string fromRelativePath, string link) =>
        DocGraphProjection.ResolveLink(fromRelativePath, link);
}
