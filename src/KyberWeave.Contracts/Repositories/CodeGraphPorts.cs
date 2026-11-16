using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.CodeGraph;

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
