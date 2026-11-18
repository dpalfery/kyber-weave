using KyberWeave.Cli.Update;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Covers the loopback Release-origin override that <c>scripts/update-loop.sh</c> depends on.
/// </summary>
/// <remarks>
/// The security-relevant assertions here are the negative ones: the override must not be
/// usable to aim an installer at a host the user did not choose, and must not become a way
/// to downgrade a real github.com request to plain HTTP.
/// </remarks>
public sealed class ReleaseOriginTests
{
    private static Func<string, string?> Env(string? value) =>
        name => name == ReleaseOrigin.EnvironmentVariable ? value : null;

    [Fact]
    public void UnsetOverrideResolvesToGitHub()
    {
        ReleaseOrigin origin = ReleaseOrigin.Resolve(Env(null));

        Assert.False(origin.IsLoopbackOverride);
        Assert.Equal(GitHubReleaseClient.ApiRoot, origin.ApiRoot);
        Assert.Equal(GitHubReleaseClient.ReleaseDownloadBase, origin.DownloadRoot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOverrideResolvesToGitHub(string value)
    {
        Assert.False(ReleaseOrigin.Resolve(Env(value)).IsLoopbackOverride);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://127.0.0.1:8443")]
    [InlineData("http://[::1]:8080")]
    public void LoopbackOverrideIsAccepted(string value)
    {
        ReleaseOrigin origin = ReleaseOrigin.Resolve(Env(value));

        Assert.True(origin.IsLoopbackOverride);
        Assert.EndsWith("/", origin.ApiRoot.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith(
            "/dpalfery/kyber-weave/releases/download/",
            origin.DownloadRoot.AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OverridePreservesThePort()
    {
        ReleaseOrigin origin = ReleaseOrigin.Resolve(Env("http://127.0.0.1:54321"));

        Assert.Equal("http://127.0.0.1:54321/", origin.ApiRoot.AbsoluteUri);
        Assert.Equal(
            "http://127.0.0.1:54321/dpalfery/kyber-weave/releases/download/",
            origin.DownloadRoot.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://169.254.169.254")]
    [InlineData("https://api.github.com.evil.example.com")]
    [InlineData("http://10.0.0.1:8080")]
    public void NonLoopbackOverrideIsRejected(string value)
    {
        SelfUpdateException error = Assert.Throws<SelfUpdateException>(
            () => ReleaseOrigin.Resolve(Env(value)));

        Assert.Contains("127.0.0.1", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/repos/dpalfery")]
    public void MalformedOverrideIsRejected(string value)
    {
        Assert.Throws<SelfUpdateException>(() => ReleaseOrigin.Resolve(Env(value)));
    }

    [Fact]
    public void NonHttpSchemeOverrideIsRejected()
    {
        Assert.Throws<SelfUpdateException>(
            () => ReleaseOrigin.Resolve(Env("file://localhost/etc/passwd")));
    }

    [Fact]
    public void DefaultOriginRefusesPlainHttpEverywhere()
    {
        Assert.Throws<SelfUpdateException>(
            () => ReleaseOrigin.Default.EnsureAllowed(new Uri("http://127.0.0.1:8080/x")));
        Assert.Throws<SelfUpdateException>(
            () => ReleaseOrigin.Default.EnsureAllowed(new Uri("http://github.com/x")));

        ReleaseOrigin.Default.EnsureAllowed(new Uri("https://github.com/x"));
    }

    [Fact]
    public void ActiveOverrideStillRefusesPlainHttpOffLoopback()
    {
        ReleaseOrigin origin = ReleaseOrigin.Resolve(Env("http://127.0.0.1:8080"));

        // The redirect case: a local server answering with a plain-HTTP Location pointing
        // somewhere else must not be followed just because the override is on.
        Assert.Throws<SelfUpdateException>(
            () => origin.EnsureAllowed(new Uri("http://evil.example.com/asset.tar.gz")));

        origin.EnsureAllowed(new Uri("http://127.0.0.1:8080/asset.tar.gz"));
        origin.EnsureAllowed(new Uri("https://github.com/asset.tar.gz"));
    }

    [Fact]
    public void AssetUriUsesTheResolvedOrigin()
    {
        ReleaseOrigin origin = ReleaseOrigin.Resolve(Env("http://127.0.0.1:8080"));

        Assert.Equal(
            "http://127.0.0.1:8080/dpalfery/kyber-weave/releases/download/v1.2.3/SHA256SUMS.txt",
            GitHubReleaseClient.AssetUri(origin, "v1.2.3", "SHA256SUMS.txt").AbsoluteUri);

        Assert.StartsWith(
            "https://github.com/",
            GitHubReleaseClient.AssetUri("v1.2.3", "SHA256SUMS.txt").AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AssetNamesWithSeparatorsAreStillRefusedUnderAnOverride()
    {
        ReleaseOrigin origin = ReleaseOrigin.Resolve(Env("http://127.0.0.1:8080"));

        Assert.Throws<SelfUpdateException>(
            () => GitHubReleaseClient.AssetUri(origin, "v1.2.3", "../../etc/passwd"));
    }
}
