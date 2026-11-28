using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.CodeGraph;
using KyberWeave.Domain.CodeGraph;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The CodeGraph resolver adapter over a real sqlite index: it resolves symbols and routes, answers path and
/// name queries, and reads end lines. These need the sqlite3 program and a database file, so they are not unit tests.
/// </summary>
public class CodeGraphAdapterSqliteTests
{
    [Fact]
    public async Task CodeGraphResolverAdapterPreservesResolveByNameRouteAndHasFilesUnder()
    {
        using CodeGraphFixtureDb fixture = await CodeGraphFixtureDb.CreateAsync();
        await fixture.IndexSymbol("AdapterSymbol", "3-Domain/Adapter/Symbol.cs", 7);
        await fixture.IndexRoute("GET /api/adapter/ping");
        await fixture.IndexFile("3-Domain/Adapter/Symbol.cs");

        ICodeGraphResolver adapter = DocsPorts.CodeGraphAt(fixture.DatabasePath);

        Assert.True(adapter.IsAvailable);
        Assert.NotEmpty(adapter.ResolveSymbol("AdapterSymbol"));
        Assert.NotEmpty(adapter.ResolveRoute("GET /api/adapter/ping"));
        Assert.True(adapter.HasFilesUnder("3-Domain/Adapter"));
        Assert.NotEmpty(adapter.CandidateNames("Adapt"));
        Assert.Contains("GET /api/adapter/ping", adapter.AllRoutes());
    }

    [Fact]
    public async Task AdapterReadsEndLinesAndEnumeratesByKind()
    {
        // Guards the SELECT: duplicate detection needs the whole span, and a node with no
        // end line has no body to compare, so losing the column would silently empty the gate.
        using CodeGraphFixtureDb fixture = await CodeGraphFixtureDb.CreateAsync();
        await fixture.IndexMethod("Total", "src/Adapter/Sums.cs", 10, 24);
        await fixture.IndexMethod("Count", "src/Adapter/Sums.cs", 26, 30);
        await fixture.IndexSymbol("Sums", "src/Adapter/Sums.cs", 5, 40);

        ICodeGraphSymbolEnumerator adapter = DocsPorts.CodeGraphAt(fixture.DatabasePath);
        IReadOnlyList<CodeGraphNode> methods = adapter.NodesOfKind("method");

        Assert.Equal(2, methods.Count);
        CodeGraphNode total = methods.Single(m => m.Name == "Total");
        Assert.Equal(10, total.StartLine);
        Assert.Equal(24, total.EndLine);
        Assert.Equal(15, total.LineSpan);
        Assert.Empty(adapter.NodesOfKind("interface"));
    }
}
