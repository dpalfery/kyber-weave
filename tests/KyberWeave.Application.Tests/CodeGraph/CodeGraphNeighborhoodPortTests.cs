using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.CodeGraph;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T05 RED — CodeGraph neighborhood traversal is optional and batch-oriented. Existing
/// resolvers remain valid; the adapter that exposes neighborhoods is proved over a real index in
/// <c>CodeGraphNeighborhoodAdapterSqliteTests</c> in the integration project.
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

    private static CodeGraphNode Node(string id, string name) =>
        new(id, "class", name, name, $"src/{name}.cs", "csharp", 1);
}
