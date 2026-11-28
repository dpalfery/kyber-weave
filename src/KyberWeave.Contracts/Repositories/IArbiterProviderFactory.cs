using KyberWeave.Core.Arbiter.Providers;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Creates the decision-model providers that call a remote or local model over HTTP.</summary>
public interface IArbiterProviderFactory
{
    /// <summary>Creates the <c>systemone</c> client the product runs, with lens instructions wired in.</summary>
    /// <param name="handler">The transport; owned and disposed with the client.</param>
    /// <param name="endpoint">The provider base URL.</param>
    /// <param name="model">The answering model, which also selects the budget.</param>
    /// <param name="timeoutMs">The per-call budget covering retries.</param>
    /// <param name="keyResolver">Returns the key for the endpoint's origin, or null, without blocking a thread.</param>
    /// <returns>The provider; disposing it releases the transport.</returns>
    IArbiterProvider CreateSystemOne(
        HttpMessageHandler handler,
        Uri endpoint,
        string model,
        int timeoutMs,
        Func<Uri, CancellationToken, Task<string?>>? keyResolver);

    /// <summary>Creates the <c>systemone</c> client over the default HTTP transport.</summary>
    /// <param name="endpoint">The provider base URL.</param>
    /// <param name="model">The answering model, which also selects the budget.</param>
    /// <param name="timeoutMs">The per-call budget covering retries.</param>
    /// <param name="keyResolver">Returns the key for the endpoint's origin, or null, without blocking a thread.</param>
    /// <returns>The provider; disposing it releases the transport.</returns>
    IArbiterProvider CreateSystemOne(
        Uri endpoint,
        string model,
        int timeoutMs,
        Func<Uri, CancellationToken, Task<string?>>? keyResolver);
}
