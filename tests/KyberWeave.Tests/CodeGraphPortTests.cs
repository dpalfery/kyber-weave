using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Export;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T3 — pluggable CodeGraph port contract. Drift, export, and indexing must accept
/// <see cref="ICodeGraphResolver"/> so tests and hosts can inject deterministic fakes.
/// Authored RED in task 1a; GREEN implements the port without changing contract intent.
/// </summary>
public class CodeGraphPortTests
{
    [Fact]
    public void DocDriftLinterAcceptsICodeGraphResolverAndFakeDrivesDriftFindings()
    {
        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols(
            ("KnownSymbol", Node("KnownSymbol", "1-Presentation/Api/Svc.cs", 10)));

        DocumentSet set = DocumentSetWithCodeRef("UnknownSymbol");
        DiagnosticReport report = new DocDriftLinter(resolver).Validate(set);

        Assert.Contains(report.Items, i => i.Code == DocDriftLinter.UnresolvedCodeRef);
        Assert.DoesNotContain(report.Items, i => i.Message.Contains("KnownSymbol", StringComparison.Ordinal));
    }

    [Fact]
    public void DocDriftLinterFakeResolverReportsUnresolvedEndpoint()
    {
        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithRoutes("GET /api/health");
        DocumentSet set = DocumentSetWithEndpoint("POST /api/missing");

        DiagnosticReport report = new DocDriftLinter(resolver).Validate(set);

        Assert.Contains(report.Items, i => i.Code == DocDriftLinter.UnresolvedEndpoint);
    }

    [Fact]
    public void DocumentIndexBuildAcceptsICodeGraphResolverAndFakeDrivesJoins()
    {
        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols(
            ("BillingService", Node("BillingService", "2-Application/Billing/Svc.cs", 42)));

        DocumentCorpus corpus = DocumentCorpus.Build(DocumentSetWithCodeRef("BillingService"));
        DocumentIndex index = DocumentIndex.Build(corpus, resolver);

        IReadOnlyList<DocumentHit> hits = index.ForSymbol("BillingService");
        DocumentHit hit = Assert.Single(hits);

        Assert.Contains(hit.CodeJoins, j => j.Reference == "BillingService" && j.Location.Contains("Svc.cs"));
    }

    [Fact]
    public void DocGraphExporterAcceptsICodeGraphResolverAndEmitsResolvedEdges()
    {
        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols(
            ("ExportTarget", Node("ExportTarget", "src/ExportTarget.cs", 1, kind: "class", id: "node-export-1")));

        DocumentSet set = DocumentSetWithCodeRef("ExportTarget");
        using TempDirectory output = new TempDirectory();

        DocGraphExportResult result = new DocGraphExporter(resolver).Export(set, output.Path);
        string[] edges = File.ReadAllLines(result.EdgesPath);

        Assert.Contains(edges, e => e.Contains("REFERENCES", StringComparison.Ordinal) && e.Contains("node-export-1", StringComparison.Ordinal));
    }

    [Fact]
    public void CodeGraphResolverAdapterPreservesResolveByNameRouteAndHasFilesUnder()
    {
        using CodeGraphFixtureDb fixture = new CodeGraphFixtureDb();
        fixture.IndexSymbol("AdapterSymbol", "3-Domain/Adapter/Symbol.cs", 7);
        fixture.IndexRoute("GET /api/adapter/ping");
        fixture.IndexFile("3-Domain/Adapter/Symbol.cs");

        ICodeGraphResolver adapter = new CodeGraphResolverAdapter(fixture.DatabasePath);

        Assert.True(adapter.IsAvailable);
        Assert.NotEmpty(adapter.ResolveSymbol("AdapterSymbol"));
        Assert.NotEmpty(adapter.ResolveRoute("GET /api/adapter/ping"));
        Assert.True(adapter.HasFilesUnder("3-Domain/Adapter"));
        Assert.NotEmpty(adapter.CandidateNames("Adapt"));
        Assert.Contains("GET /api/adapter/ping", adapter.AllRoutes());
    }

    [Fact]
    public void AdapterReadsEndLinesAndEnumeratesByKind()
    {
        // Guards the SELECT: duplicate detection needs the whole span, and a node with no
        // end line has no body to compare, so losing the column would silently empty the gate.
        using CodeGraphFixtureDb fixture = new CodeGraphFixtureDb();
        fixture.IndexMethod("Total", "src/Adapter/Sums.cs", 10, 24);
        fixture.IndexMethod("Count", "src/Adapter/Sums.cs", 26, 30);
        fixture.IndexSymbol("Sums", "src/Adapter/Sums.cs", 5, 40);

        ICodeGraphSymbolEnumerator adapter = new CodeGraphResolverAdapter(fixture.DatabasePath);
        IReadOnlyList<CodeGraphNode> methods = adapter.NodesOfKind("method");

        Assert.Equal(2, methods.Count);
        CodeGraphNode total = methods.Single(m => m.Name == "Total");
        Assert.Equal(10, total.StartLine);
        Assert.Equal(24, total.EndLine);
        Assert.Equal(15, total.LineSpan);
        Assert.Empty(adapter.NodesOfKind("interface"));
    }

    private static DocumentSet DocumentSetWithCodeRef(string symbol) =>
        new()
        {
            Documents =
            [
                new DocumentModel
                {
                    RelativePath = "6-Docs/reference/thing.md",
                    FilePath = "/tmp/6-Docs/reference/thing.md",
                    HasFrontmatter = true,
                    Frontmatter = new DocumentFrontmatter
                    {
                        Id = "reference/thing",
                        Title = "Thing",
                        DocType = "reference",
                        Status = "current",
                        Owner = "Maintainers",
                        LastReviewed = "2026-07-21",
                        CodeRefs = [symbol]
                    },
                    DocType = DocType.Reference,
                    Status = DocStatus.Current
                }
            ]
        };

    private static DocumentSet DocumentSetWithEndpoint(string route) =>
        new()
        {
            Documents =
            [
                new DocumentModel
                {
                    RelativePath = "6-Docs/reference/endpoint.md",
                    FilePath = "/tmp/6-Docs/reference/endpoint.md",
                    HasFrontmatter = true,
                    Frontmatter = new DocumentFrontmatter
                    {
                        Id = "reference/endpoint",
                        Title = "Endpoint",
                        DocType = "reference",
                        Status = "current",
                        Owner = "Maintainers",
                        LastReviewed = "2026-07-21",
                        ApiEndpoints = [route]
                    },
                    DocType = DocType.Reference,
                    Status = DocStatus.Current
                }
            ]
        };

    private static CodeGraphNode Node(
        string name,
        string filePath,
        int startLine,
        string kind = "class",
        string? id = null) =>
        new(id ?? $"node-{name}", kind, name, name, filePath, "csharp", startLine);
}
