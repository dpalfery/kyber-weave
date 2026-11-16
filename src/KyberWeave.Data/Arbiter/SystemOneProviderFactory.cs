using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Providers;

namespace KyberWeave.Data.Arbiter;

/// <summary>Creates the <c>systemone</c> HTTP client.</summary>
public sealed class SystemOneProviderFactory : IArbiterProviderFactory
{
    /// <inheritdoc />
    public IArbiterProvider CreateSystemOne(
        HttpMessageHandler handler,
        Uri endpoint,
        string model,
        int timeoutMs,
        Func<Uri, string?>? keyResolver) =>
        SystemOneClient.CreateForProduct(handler, endpoint, model, timeoutMs, keyResolver);

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler's lifetime transfers to the returned provider, which the caller disposes after the evaluation.")]
    public IArbiterProvider CreateSystemOne(
        Uri endpoint,
        string model,
        int timeoutMs,
        Func<Uri, string?>? keyResolver) =>
        CreateSystemOne(new HttpClientHandler(), endpoint, model, timeoutMs, keyResolver);
}
