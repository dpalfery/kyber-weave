using KyberWeave.Core.CodeGraph;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T05 RED — CodeGraph neighborhood traversal is optional and batch-oriented. Existing
/// resolvers remain valid; adapters that expose neighborhoods preload them with the nodes.
/// </summary>
public sealed class CodeGraphNeighborhoodPortTests
{
    [Fact]
    public void ICodeGraphNeighborhoodProvider_Is_Optional_For_Existing_Resolvers()
    {
        ICodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols(
            ("Known", Node("known", "Known")));

        Assert.IsNotAssignableFrom<ICodeGraphNeighborhoodProvider>(resolver);
        Assert.Single(resolver.ResolveSymbol("Known"));
    }

    [Fact]
    public void CodeGraphResolverAdapter_Loads_Nodes_And_Edges_Before_The_Index_Is_Removed()
    {
        var fixture = new CodeGraphFixtureDb();
        try
        {
            fixture.IndexSymbol("Caller", "src/Caller.cs", 10);
            fixture.IndexSymbol("Callee", "src/Callee.cs", 20);
            fixture.IndexEdge("id-Caller", "id-Callee", "calls");

            var adapter = new CodeGraphResolverAdapter(fixture.DatabasePath);
            var provider = Assert.IsAssignableFrom<ICodeGraphNeighborhoodProvider>(adapter);

            // A neighborhood lookup must be in-memory. Removing the database after the
            // constructor proves GetEdges does not launch sqlite once per edge or node.
            fixture.Dispose();

            var edge = Assert.Single(provider.GetEdges(["id-Caller", "id-Callee"], maxDegree: 50));
            Assert.Equal(new CodeGraphEdge("id-Caller", "id-Callee", "calls"), edge);
            Assert.Single(adapter.ResolveSymbol("Caller"));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void CodeGraphResolverAdapter_Neighborhood_Query_Is_Batched_And_Applies_Degree_Cap()
    {
        using var fixture = new CodeGraphFixtureDb();
        fixture.IndexSymbol("Hub", "src/Hub.cs", 1);
        fixture.IndexSymbol("First", "src/First.cs", 2);
        fixture.IndexSymbol("Second", "src/Second.cs", 3);
        fixture.IndexEdge("id-Hub", "id-First", "calls");
        fixture.IndexEdge("id-Hub", "id-Second", "references");

        var provider = Assert.IsAssignableFrom<ICodeGraphNeighborhoodProvider>(
            new CodeGraphResolverAdapter(fixture.DatabasePath));

        var capped = provider.GetEdges(["id-Hub", "id-First", "id-Second"], maxDegree: 1);

        Assert.Empty(capped);
    }

    private static CodeGraphNode Node(string id, string name) =>
        new(id, "class", name, name, $"src/{name}.cs", "csharp", 1);
}
