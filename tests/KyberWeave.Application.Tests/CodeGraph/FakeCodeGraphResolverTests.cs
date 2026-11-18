using KyberWeave.Core.CodeGraph;
using KyberWeave.Domain.CodeGraph;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The shared CodeGraph fake answers every port it claims, so a caller's test cannot pass on an answer the index
/// would never give.
/// </summary>
public sealed class FakeCodeGraphResolverTests
{
    private static CodeGraphNode Node(string kind, string name) =>
        new(name + "-id", kind, name, "Acme." + name, "src/" + name + ".cs", "csharp", 3);

    [Fact]
    public void ASymbolGivenByNameIsAlsoEnumerableByItsKind()
    {
        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols(
            ("OrderService", Node("class", "OrderService")));

        CodeGraphNode node = Assert.Single(resolver.NodesOfKind("class"));

        Assert.Equal("OrderService", node.Name);
        Assert.Empty(resolver.NodesOfKind("method"));
    }

    [Fact]
    public void ThePortsFactoryAnswersEveryPortFromTheResolverItWasGiven()
    {
        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithNodes(Node("method", "Save"));
        FakeCodeGraphPortsFactory factory = new(resolver);

        CodeGraphPorts ports = factory.ForRepository("/repo");

        Assert.Same(resolver, ports.Resolver);
        Assert.Same(resolver, ports.Enumerator);
        Assert.Single(ports.Enumerator.NodesOfKind("method"));
        Assert.Equal(["/repo"], factory.Roots);
    }

    [Fact]
    public void ThePortsFactoryRefusesANullResolver()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new FakeCodeGraphPortsFactory(null!));
    }
}
