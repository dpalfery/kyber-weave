using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.CodeGraph;
using KyberWeave.Domain.CodeGraph;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The CodeGraph resolver adapter loads nodes and edges from a real sqlite index once, and answers a batched
/// neighborhood query with a degree cap. These need the sqlite3 program and a database file.
/// </summary>
public sealed class CodeGraphNeighborhoodAdapterSqliteTests
{
    [Fact]
    public async Task CodeGraphResolverAdapterLoadsNodesAndEdgesBeforeTheIndexIsRemoved()
    {
        CodeGraphFixtureDb fixture = await CodeGraphFixtureDb.CreateAsync();
        try
        {
            await fixture.IndexSymbol("Caller", "src/Caller.cs", 10);
            await fixture.IndexSymbol("Callee", "src/Callee.cs", 20);
            await fixture.IndexEdge("id-Caller", "id-Callee", "calls");

            CodeGraphResolverAdapter adapter = DocsPorts.CodeGraphAt(fixture.DatabasePath);
            ICodeGraphNeighborhoodProvider provider = Assert.IsAssignableFrom<ICodeGraphNeighborhoodProvider>(adapter);

            // A neighborhood lookup must be in-memory. Removing the database after the
            // constructor proves GetEdges does not launch sqlite once per edge or node.
            fixture.Dispose();

            CodeGraphEdge edge = Assert.Single(provider.GetEdges(["id-Caller", "id-Callee"], maxDegree: 50));
            Assert.Equal(new CodeGraphEdge("id-Caller", "id-Callee", "calls"), edge);
            Assert.Single(adapter.ResolveSymbol("Caller"));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task CodeGraphResolverAdapterNeighborhoodQueryIsBatchedAndAppliesDegreeCap()
    {
        using CodeGraphFixtureDb fixture = await CodeGraphFixtureDb.CreateAsync();
        await fixture.IndexSymbol("Hub", "src/Hub.cs", 1);
        await fixture.IndexSymbol("First", "src/First.cs", 2);
        await fixture.IndexSymbol("Second", "src/Second.cs", 3);
        await fixture.IndexEdge("id-Hub", "id-First", "calls");
        await fixture.IndexEdge("id-Hub", "id-Second", "references");

        ICodeGraphNeighborhoodProvider provider = Assert.IsAssignableFrom<ICodeGraphNeighborhoodProvider>(
            DocsPorts.CodeGraphAt(fixture.DatabasePath));

        IReadOnlyList<CodeGraphEdge> capped = provider.GetEdges(["id-Hub", "id-First", "id-Second"], maxDegree: 1);
        Assert.Empty(capped);

        IReadOnlyList<CodeGraphEdge> inclusive = provider.GetEdges(["id-Hub", "id-First", "id-Second"], maxDegree: 2);
        Assert.Equal(2, inclusive.Count);
    }
}
