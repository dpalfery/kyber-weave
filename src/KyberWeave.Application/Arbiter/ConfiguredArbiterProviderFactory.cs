using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Arbiter;

/// <summary>
/// The decision model a process outside the Arbiter hook may ask (the docs tools that write a task file): the
/// repository's provider with the user's override applied, and the key resolved the way the hook resolves it.
/// </summary>
/// <remarks>
/// Optional by design. Provider <c>none</c>, an endpoint that is not a URL, a missing key or a credential store that
/// cannot be read all answer null, without a network call, so a host with no model pays nothing and sees nothing.
/// </remarks>
/// <param name="userSettings">Applies the user override to the repository provider.</param>
/// <param name="environment">Where the key's environment variable is read.</param>
/// <param name="handlers">Supplies the transport when the caller names none.</param>
/// <param name="clock">Dates each client's retry deadline.</param>
/// <param name="credentialStore">Builds the operating system's credential store; called once per provider, so a failure to build it is absorbed there.</param>
public sealed class ConfiguredArbiterProviderFactory(
    IArbiterUserSettings userSettings,
    IProcessEnvironment environment,
    IArbiterHttpHandlers handlers,
    TimeProvider clock,
    Func<ICredentialStore> credentialStore)
{
    /// <summary>The provider to ask, or null when none is configured or reachable without a call.</summary>
    /// <param name="repositoryProvider">The provider the repository configures.</param>
    /// <param name="homeDirectory">The user's home directory, where the override lives.</param>
    /// <param name="handler">The HTTP handler; null for the transport the handler seam creates.</param>
    /// <param name="storeFactory">The credential store; null for the operating system's.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler's lifetime transfers to the returned provider, which the caller disposes.")]
    public IArbiterProvider? TryCreate(
        ArbiterProviderConfig repositoryProvider,
        string homeDirectory,
        HttpMessageHandler? handler = null,
        Func<ICredentialStore>? storeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryProvider);
        ArgumentNullException.ThrowIfNull(homeDirectory);

        ArbiterProviderConfig provider;
        try
        {
            provider = userSettings.ApplyTo(repositoryProvider, homeDirectory);
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            provider = repositoryProvider;
        }

        if (provider.Kind == ArbiterProviderKind.None
            || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            return null;
        }

        string? userOverride = string.Equals(provider.Endpoint, repositoryProvider.Endpoint, StringComparison.Ordinal)
            ? null
            : provider.Endpoint;
        ICredentialStore? store;
        try
        {
            store = (storeFactory ?? credentialStore)();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            store = null;
        }

        Func<Uri, string?> keyResolver = uri =>
        {
            try
            {
                return ArbiterKeyResolver.Resolve(uri.ToString(), store, environment.GetVariable, userOverride);
            }
            catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        };

        if (!endpoint.IsLoopback && keyResolver(endpoint) is null)
            return null;

        return SystemOneClient.CreateForProduct(
            handler ?? handlers.Create(), endpoint, provider.Model, provider.TimeoutMs, clock, keyResolver);
    }

    /// <summary>The provider to ask without blocking a thread, or null when none is configured or reachable without a call.</summary>
    /// <param name="repositoryProvider">The provider the repository configures.</param>
    /// <param name="homeDirectory">The user's home directory, where the override lives.</param>
    /// <param name="handler">The HTTP handler; null for the transport the handler seam creates.</param>
    /// <param name="storeFactory">The credential store; null for the operating system's.</param>
    /// <param name="cancellationToken">Cancels the key resolution.</param>
    public async Task<IArbiterProvider?> TryCreateAsync(
        ArbiterProviderConfig repositoryProvider,
        string homeDirectory,
        HttpMessageHandler? handler = null,
        Func<ICredentialStore>? storeFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryProvider);
        ArgumentNullException.ThrowIfNull(homeDirectory);

        ArbiterProviderConfig provider;
        try
        {
            provider = userSettings.ApplyTo(repositoryProvider, homeDirectory);
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            provider = repositoryProvider;
        }

        if (provider.Kind == ArbiterProviderKind.None
            || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            return null;
        }

        string? userOverride = string.Equals(provider.Endpoint, repositoryProvider.Endpoint, StringComparison.Ordinal)
            ? null
            : provider.Endpoint;
        ICredentialStore? store;
        try
        {
            store = (storeFactory ?? credentialStore)();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            store = null;
        }

        Func<Uri, CancellationToken, Task<string?>> keyResolver = async (uri, token) =>
        {
            try
            {
                return await ArbiterKeyResolver.ResolveAsync(uri.ToString(), store, environment.GetVariable, userOverride, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        };

        if (!endpoint.IsLoopback && await keyResolver(endpoint, cancellationToken).ConfigureAwait(false) is null)
            return null;

        // The client's per-request resolver stays synchronous until the provider seam takes an async one.
        Func<Uri, string?> requestKeyResolver = uri =>
        {
            try
            {
                return ArbiterKeyResolver.Resolve(uri.ToString(), store, environment.GetVariable, userOverride);
            }
            catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        };

        return SystemOneClient.CreateForProduct(
            CreateTransport(handler), endpoint, provider.Model, provider.TimeoutMs, clock, requestKeyResolver);
    }

    /// <summary>The transport to hand the client: the caller's, or the one the handler seam creates.</summary>
    private HttpMessageHandler CreateTransport(HttpMessageHandler? handler) => handler ?? handlers.Create();
}
