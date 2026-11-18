using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Embeddings;

namespace KyberWeave.Data.Docs.Analysis.Embeddings;

/// <summary>Creates <see cref="OpenAiCompatibleEmbeddingGenerator"/> clients over the registered transport.</summary>
/// <param name="transport">The HTTP transport and name resolution each client calls the endpoint through.</param>
/// <param name="environment">Reads the variable that holds the endpoint's bearer token.</param>
/// <param name="clock">Times each client's per-call limit.</param>
public sealed class OpenAiCompatibleEmbeddingGeneratorFactory(
    IEmbeddingTransport transport,
    IProcessEnvironment environment,
    TimeProvider clock) : IEmbeddingGeneratorFactory
{
    /// <inheritdoc />
    public IEmbeddingGenerator Create() =>
        new OpenAiCompatibleEmbeddingGenerator(transport, environment, clock);
}
