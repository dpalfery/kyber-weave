using KyberWeave.Core.Networking;

namespace KyberWeave.Core.Arbiter.Credentials;

/// <summary>Resolves the TypeSafe key the same way in every process.</summary>
/// <remarks>
/// Resolution order per endpoint origin: <c>TYPESAFE_API_KEY</c>, then the
/// credential-store entry for the endpoint's origin, then no key. The environment key
/// is bound to its origin — it serves only the TypeSafe origin or an origin named in
/// the user override — so a repository whose configuration points elsewhere cannot
/// obtain the user's key. A loopback endpoint needs no key and consults neither source.
/// The key is returned, never logged, and never interpolated into an exception.
/// </remarks>
public static class ArbiterKeyResolver
{
    /// <summary>Environment variable holding the TypeSafe key.</summary>
    public const string EnvVarName = "TYPESAFE_API_KEY";

    /// <summary>Origin the environment key always serves.</summary>
    public const string TypeSafeOrigin = "https://api.typesafe.ai";

    /// <summary>
    /// Resolves the key for <paramref name="endpoint"/>, reading the environment through
    /// <paramref name="environment"/> for testability.
    /// </summary>
    /// <param name="endpoint">The provider endpoint URL.</param>
    /// <param name="store">The OS credential store, or null when unavailable.</param>
    /// <param name="environment">Maps variable names to values.</param>
    /// <param name="userOverrideEndpoint">
    /// The user override's <c>provider.endpoint</c>, when one names a non-TypeSafe origin
    /// the environment key may serve.
    /// </param>
    /// <returns>The key, or null when none resolves or the endpoint is loopback.</returns>
    public static string? Resolve(
        string endpoint,
        ICredentialStore? store,
        Func<string, string?> environment,
        string? userOverrideEndpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(environment);

        if (IsLoopbackEndpoint(endpoint))
        {
            return null;
        }

        string? origin = GetOrigin(endpoint);
        if (origin is null)
        {
            return null;
        }

        string? envKey = environment(EnvVarName);
        if (!string.IsNullOrWhiteSpace(envKey) && IsEnvOrigin(origin, userOverrideEndpoint))
        {
            return envKey;
        }

        string? stored = store?.Read(origin);
        return string.IsNullOrWhiteSpace(stored) ? null : stored;
    }

    /// <summary>
    /// Resolves the key for <paramref name="endpoint"/> against the process environment.
    /// </summary>
    public static string? Resolve(
        string endpoint,
        ICredentialStore? store,
        string? userOverrideEndpoint = null) =>
        Resolve(endpoint, store, Environment.GetEnvironmentVariable, userOverrideEndpoint);

    /// <summary>Derives the <c>scheme://host[:port]</c> origin of an endpoint URL.</summary>
    /// <returns>The origin, or null when the endpoint is not an absolute http(s) URL.</returns>
    public static string? GetOrigin(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return null;
        }

        string host = uri.Host.Contains(':', StringComparison.Ordinal) ? $"[{uri.Host}]" : uri.Host;
        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    /// <summary>Whether <paramref name="endpoint"/> is loopback and needs no key.</summary>
    public static bool IsLoopbackEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return IsLoopbackHost(uri.Host);
    }

    private static bool IsEnvOrigin(string origin, string? userOverrideEndpoint)
    {
        if (string.Equals(origin, TypeSafeOrigin, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(userOverrideEndpoint))
        {
            return false;
        }

        return string.Equals(origin, GetOrigin(userOverrideEndpoint), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a host is loopback. Mirrors the validator's endpoint-scheme check so the
    /// key is refused on plain HTTP everywhere except where no key is sent at all.
    /// </summary>
    private static bool IsLoopbackHost(string host) =>
        LoopbackAddress.IsLoopbackHost(host);
}
