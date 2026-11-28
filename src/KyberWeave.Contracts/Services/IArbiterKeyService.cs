using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Services;

/// <summary>Resolves the Arbiter key for a provider endpoint from the credential store and the environment.</summary>
public interface IArbiterKeyService
{
    /// <summary>
    /// Resolves the key for <paramref name="endpoint"/> without blocking a thread, binding the environment key to the
    /// TypeSafe origin or the user override's endpoint. A loopback endpoint needs no key and returns null.
    /// </summary>
    /// <param name="config">The configuration whose provider the user override applies to.</param>
    /// <param name="endpoint">The provider endpoint.</param>
    /// <param name="cancellationToken">Cancels the credential-store lookup.</param>
    /// <returns>The key, or null when none resolves.</returns>
    Task<string?> ResolveKeyAsync(KyberWeaveConfig config, string endpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the key for <paramref name="endpoint"/> without blocking a thread using a pre-resolved
    /// <paramref name="effectiveProvider"/> instead of re-reading the per-user override. A loopback endpoint needs no
    /// key and returns null.
    /// </summary>
    /// <param name="config">The repository configuration before the per-user override is applied.</param>
    /// <param name="effectiveProvider">The merged provider already read from the per-user override.</param>
    /// <param name="endpoint">The provider endpoint.</param>
    /// <param name="cancellationToken">Cancels the credential-store lookup.</param>
    /// <returns>The key, or null when none resolves.</returns>
    Task<string?> ResolveKeyAsync(
        KyberWeaveConfig config, ArbiterProviderConfig effectiveProvider, string endpoint, CancellationToken cancellationToken);

    /// <summary>Resolves the key without blocking a thread, treating a credential store that cannot be read as an absent key.</summary>
    /// <param name="config">The configuration whose provider the user override applies to.</param>
    /// <param name="endpoint">The provider endpoint.</param>
    /// <param name="cancellationToken">Cancels the credential-store lookup.</param>
    /// <returns>The key, or null when none resolves or the store failed.</returns>
    Task<string?> TryResolveKeyAsync(KyberWeaveConfig config, string endpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the key without blocking a thread using a pre-resolved <paramref name="effectiveProvider"/>, treating a
    /// credential store that cannot be read as an absent key.
    /// </summary>
    /// <param name="config">The repository configuration before the per-user override is applied.</param>
    /// <param name="effectiveProvider">The merged provider already read from the per-user override.</param>
    /// <param name="endpoint">The provider endpoint.</param>
    /// <param name="cancellationToken">Cancels the credential-store lookup.</param>
    /// <returns>The key, or null when none resolves or the store failed.</returns>
    Task<string?> TryResolveKeyAsync(
        KyberWeaveConfig config, ArbiterProviderConfig effectiveProvider, string endpoint, CancellationToken cancellationToken);

    /// <summary>Creates a resolver that asks for the key of the endpoint it calls without blocking a thread.</summary>
    /// <param name="config">The configuration whose provider the user override applies to.</param>
    /// <returns>A resolver from endpoint and cancellation token to key.</returns>
    Func<Uri, CancellationToken, Task<string?>> CreateKeyResolverAsync(KyberWeaveConfig config);
}
