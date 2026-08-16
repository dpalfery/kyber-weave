namespace KyberWeave.Cli.Update;

/// <summary>
/// The host serving Release metadata and assets: github.com, or a loopback stand-in.
/// </summary>
/// <remarks>
/// <para>
/// The failures this override exists for — replacing a running single-file image, and
/// installing a freshly packed Squad archive — only reproduce against a real published
/// binary talking to a real Release endpoint. Without a redirect, the only way to exercise
/// either is to tag, push, and wait for the release workflow, which puts a fifteen-minute
/// round trip between a code change and the first signal about whether it worked. The
/// override collapses that to seconds; <c>scripts/update-loop.sh</c> is its only intended
/// caller.
/// </para>
/// <para>
/// <see cref="Resolve"/> accepts loopback authorities only. Setting an environment variable
/// already requires the ability to run code as this user, so the override grants no reach an
/// attacker would not already have — but a non-loopback value would turn it into a way to
/// aim an installer at an arbitrary host, so those are refused rather than trusted.
/// </para>
/// </remarks>
internal sealed record ReleaseOrigin(Uri ApiRoot, Uri DownloadRoot, bool IsLoopbackOverride)
{
    /// <summary>The environment variable naming a loopback Release host.</summary>
    internal const string EnvironmentVariable = "KYBER_WEAVE_RELEASE_ORIGIN";

    /// <summary>The github.com endpoints used by every ordinary install.</summary>
    internal static ReleaseOrigin Default { get; } = new(
        GitHubReleaseClient.ApiRoot,
        GitHubReleaseClient.ReleaseDownloadBase,
        IsLoopbackOverride: false);

    /// <summary>Reads the override, falling back to github.com when it is unset.</summary>
    /// <exception cref="SelfUpdateException">The override is set to a value that is not a loopback origin.</exception>
    internal static ReleaseOrigin Resolve(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);

        string? configured = readEnvironment(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
            return Default;

        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out Uri? origin))
        {
            throw new SelfUpdateException(
                $"{EnvironmentVariable} is not an absolute URL: '{configured}'.");
        }

        if (!IsLoopbackAuthority(origin))
        {
            throw new SelfUpdateException(
                $"{EnvironmentVariable} must point at 127.0.0.1, [::1], or localhost. Got '{origin.Host}'.");
        }

        if (!string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new SelfUpdateException(
                $"{EnvironmentVariable} must use http or https. Got '{origin.Scheme}'.");
        }

        // Trailing slash matters: Uri's relative-resolution rules drop the last segment of a
        // base without one, which would silently strip the port-bearing root.
        Uri root = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/");
        return new ReleaseOrigin(
            root,
            new Uri(root, $"{GitHubReleaseClient.Owner}/{GitHubReleaseClient.Repo}/releases/download/"),
            IsLoopbackOverride: true);
    }

    /// <summary>Rejects any URL this origin is not permitted to reach.</summary>
    /// <remarks>
    /// Plain HTTP is tolerated only for a loopback authority under an active override, so a
    /// redirect away from the local server — the case a downgrade attack would need — still
    /// has to be HTTPS.
    /// </remarks>
    internal void EnsureAllowed(Uri uri, string subject = "URL")
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return;

        if (IsLoopbackOverride && IsLoopbackAuthority(uri) && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return;

        throw new SelfUpdateException($"refusing non-HTTPS {subject}: {uri}");
    }

    internal static bool IsLoopbackAuthority(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
            return false;

        // Uri.IsLoopback covers 127.0.0.0/8, ::1 and "localhost"; the explicit host list
        // guards against a resolver that maps "localhost" somewhere else.
        return uri.IsLoopback
            && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6);
    }
}
