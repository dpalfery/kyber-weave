using System.Collections.ObjectModel;
using System.Text.Json;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Export;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T05 RED — one immutable DocGraph projection is shared by export, retrieval, and
/// documentation analysis instead of each consumer reconstructing relationships.
/// </summary>
public sealed class DocGraphProjectionTests
{
    private static readonly string[] TraversedCodeEdgeKinds =
        ["contains", "calls", "references", "instantiates", "extends", "implements"];

    [Fact]
    public void Build_Projects_Exporter_Compatible_Document_Concept_And_Join_Relationships()
    {
        var resolver = new NeighborhoodResolver()
            .WithSymbol("BillingService", Node("code:billing", "BillingService"))
            .WithRoute("GET /billing", Node("route:billing", "GET /billing", "route"));
        var set = FullRelationshipSet();

        var projection = DocGraphProjection.Build(set, resolver);

        Assert.Contains(projection.Nodes, n => n.Id == "doc:architecture/billing" && n.Label == "Document");
        Assert.Contains(projection.Nodes, n => n.Id == "component:Billing" && n.Label == "Component");
        Assert.Contains(projection.Nodes, n => n.Id == "team:Platform" && n.Label == "Team");
        Assert.Contains(new DocGraphEdge("DOCUMENTS", "doc:architecture/billing", "component:Billing"), projection.Edges);
        Assert.Contains(new DocGraphEdge("OWNED_BY", "doc:architecture/billing", "team:Platform"), projection.Edges);
        Assert.Contains(new DocGraphEdge("DESCRIBES", "doc:architecture/billing", "path:src/Billing"), projection.Edges);
        Assert.Contains(new DocGraphEdge("REFERENCES", "doc:architecture/billing", "code:billing"), projection.Edges);
        Assert.Contains(new DocGraphEdge("EXPOSES", "doc:architecture/billing", "route:billing"), projection.Edges);
        Assert.Contains(new DocGraphEdge("LINKS_TO", "doc:architecture/billing", "doc:reference/billing"), projection.Edges);
        Assert.Contains(new DocGraphEdge("DECIDED_BY", "doc:architecture/billing", "doc:adr/0001"), projection.Edges);
        Assert.Contains(new DocGraphEdge("SUPERSEDES", "doc:architecture/billing", "doc:architecture/legacy"), projection.Edges);
    }

    [Fact]
    public void Build_Copies_Contributor_Output_Into_An_Immutable_Snapshot()
    {
        var nodes = new List<DocGraphNode>
        {
            new("term:loop", "Term", new ReadOnlyDictionary<string, string?>(
                new Dictionary<string, string?> { ["name"] = "loop" }))
        };
        var edges = new List<DocGraphEdge>
        {
            new("EVIDENCED_BY", "term:loop", "doc:reference/billing")
        };
        var contributor = new StubContributor(nodes, edges);

        var projection = DocGraphProjection.Build(
            new DocumentSet { Documents = [] },
            FakeCodeGraphResolver.WithSymbols(),
            contributors: [contributor]);
        nodes.Clear();
        edges.Clear();

        Assert.Contains(projection.Nodes, n => n.Id == "term:loop");
        Assert.Contains(projection.Edges, e => e.From == "term:loop");
    }

    [Fact]
    public void AreDocumentsRelated_Treats_Overlapping_Source_Roots_As_Graph_Neighbors()
    {
        var set = new DocumentSet
        {
            Documents =
            [
                Document("architecture/parent", "docs/parent.md", sourceRoot: "src/Gameplay"),
                Document("reference/child", "docs/child.md", sourceRoot: "src/Gameplay/Loops")
            ]
        };

        var projection = DocGraphProjection.Build(set, FakeCodeGraphResolver.WithSymbols());

        Assert.True(projection.AreDocumentsRelated("doc:architecture/parent", "doc:reference/child"));
    }

    [Theory]
    [MemberData(nameof(ApprovedCodeEdgeKinds))]
    public void AreDocumentsRelated_Traverses_Approved_OneHop_CodeGraph_Edges(string kind)
    {
        var resolver = new NeighborhoodResolver()
            .WithSymbol("Left", Node("code:left", "Left"))
            .WithSymbol("Right", Node("code:right", "Right"))
            .WithEdge(new CodeGraphEdge("code:left", "code:right", kind));
        var set = TwoCodeReferenceDocuments();

        var projection = DocGraphProjection.Build(set, resolver);

        Assert.True(projection.AreDocumentsRelated("doc:left", "doc:right"));
        Assert.Equal(1, resolver.NeighborhoodRequestCount);
        Assert.Equal(["code:left", "code:right"], resolver.LastRequestedNodeIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AreDocumentsRelated_Does_Not_Traverse_Imports()
    {
        var resolver = new NeighborhoodResolver()
            .WithSymbol("Left", Node("code:left", "Left"))
            .WithSymbol("Right", Node("code:right", "Right"))
            .WithEdge(new CodeGraphEdge("code:left", "code:right", "imports"));

        var projection = DocGraphProjection.Build(TwoCodeReferenceDocuments(), resolver);

        Assert.False(projection.AreDocumentsRelated("doc:left", "doc:right"));
    }

    [Fact]
    public void Build_Skips_Code_Nodes_Whose_Degree_Exceeds_The_Cap()
    {
        var resolver = new NeighborhoodResolver()
            .WithSymbol("Left", Node("code:hub", "Left"))
            .WithSymbol("Right", Node("code:right", "Right"))
            .WithEdge(new CodeGraphEdge("code:hub", "code:right", "calls"))
            .WithEdge(new CodeGraphEdge("code:hub", "code:third", "calls"));

        var projection = DocGraphProjection.Build(
            TwoCodeReferenceDocuments(), resolver, maxCodeNeighbors: 1);

        Assert.False(projection.AreDocumentsRelated("doc:left", "doc:right"));
        Assert.Equal(1, resolver.LastMaxDegree);
    }

    [Fact]
    public void Build_When_Resolver_Has_No_Neighborhood_Port_Preserves_Document_Relationships()
    {
        var set = new DocumentSet
        {
            Documents =
            [
                Document("first", "docs/first.md", component: "Shared"),
                Document("second", "docs/second.md", component: "Shared")
            ]
        };

        var projection = DocGraphProjection.Build(set, FakeCodeGraphResolver.WithSymbols());

        Assert.True(projection.AreDocumentsRelated("doc:first", "doc:second"));
    }

    [Fact]
    public void AreDocumentsRelated_Does_Not_Relate_Documents_Only_Because_They_Share_An_Owner()
    {
        var set = new DocumentSet
        {
            Documents =
            [
                Document("first", "docs/first.md", owner: "Platform"),
                Document("second", "docs/second.md", owner: "Platform")
            ]
        };

        var projection = DocGraphProjection.Build(set, FakeCodeGraphResolver.WithSymbols());

        Assert.False(projection.AreDocumentsRelated("doc:first", "doc:second"));
    }

    [Fact]
    public void DocGraphExporter_Output_Remains_Compatible_With_The_V1_Jsonl_Schema()
    {
        var resolver = new NeighborhoodResolver()
            .WithSymbol("BillingService", Node("code:billing", "BillingService"))
            .WithRoute("GET /billing", Node("route:billing", "GET /billing", "route"));
        using var output = new TempDirectory();

        var result = new DocGraphExporter(resolver).Export(FullRelationshipSet(), output.Path);
        var nodes = File.ReadAllLines(result.NodesPath).Select(Parse).ToList();
        var edges = File.ReadAllLines(result.EdgesPath).Select(Parse).ToList();

        Assert.All(nodes, node => Assert.Equal("node", node.GetProperty("type").GetString()));
        Assert.All(edges, edge => Assert.Equal("edge", edge.GetProperty("type").GetString()));
        Assert.Contains(nodes, node => node.GetProperty("id").GetString() == "doc:architecture/billing"
            && node.GetProperty("docType").GetString() == "architecture"
            && node.GetProperty("status").GetString() == "current"
            && node.GetProperty("path").GetString() == "docs/billing.md");
        Assert.Contains(edges, edge => edge.GetProperty("label").GetString() == "REFERENCES"
            && edge.GetProperty("from").GetString() == "doc:architecture/billing"
            && edge.GetProperty("to").GetString() == "code:billing");
    }

    public static TheoryData<string> ApprovedCodeEdgeKinds()
    {
        var data = new TheoryData<string>();
        foreach (var kind in TraversedCodeEdgeKinds)
            data.Add(kind);
        return data;
    }

    private static DocumentSet FullRelationshipSet() =>
        new()
        {
            Documents =
            [
                Document(
                    "architecture/billing",
                    "docs/billing.md",
                    component: "Billing",
                    owner: "Platform",
                    sourceRoot: "src/Billing",
                    codeRefs: ["BillingService"],
                    endpoints: ["GET /billing"],
                    decidedBy: ["adr/0001"],
                    supersedes: ["architecture/legacy"],
                    bodyLinks: ["billing-reference.md"]),
                Document("reference/billing", "docs/billing-reference.md")
            ]
        };

    private static DocumentSet TwoCodeReferenceDocuments() =>
        new()
        {
            Documents =
            [
                Document("left", "docs/left.md", codeRefs: ["Left"]),
                Document("right", "docs/right.md", codeRefs: ["Right"])
            ]
        };

    private static DocumentModel Document(
        string id,
        string path,
        string? component = null,
        string? owner = null,
        string? sourceRoot = null,
        IReadOnlyList<string>? codeRefs = null,
        IReadOnlyList<string>? endpoints = null,
        IReadOnlyList<string>? decidedBy = null,
        IReadOnlyList<string>? supersedes = null,
        IReadOnlyList<string>? bodyLinks = null) =>
        new()
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
                Component = component,
                Owner = owner,
                SourceRoot = sourceRoot,
                LastReviewed = "2026-08-11",
                CodeRefs = codeRefs is null ? null : new Collection<string>(codeRefs.ToList()),
                ApiEndpoints = endpoints is null ? null : new Collection<string>(endpoints.ToList()),
                DecidedBy = decidedBy is null ? null : new Collection<string>(decidedBy.ToList()),
                Supersedes = supersedes is null ? null : new Collection<string>(supersedes.ToList())
            },
            DocType = DocType.Architecture,
            Status = DocStatus.Current,
            BodyLinks = bodyLinks ?? []
        };

    private static CodeGraphNode Node(string id, string name, string kind = "class") =>
        new(id, kind, name, name, $"src/{name}.cs", "csharp", 1);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class StubContributor(
        IReadOnlyList<DocGraphNode> nodes,
        IReadOnlyList<DocGraphEdge> edges) : IDocGraphContributor
    {
        public DocGraphContribution Contribute(DocumentSet documents, ICodeGraphResolver codeGraph) =>
            new(nodes, edges);
    }

    private sealed class NeighborhoodResolver : ICodeGraphResolver, ICodeGraphNeighborhoodProvider
    {
        private readonly Dictionary<string, CodeGraphNode> _symbols = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CodeGraphNode> _routes = new(StringComparer.Ordinal);
        private readonly List<CodeGraphEdge> _edges = [];

        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public string DatabasePath => ":memory:";
        public int NeighborhoodRequestCount { get; private set; }
        public int LastMaxDegree { get; private set; }
        public IReadOnlyList<string> LastRequestedNodeIds { get; private set; } = [];

        public NeighborhoodResolver WithSymbol(string name, CodeGraphNode node)
        {
            _symbols[name] = node;
            return this;
        }

        public NeighborhoodResolver WithRoute(string route, CodeGraphNode node)
        {
            _routes[route] = node;
            return this;
        }

        public NeighborhoodResolver WithEdge(CodeGraphEdge edge)
        {
            _edges.Add(edge);
            return this;
        }

        public IReadOnlyList<CodeGraphNode> ResolveSymbol(string name) =>
            _symbols.TryGetValue(name, out var node) ? [node] : [];

        public IReadOnlyList<CodeGraphNode> ResolveRoute(string route) =>
            _routes.TryGetValue(route, out var node) ? [node] : [];

        public IReadOnlyList<CodeGraphEdge> GetEdges(IReadOnlyCollection<string> nodeIds, int maxDegree)
        {
            NeighborhoodRequestCount++;
            LastMaxDegree = maxDegree;
            LastRequestedNodeIds = nodeIds.ToArray();
            return _edges;
        }

        public bool HasFilesUnder(string relativePathPrefix) => true;
        public IReadOnlyList<string> CandidateNames(string like) => [];
        public IReadOnlyList<string> AllRoutes() => _routes.Keys.ToList();
    }
}
