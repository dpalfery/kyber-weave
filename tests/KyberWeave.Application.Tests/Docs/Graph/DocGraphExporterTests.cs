using System.Collections.ObjectModel;
using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Export;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Domain.CodeGraph;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Graph;

/// <summary>The graph export over the write seam: the two JSONL files land in memory, so nothing touches the disk.</summary>
public sealed class DocGraphExporterTests
{
    private const string Out = "/out/graph";

    private readonly InMemoryFileSystem _files = new();

    private DocGraphExportResult Export(DocumentSet set, ICodeGraphResolver? resolver = null) =>
        new DocGraphExporter(resolver ?? new EmptyResolver(), _files).Export(set, Out);

    [Fact]
    public void ItCreatesTheOutputDirectoryAndWritesOneJsonLinePerNodeAndEdge()
    {
        DocumentSet set = new() { Documents = [Document("a", "docs/a.md", owner: "Platform"), Document("b", "docs/b.md", bodyLinks: ["a.md"])] };

        DocGraphExportResult result = Export(set);

        Assert.True(_files.DirectoryExists(Out));
        Assert.Equal(Path.Combine(Out, "nodes.jsonl"), result.NodesPath);
        Assert.Equal(Path.Combine(Out, "edges.jsonl"), result.EdgesPath);
        string[] nodes = _files.ReadAllText(result.NodesPath).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string[] edges = _files.ReadAllText(result.EdgesPath).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(result.NodeCount, nodes.Length);
        Assert.Equal(result.EdgeCount, edges.Length);
        Assert.Contains(nodes, line => JsonDocument.Parse(line).RootElement.GetProperty("id").GetString() == "doc:a");
        Assert.Contains(edges, line => JsonDocument.Parse(line).RootElement.GetProperty("label").GetString() == "LINKS_TO");
    }

    [Fact]
    public void EachLineEndsWithTheNewlineFileWriteAllLinesWouldHaveWritten()
    {
        DocGraphExportResult result = Export(new DocumentSet { Documents = [Document("a", "docs/a.md")] });

        string text = _files.ReadAllText(result.NodesPath);

        Assert.EndsWith(Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Equal(result.NodeCount, text.Split(Environment.NewLine).Length - 1);
    }

    [Fact]
    public void AnEmptyGraphStillWritesBothFilesEmpty()
    {
        DocGraphExportResult result = Export(new DocumentSet { Documents = [] });

        Assert.Equal(0, result.NodeCount);
        Assert.Equal(string.Empty, _files.ReadAllText(result.NodesPath));
        Assert.Equal(string.Empty, _files.ReadAllText(result.EdgesPath));
    }

    [Fact]
    public void ABlankOutputDirectoryIsRefusedBeforeAnythingIsWritten()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new DocGraphExporter(new EmptyResolver(), _files).Export(new DocumentSet { Documents = [] }, " "));
        Assert.Equal(0, _files.OperationCount);
    }

    private static DocumentModel Document(string id, string path, string? owner = null, IReadOnlyList<string>? bodyLinks = null) => new()
    {
        RelativePath = path,
        FilePath = "/tmp/" + path,
        HasFrontmatter = true,
        Frontmatter = new DocumentFrontmatter
        {
            Id = id,
            Title = id,
            DocType = "architecture",
            Status = "current",
            Owner = owner,
            LastReviewed = "2026-08-11",
            CodeRefs = new Collection<string>(),
        },
        DocType = DocType.Architecture,
        Status = DocStatus.Current,
        BodyLinks = bodyLinks ?? [],
    };

    private sealed class EmptyResolver : ICodeGraphResolver
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public string DatabasePath => ":memory:";

        public IReadOnlyList<CodeGraphNode> ResolveSymbol(string name) => [];

        public IReadOnlyList<CodeGraphNode> ResolveRoute(string route) => [];

        public bool HasFilesUnder(string relativePathPrefix) => false;

        public IReadOnlyList<string> CandidateNames(string likePattern) => [];

        public IReadOnlyList<string> AllRoutes() => [];
    }
}
