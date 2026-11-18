using KyberWeave.Core.Docs.Analysis;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Creates the client for the local embedding endpoint.</summary>
public interface IEmbeddingGeneratorFactory
{
    /// <summary>Creates a client over a transport of its own.</summary>
    /// <returns>A new generator. The caller disposes it when it is <see cref="IDisposable"/>.</returns>
    IEmbeddingGenerator Create();
}
