using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.CodeGraph;

namespace KyberWeave.Core.CodeGraph;

/// <summary>The one place the entry apps build a CodeGraph adapter, without referencing the data layer.</summary>
/// <remarks>
/// The adapter reads the index through <c>sqlite3</c> and so lives in <c>KyberWeave.Data</c>, which entry apps never
/// reference. Core does reference Data, so it is the lowest project both the adapter and the hosts can see: it
/// re-exports the construction here rather than each host naming the adapter class. This is the legacy wire the
/// layering keeps until the entry apps are thinned onto <c>ICodeGraphResolverFactory</c> from the container.
/// </remarks>
public static class CodeGraphResolvers
{
    /// <summary>Builds the ports over the index under <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The checkout whose <c>.codegraph/codegraph.db</c> is read.</param>
    /// <returns>
    /// One object holding every port the loaded adapter answers, so a caller takes the ports it needs from a single
    /// index read instead of naming the adapter class.
    /// </returns>
    public static CodeGraphPorts ForRepository(string repositoryRoot)
    {
        CodeGraphResolverAdapter adapter = CodeGraphResolverAdapter.ForRepository(repositoryRoot);
        return new CodeGraphPorts(adapter, adapter, adapter, adapter);
    }
}

/// <summary>The ports one loaded CodeGraph index answers.</summary>
/// <param name="Resolver">Symbol and route lookups.</param>
/// <param name="Neighborhood">Batched one-hop relationships between resolved nodes.</param>
/// <param name="Enumerator">Every indexed symbol of one kind.</param>
/// <param name="Freshness">How far the index lags the checkout.</param>
/// <remarks>
/// All four are the same adapter instance, so they share its single read of the index.
/// </remarks>
public sealed record CodeGraphPorts(
    ICodeGraphResolver Resolver,
    ICodeGraphNeighborhoodProvider Neighborhood,
    ICodeGraphSymbolEnumerator Enumerator,
    ICodeGraphIndexFreshness Freshness);
