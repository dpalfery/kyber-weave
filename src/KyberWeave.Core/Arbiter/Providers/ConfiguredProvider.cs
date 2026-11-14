using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Arbiter.Providers;

/// <summary>
/// The decision model a process outside the Arbiter hook may ask (the docs tools that write a task file): the
/// repository's provider with the user's override applied, and the key resolved the way the hook resolves it.
/// </summary>
/// <remarks>
/// Optional by design. Provider <c>none</c>, an endpoint that is not a URL, a missing key or a credential store that
/// cannot be read all answer null, without a network call, so a host with no model pays nothing and sees nothing.
/// </remarks>
public static class ConfiguredProvider
{
    /// <summary>The provider to ask, or null when none is configured or reachable without a call.</summary>
    /// <param name="config">The repository configuration.</param>
    /// <param name="homeDirectory">The user's home directory, where the override lives.</param>
    /// <param name="handler">The HTTP handler; null for a new <see cref="HttpClientHandler"/>.</param>
    /// <param name="storeFactory">The credential store; null for the operating system's.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler's lifetime transfers to the returned provider, which the caller disposes.")]
    public static IArbiterProvider? TryCreate(
        KyberWeaveConfig config,
        string homeDirectory,
        HttpMessageHandler? handler = null,
        Func<ICredentialStore>? storeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(homeDirectory);

        ArbiterProviderConfig provider;
        try
        {
            provider = ArbiterUserSettings.ApplyTo(config.Arbiter.Provider, homeDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or YamlDotNet.Core.YamlException)
        {
            provider = config.Arbiter.Provider;
        }

        if (provider.Kind == ArbiterProviderKind.None
            || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            return null;
        }

        string? userOverride = string.Equals(provider.Endpoint, config.Arbiter.Provider.Endpoint, StringComparison.Ordinal)
            ? null
            : provider.Endpoint;
        ICredentialStore? store;
        try
        {
            store = (storeFactory ?? CreateStore)();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            store = null;
        }

        Func<Uri, string?> keyResolver = uri =>
        {
            try
            {
                return ArbiterKeyResolver.Resolve(uri.ToString(), store, userOverride);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
                                                  or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        };

        if (!endpoint.IsLoopback && keyResolver(endpoint) is null)
            return null;

        return SystemOneClient.CreateForProduct(
            handler ?? new HttpClientHandler(), endpoint, provider.Model, provider.TimeoutMs, keyResolver);
    }

    private static ICredentialStore CreateStore()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsCredentialStore();
        if (OperatingSystem.IsMacOS())
            return new MacKeychainCredentialStore(new ProcessRunnerCredentialProcessRunner());
        return new SecretServiceCredentialStore(new ProcessRunnerCredentialProcessRunner());
    }
}
