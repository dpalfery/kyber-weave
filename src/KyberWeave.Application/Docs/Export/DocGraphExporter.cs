using System.Text.Json.Nodes;
using System.Text.Json;

using KyberWeave.Contracts.Repositories;
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
public sealed class DocGraphExporter(ICodeGraphResolver resolver, IWritableFileSystem files)
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly ICodeGraphResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IWritableFileSystem _files = files ?? throw new ArgumentNullException(nameof(files));

    public DocGraphExportResult Export(DocumentSet set, string outputDirectory)
    {
        return Export(set, outputDirectory, null);
    }

    /// <summary>Exports documents plus independently governed graph contributions.</summary>
    public DocGraphExportResult Export(
        DocumentSet set,
        string outputDirectory,
        IReadOnlyList<IDocGraphContributor>? contributors)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        _files.CreateDirectory(outputDirectory);
        string nodesPath = Path.Combine(outputDirectory, "nodes.jsonl");
        string edgesPath = Path.Combine(outputDirectory, "edges.jsonl");

        List<DocGraphContribution>? contributions = contributors?
            .Select(contributor => contributor.Contribute(set, _resolver)
                                   ?? throw new InvalidOperationException(
                                       "A DocGraph contributor returned null."))
            .ToList();
        DocGraphProjection projection = DocGraphProjection.Build(
            set,
            _resolver.ResolveSymbol,
            _resolver.ResolveRoute,
            _resolver is ICodeGraphNeighborhoodProvider neighborhoods ? neighborhoods.GetEdges : null,
            contributions: contributions);
        List<string> nodeLines = projection.Nodes.Select(Node).ToList();
        List<string> edgeLines = projection.Edges.Select(Edge).ToList();

        _files.WriteAllText(nodesPath, Joined(nodeLines));
        _files.WriteAllText(edgesPath, Joined(edgeLines));

        return new DocGraphExportResult(nodeLines.Count, edgeLines.Count, nodesPath, edgesPath);
    }

    private static string Joined(IEnumerable<string> lines) =>
        string.Concat(lines.Select(line => line + Environment.NewLine));

    private static string Node(DocGraphNode node)
    {
        JsonObject json = new()
        {
            ["type"] = "node",
            ["id"] = node.Id,
            ["label"] = node.Label
        };

        foreach (KeyValuePair<string, string?> property in node.Properties)
        {
            if (property.Key is "type" or "id" or "label") continue;
            json[property.Key] = property.Value;
        }

        return Line(json);
    }

    private static string Edge(DocGraphEdge edge)
    {
        JsonObject json = new()
        {
            ["type"] = "edge",
            ["label"] = edge.Label,
            ["from"] = edge.From,
            ["to"] = edge.To
        };

        if (edge.Properties is not null)
        {
            foreach (KeyValuePair<string, string?> property in edge.Properties)
            {
                if (property.Key is "type" or "label" or "from" or "to") continue;
                json[property.Key] = property.Value;
            }
        }

        return Line(json);
    }

    private static string Line(JsonNode node)
    {
        return node.ToJsonString(Compact);
    }

    /// <summary>Resolves a relative link against the linking document's directory.</summary>
    internal static string? ResolveLink(string fromRelativePath, string link)
    {
        return DocGraphProjection.ResolveLink(fromRelativePath, link);
    }
}
