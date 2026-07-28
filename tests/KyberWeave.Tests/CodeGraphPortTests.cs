using KyberWeave.Core.CodeGraph;
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
    public void DocDriftLinter_Accepts_ICodeGraphResolver_And_Fake_Drives_Drift_Findings()
    {
        var resolver = FakeCodeGraphResolver.WithSymbols(
            ("KnownSymbol", Node("KnownSymbol", "1-Presentation/Api/Svc.cs", 10)));

        var set = DocumentSetWithCodeRef("UnknownSymbol");
        var report = new DocDriftLinter(resolver).Validate(set);

        Assert.Contains(report.Items, i => i.Code == DocDriftLinter.UnresolvedCodeRef);
        Assert.DoesNotContain(report.Items, i => i.Message.Contains("KnownSymbol", StringComparison.Ordinal));
    }

    [Fact]
    public void DocDriftLinter_Fake_Resolver_Reports_Unresolved_Endpoint()
    {
        var resolver = FakeCodeGraphResolver.WithRoutes("GET /api/health");
        var set = DocumentSetWithEndpoint("POST /api/missing");

        var report = new DocDriftLinter(resolver).Validate(set);

        Assert.Contains(report.Items, i => i.Code == DocDriftLinter.UnresolvedEndpoint);
    }

    [Fact]
    public void DocumentIndex_Build_Accepts_ICodeGraphResolver_And_Fake_Drives_Joins()
    {
        var resolver = FakeCodeGraphResolver.WithSymbols(
            ("BillingService", Node("BillingService", "2-Application/Billing/Svc.cs", 42)));

        var corpus = DocumentCorpus.Build(DocumentSetWithCodeRef("BillingService"));
        var index = DocumentIndex.Build(corpus, resolver);

        var hits = index.ForSymbol("BillingService");
        var hit = Assert.Single(hits);

        Assert.Contains(hit.CodeJoins, j => j.Reference == "BillingService" && j.Location.Contains("Svc.cs"));
    }

    [Fact]
    public void DocGraphExporter_Accepts_ICodeGraphResolver_And_Emits_Resolved_Edges()
    {
        var resolver = FakeCodeGraphResolver.WithSymbols(
            ("ExportTarget", Node("ExportTarget", "src/ExportTarget.cs", 1, kind: "class", id: "node-export-1")));

        var set = DocumentSetWithCodeRef("ExportTarget");
        using var output = new TempDirectory();

        var result = new DocGraphExporter(resolver).Export(set, output.Path);
        var edges = File.ReadAllLines(result.EdgesPath);

        Assert.Contains(edges, e => e.Contains("REFERENCES", StringComparison.Ordinal) && e.Contains("node-export-1", StringComparison.Ordinal));
    }

    [Fact]
    public void CodeGraphResolver_Adapter_Preserves_Resolve_By_Name_Route_And_HasFilesUnder()
    {
        using var fixture = new CodeGraphFixtureDb();
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
