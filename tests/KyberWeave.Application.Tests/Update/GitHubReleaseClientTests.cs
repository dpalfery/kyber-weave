using System.Net;
using System.Text;
using KyberWeave.Cli.Update;
using Xunit;

namespace KyberWeave.Application.Tests.Update;

/// <summary>The release client over a stub HTTP handler: no network, no disk.</summary>
public sealed class GitHubReleaseClientTests
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The client owns the handler.")]
    private static GitHubReleaseClient Client(Func<Uri, HttpResponseMessage> respond, Func<string, string?>? environment = null) =>
        new(new StubHandler(respond), "1.2.3", environment ?? (_ => null));

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

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!));
    }
}
