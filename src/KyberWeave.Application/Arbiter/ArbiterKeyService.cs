using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;
using YamlDotNet.Core;

namespace KyberWeave.Core.Arbiter;

/// <summary>Resolves the Arbiter key through the registered credential store, environment and user settings.</summary>
/// <param name="store">The credential store the key is read from.</param>
/// <param name="environment">The process environment: the home directory and the key variable.</param>
/// <param name="userSettings">Applies the user override to the repository provider.</param>
public sealed class ArbiterKeyService(
    ICredentialStore store,
    IProcessEnvironment environment,
    IArbiterUserSettings userSettings) : IArbiterKeyService
{
    /// <inheritdoc />
    public async Task<string?> ResolveKeyAsync(KyberWeaveConfig config, string endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return await ArbiterKeyResolver.ResolveAsync(
            ArbiterUserSettings.NormalizeEndpoint(endpoint), store, environment.GetVariable, UserOverrideEndpoint(config), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> ResolveKeyAsync(
        KyberWeaveConfig config, ArbiterProviderConfig effectiveProvider, string endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(effectiveProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return await ArbiterKeyResolver.ResolveAsync(
            ArbiterUserSettings.NormalizeEndpoint(endpoint),
            store,
            environment.GetVariable,
            UserOverrideEndpoint(config.Arbiter.Provider, effectiveProvider),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> TryResolveKeyAsync(KyberWeaveConfig config, string endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        try
        {
            return await ResolveKeyAsync(config, endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> TryResolveKeyAsync(
        KyberWeaveConfig config, ArbiterProviderConfig effectiveProvider, string endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(effectiveProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        try
        {
            return await ResolveKeyAsync(config, effectiveProvider, endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Func<Uri, CancellationToken, Task<string?>> CreateKeyResolverAsync(KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string? userOverrideEndpoint = UserOverrideEndpoint(config);
        return (uri, cancellationToken) => ArbiterKeyResolver.ResolveAsync(
            uri.ToString(), store, environment.GetVariable, userOverrideEndpoint, cancellationToken);
    }

    private string? UserOverrideEndpoint(KyberWeaveConfig config)
    {
        try
        {
            ArbiterProviderConfig repository = config.Arbiter.Provider;
            ArbiterProviderConfig merged = userSettings.ApplyTo(repository, environment.HomeDirectory);
            return UserOverrideEndpoint(repository, merged);
        }
        catch (YamlException)
        {
            return null;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return null;
        }
    }

    private static string? UserOverrideEndpoint(ArbiterProviderConfig repository, ArbiterProviderConfig merged)
    {
        string repositoryEndpoint = ArbiterUserSettings.NormalizeEndpoint(repository.Endpoint);
        string mergedEndpoint = ArbiterUserSettings.NormalizeEndpoint(merged.Endpoint);
        return mergedEndpoint.Length == 0 || string.Equals(mergedEndpoint, repositoryEndpoint, StringComparison.Ordinal)
            ? null
            : mergedEndpoint;
    }
}
