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
    public void ICodeGraphNeighborhoodProviderIsOptionalForExistingResolvers()
    {
        ICodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols(
            ("Known", Node("known", "Known")));

        Assert.IsNotAssignableFrom<ICodeGraphNeighborhoodProvider>(resolver);
        Assert.Single(resolver.ResolveSymbol("Known"));
    }

    [Fact]
    public void CodeGraphResolverAdapterLoadsNodesAndEdgesBeforeTheIndexIsRemoved()
    {
        CodeGraphFixtureDb fixture = new CodeGraphFixtureDb();
        try
        {
            fixture.IndexSymbol("Caller", "src/Caller.cs", 10);
            fixture.IndexSymbol("Callee", "src/Callee.cs", 20);
            fixture.IndexEdge("id-Caller", "id-Callee", "calls");

            CodeGraphResolverAdapter adapter = new CodeGraphResolverAdapter(fixture.DatabasePath);
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
    public void CodeGraphResolverAdapterNeighborhoodQueryIsBatchedAndAppliesDegreeCap()
    {
        using CodeGraphFixtureDb fixture = new CodeGraphFixtureDb();
        fixture.IndexSymbol("Hub", "src/Hub.cs", 1);
        fixture.IndexSymbol("First", "src/First.cs", 2);
        fixture.IndexSymbol("Second", "src/Second.cs", 3);
        fixture.IndexEdge("id-Hub", "id-First", "calls");
        fixture.IndexEdge("id-Hub", "id-Second", "references");

        ICodeGraphNeighborhoodProvider provider = Assert.IsAssignableFrom<ICodeGraphNeighborhoodProvider>(
            new CodeGraphResolverAdapter(fixture.DatabasePath));

        IReadOnlyList<CodeGraphEdge> capped = provider.GetEdges(["id-Hub", "id-First", "id-Second"], maxDegree: 1);
        Assert.Empty(capped);

        var inclusive = provider.GetEdges(["id-Hub", "id-First", "id-Second"], maxDegree: 2);
        Assert.Equal(2, inclusive.Count);
    }

    private static CodeGraphNode Node(string id, string name) =>
        new(id, "class", name, name, $"src/{name}.cs", "csharp", 1);
}
