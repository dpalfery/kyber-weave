using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Providers;

namespace KyberWeave.Data.Arbiter;

/// <summary>Creates the <c>systemone</c> HTTP client over the transport and clock seams.</summary>
/// <param name="handlers">Supplies the default transport.</param>
/// <param name="clock">Dates each client's retry deadline.</param>
public sealed class SystemOneProviderFactory(IArbiterHttpHandlers handlers, TimeProvider clock) : IArbiterProviderFactory
{
    /// <inheritdoc />
    public IArbiterProvider CreateSystemOne(
        HttpMessageHandler handler,
        Uri endpoint,
        string model,
        int timeoutMs,
        Func<Uri, CancellationToken, Task<string?>>? keyResolver) =>
        SystemOneClient.CreateForProduct(handler, endpoint, model, timeoutMs, clock, keyResolver);

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler's lifetime transfers to the returned provider, which the caller disposes after the evaluation.")]
    public IArbiterProvider CreateSystemOne(
        Uri endpoint,
        string model,
        int timeoutMs,
        Func<Uri, CancellationToken, Task<string?>>? keyResolver) =>
        CreateSystemOne(handlers.Create(), endpoint, model, timeoutMs, keyResolver);
}
