using System.Collections.ObjectModel;
using System.Text.Json;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Export;
using KyberWeave.Core.Docs.Model;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T11 RED — docs graph exports only approved managed-glossary knowledge while
/// preserving the existing deterministic JSONL contract.
/// </summary>
public sealed class GlossaryGraphExportTests
{
    [Fact]
    public void Export_ApprovedManagedSenses_EmitsTermSenseAliasScopeAndEvidenceGraph()
    {
        using var repository = GlossaryRepository();
        using var firstOutput = new TempDirectory();
        using var secondOutput = new TempDirectory();
        var glossary = GlossaryService(repository.Path).Load();
        var resolver = Resolver();
        var contributor = new ManagedGlossaryGraphContributor(glossary);
        var exporter = new DocGraphExporter(resolver);

        var first = exporter.Export(Documents(), firstOutput.Path, contributors: [contributor]);
        var second = exporter.Export(Documents(), secondOutput.Path, contributors: [contributor]);
        var nodes = File.ReadAllLines(first.NodesPath).Select(Parse).ToArray();
        var edges = File.ReadAllLines(first.EdgesPath).Select(Parse).ToArray();

        Assert.Contains(nodes, node => IsNode(node, "term:loop", "Term")
            && node.GetProperty("name").GetString() == "loop");
        Assert.Contains(nodes, node => IsNode(node, "sense:loop-gameplay", "Sense")
            && node.GetProperty("term").GetString() == "loop"
            && node.GetProperty("definition").GetString() == "The gameplay update cycle.");
        Assert.Contains(nodes, node => IsNode(node, "term:gameplay-loop", "Term")
            && node.GetProperty("name").GetString() == "gameplay loop");
        Assert.Contains(edges, edge => IsEdge(edge, "HAS_SENSE", "term:loop", "sense:loop-gameplay"));
        Assert.Contains(edges, edge => IsEdge(edge, "ALIAS_OF", "term:gameplay-loop", "sense:loop-gameplay"));
        Assert.Contains(edges, edge => IsEdge(edge, "SCOPED_TO", "sense:loop-gameplay", "component:Gameplay"));
        Assert.Contains(edges, edge => IsEdge(edge, "SCOPED_TO", "sense:loop-gameplay", "code:game-run"));
        Assert.Contains(edges, edge => IsEdge(edge, "EVIDENCED_BY", "sense:loop-gameplay", "claim-gameplay"));

        Assert.All(nodes, node =>
        {
            Assert.Equal("node", node.GetProperty("type").GetString());
            Assert.True(node.TryGetProperty("id", out _));
            Assert.True(node.TryGetProperty("label", out _));
        });
        Assert.All(edges, edge =>
        {
            Assert.Equal("edge", edge.GetProperty("type").GetString());
            Assert.True(edge.TryGetProperty("label", out _));
            Assert.True(edge.TryGetProperty("from", out _));
            Assert.True(edge.TryGetProperty("to", out _));
        });
        Assert.Equal(File.ReadAllText(first.NodesPath), File.ReadAllText(second.NodesPath));
        Assert.Equal(File.ReadAllText(first.EdgesPath), File.ReadAllText(second.EdgesPath));
    }

    [Fact]
    public void Export_ProposedAndRejectedSenses_ExcludesTheirNodesAndEveryDerivedEdge()
    {
        using var repository = GlossaryRepository();
        using var output = new TempDirectory();
        var contributor = new ManagedGlossaryGraphContributor(GlossaryService(repository.Path).Load());

        var result = new DocGraphExporter(Resolver()).Export(
            Documents(),
            output.Path,
            contributors: [contributor]);
        var allLines = File.ReadAllText(result.NodesPath) + File.ReadAllText(result.EdgesPath);

        Assert.DoesNotContain("loop-agent", allLines, StringComparison.Ordinal);
        Assert.DoesNotContain("churn loop", allLines, StringComparison.Ordinal);
        Assert.DoesNotContain("claim-agent", allLines, StringComparison.Ordinal);
        Assert.DoesNotContain("loop-legacy", allLines, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy loop", allLines, StringComparison.Ordinal);
        Assert.DoesNotContain("claim-legacy", allLines, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CollidingGlossaries))]
    public void Constructor_DistinctGlossaryIdentityWouldShareGraphId_FailsClosed(
        ManagedGlossaryLoadResult glossary)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            new ManagedGlossaryGraphContributor(glossary));

        Assert.Contains("collision", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<ManagedGlossaryLoadResult> CollidingGlossaries() => new()
    {
        CreateLoad(
            Term("run loop", Sense("run-cycle-a")),
            Term("run-loop", Sense("run-cycle-b"))),
        CreateLoad(
            Term("loop", Sense("loop-primary", aliases: ["agent loop"])),
            Term("agent-loop", Sense("loop-agent"))),
        CreateLoad(
            Term("loop", Sense("shared-sense")),
            Term("cycle", Sense("shared-sense")))
    };

    [Fact]
    public void Constructor_SnapshotsOuterAndNestedGlossaryCollections()
    {
        var scopes = new List<string> { "component:Gameplay" };
        var aliases = new List<string> { "gameplay loop" };
        var evidence = new List<string> { "claim-gameplay" };
        var senses = new List<GlossarySense>
        {
            new(
                "loop-gameplay",
                GlossarySenseStatus.Approved,
                "The gameplay update cycle.",
                scopes,
                aliases,
                evidence)
        };
        var terms = new List<GlossaryLookupResult> { new("loop", senses) };
        var contributor = new ManagedGlossaryGraphContributor(
            new ManagedGlossaryLoadResult(new AnalysisGlossary([]), terms));

        scopes.Clear();
        scopes.Add("component:Mutated");
        aliases.Clear();
        aliases.Add("mutated alias");
        evidence.Clear();
        evidence.Add("claim-mutated");
        senses.Clear();
        terms.Clear();
        terms.Add(Term("mutated", Sense("mutated-sense")));

        var contribution = contributor.Contribute(Documents(), Resolver());

        Assert.Contains(contribution.Nodes, node => node.Id == "term:loop");
        Assert.Contains(contribution.Nodes, node => node.Id == "sense:loop-gameplay"
            && node.Properties["definition"] == "The gameplay update cycle.");
        Assert.Contains(contribution.Nodes, node => node.Id == "term:gameplay-loop");
        Assert.Contains(
            new KyberWeave.Core.Docs.Graph.DocGraphEdge(
                "SCOPED_TO",
                "sense:loop-gameplay",
                "component:Gameplay"),
            contribution.Edges);
        Assert.Contains(
            new KyberWeave.Core.Docs.Graph.DocGraphEdge(
                "EVIDENCED_BY",
                "sense:loop-gameplay",
                "claim-gameplay"),
            contribution.Edges);
        Assert.DoesNotContain(contribution.Nodes, node => node.Id.Contains("mutated", StringComparison.Ordinal));
        Assert.DoesNotContain(contribution.Edges, edge => edge.To.Contains("mutated", StringComparison.Ordinal));
    }

    private static ManagedGlossaryService GlossaryService(string repositoryRoot) =>
        new(
            repositoryRoot,
            new KyberWeaveConfig
            {
                Ontology = OntologyConfig.ProductDefaults.WithDocsRoots(["docs"]),
                DocsAnalysis = new DocsAnalysisConfig { GlossaryPath = "docs/glossary.md" }
            },
            TimeProvider.System);

    private static ManagedGlossaryLoadResult CreateLoad(params GlossaryLookupResult[] terms) =>
        new(new AnalysisGlossary([]), terms);

    private static GlossaryLookupResult Term(string term, params GlossarySense[] senses) =>
        new(term, senses);

    private static GlossarySense Sense(
        string id,
        IReadOnlyList<string>? aliases = null) =>
        new(
            id,
            GlossarySenseStatus.Approved,
            $"Definition for {id}.",
            ["component:Gameplay"],
            aliases ?? [],
            ["claim-" + id]);

    private static TempDirectory GlossaryRepository()
    {
        var repository = new TempDirectory();
        var docs = Path.Combine(repository.Path, "docs");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "catalog.md"), """
            | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            | --- | --- | --- | --- | --- | --- | --- | --- |
            | Gameplay | Application | `src/Game` | [README](x) | [docs](y) | Gameplay maintainers | 2026-08-01 | Current |
            | Agents | Tool | `src/Agents` | [README](x) | [docs](y) | Agent maintainers | 2026-08-01 | Current |
            """);
        File.WriteAllText(Path.Combine(docs, "glossary.md"), """
            ---
            id: reference/glossary
            title: Glossary
            doc-type: reference
            status: current
            owner: Maintainers
            last-reviewed: 2026-08-12
            ---

            # Glossary

            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | approved | The gameplay update cycle. | component:Gameplay; code-ref:Game.Run | gameplay loop |
            | loop-agent | proposed | An autonomous agent cycle. | component:Agents | churn loop |
            | loop-legacy | rejected | A retired cycle. | component:Legacy | legacy loop |

            <!-- kyber-weave:glossary-evidence:start sense="loop-gameplay" -->
            - claim-gameplay
            <!-- kyber-weave:glossary-evidence:end -->

            <!-- kyber-weave:glossary-evidence:start sense="loop-agent" -->
            - claim-agent
            <!-- kyber-weave:glossary-evidence:end -->

            <!-- kyber-weave:glossary-evidence:start sense="loop-legacy" -->
            - claim-legacy
            <!-- kyber-weave:glossary-evidence:end -->
            """);
        return repository;
    }

    private static FakeCodeGraphResolver Resolver() => FakeCodeGraphResolver.WithSymbols(
        ("Game.Run", new CodeGraphNode(
            "code:game-run",
            "method",
            "Run",
            "Game.Run",
            "src/Game.cs",
            "csharp",
            10)));

    private static DocumentSet Documents() => new()
    {
        Documents =
        [
            new DocumentModel
            {
                RelativePath = "docs/gameplay.md",
                FilePath = "/repo/docs/gameplay.md",
                HasFrontmatter = true,
                Frontmatter = new DocumentFrontmatter
                {
                    Id = "reference/gameplay",
                    Title = "Gameplay",
                    DocType = "reference",
                    Status = "current",
                    Component = "Gameplay",
                    LastReviewed = "2026-08-12",
                    CodeRefs = new Collection<string>(["Game.Run"])
                },
                DocType = DocType.Reference,
                Status = DocStatus.Current
            }
        ]
    };

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    private static bool IsNode(JsonElement node, string id, string label) =>
        node.GetProperty("type").GetString() == "node"
        && node.GetProperty("id").GetString() == id
        && node.GetProperty("label").GetString() == label;

    private static bool IsEdge(JsonElement edge, string label, string from, string to) =>
        edge.GetProperty("type").GetString() == "edge"
        && edge.GetProperty("label").GetString() == label
        && edge.GetProperty("from").GetString() == from
        && edge.GetProperty("to").GetString() == to;
}
