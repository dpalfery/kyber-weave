using System.Net;
using System.Text;
using KyberWeave.Cli.Update;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Update;

/// <summary>The release client over a stub HTTP handler: no network, no disk.</summary>
public sealed class GitHubReleaseClientTests
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The client owns the handler.")]
    private static GitHubReleaseClient Client(Func<Uri, HttpResponseMessage> respond, Func<string, string?>? environment = null) =>
        Client(new FakeHttpMessageHandler(request => respond(request.Uri!)), environment);

    private static GitHubReleaseClient Client(FakeHttpMessageHandler handler, Func<string, string?>? environment = null) =>
        new(handler, "1.2.3", environment ?? (_ => null));

    [Fact]
    public void TheLatestStableTagIsReadFromTheLatestEndpoint()
    {
        using GitHubReleaseClient client = Client(uri =>
            uri == GitHubReleaseClient.LatestApi
                ? Json("{\"tag_name\":\"v9.9.9\",\"draft\":false}")
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal("9.9.9", client.ResolveLatestStable());
    }

    [Fact]
    public void AnAssetIsCopiedIntoTheStreamTheCallerSupplies()
    {
        using GitHubReleaseClient client = Client(uri =>
            uri == GitHubReleaseClient.AssetUri("v1.0.0", "kw.tar.gz")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        using MemoryStream destination = new();

        client.DownloadAsset("v1.0.0", "kw.tar.gz", destination);

        Assert.Equal([1, 2, 3], destination.ToArray());
    }

    [Fact]
    public void AMissingAssetNamesTheReleaseAndWritesNothing()
    {
        using GitHubReleaseClient client = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using MemoryStream destination = new();

        SelfUpdateException failure = Assert.Throws<SelfUpdateException>(() => client.DownloadAsset("v1.0.0", "kw.tar.gz", destination));

        Assert.Contains("v1.0.0", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void AnAssetNameThatEscapesTheReleaseIsRefused() =>
        Assert.Throws<SelfUpdateException>(() => GitHubReleaseClient.AssetUri("v1.0.0", "../../etc/passwd"));

    [Fact]
    public void EveryRequestIsAGetThatNamesTheProductAndAsksForTheGitHubMediaType()
    {
        using FakeHttpMessageHandler handler = new(Json("{\"tag_name\":\"v2.0.0\"}"));
        using GitHubReleaseClient client = Client(handler);

        _ = client.ResolveLatestStable();

        RecordedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(GitHubReleaseClient.LatestApi, request.Uri);
        Assert.Equal("kyber-weave/1.2.3", request.UserAgent);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public void ATokenInTheEnvironmentIsSentAsABearerCredential()
    {
        using FakeHttpMessageHandler handler = new(Json("{\"tag_name\":\"v2.0.0\"}"));
        using GitHubReleaseClient client = Client(handler, name => name == "GH_TOKEN" ? " token-1 " : null);

        _ = client.ResolveLatestStable();

        RecordedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Authorization?.Scheme);
        Assert.Equal("token-1", request.Authorization?.Parameter);
    }

    [Fact]
    public void AServerErrorOtherThanNotFoundFailsTheLookupAndNamesTheStatus()
    {
        using GitHubReleaseClient client = Client(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        SelfUpdateException failure = Assert.Throws<SelfUpdateException>(() => client.ResolveLatestStable());

        Assert.Contains("HTTP 502", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreachableServerSurfacesTheTransportFailureAndTheDownloadWritesNothing()
    {
        using FakeHttpMessageHandler handler = FakeHttpMessageHandler.Failing(new HttpRequestException("connection refused"));
        using GitHubReleaseClient client = Client(handler);
        using MemoryStream destination = new();

        _ = Assert.Throws<HttpRequestException>(() => client.DownloadAsset("v1.0.0", "kw.tar.gz", destination));

        Assert.Equal(0, destination.Length);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
