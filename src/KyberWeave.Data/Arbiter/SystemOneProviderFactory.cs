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
}
