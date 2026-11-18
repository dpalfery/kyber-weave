using System.Net;
using System.Text;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A release asset is buffered in memory, so a download over the limit is refused. Failure this guards: a
/// hostile or broken release host streams an unbounded asset and the CLI runs out of memory.
/// </summary>
public sealed class GitHubSquadReleaseSourceSizeLimitTests
{
    [Fact]
    public async Task AStreamOverTheLimitIsRefusedWithoutBufferingTheRest()
    {
        using MemoryStream source = new(new byte[1000]);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => GitHubSquadReleaseSource.ReadCappedAsync(source, 999, CancellationToken.None));

        Assert.Contains("999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStreamAtTheLimitIsRead()
    {
        byte[] payload = Encoding.ASCII.GetBytes("abcdef");
        using MemoryStream source = new(payload);

        byte[] read = await GitHubSquadReleaseSource.ReadCappedAsync(source, payload.Length, CancellationToken.None);

        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task ADeclaredLengthOverTheLimitIsRefusedBeforeAnyByteIsRead()
    {
        Uri apiRoot = new("https://api.github.test/");
        Uri assetUri = new("https://downloads.github.test/SHA256SUMS.txt");
        Uri releaseUri = new(apiRoot, "repos/dpalfery/kyber-weave/releases/tags/v1.2.3");
        using DeclaredLengthHandler handler = new(releaseUri, assetUri);
        using ISquadReleaseSource source = RealSquadReleaseSource.Create(handler, apiRoot, maximumAssetBytes: 64);
        string root = Path.Combine(Path.GetTempPath(), "kw-size-" + Guid.NewGuid().ToString("N"));

        try
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => source.DownloadAndExtractAsync(
                    new SquadReleaseRequest("dpalfery/kyber-weave", "1.2.3", Path.Combine(root, "squad")),
                    CancellationToken.None));

            Assert.Contains("64-byte limit", error.Message, StringComparison.Ordinal);
            Assert.False(handler.BodyWasRead);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class DeclaredLengthHandler(Uri releaseUri, Uri assetUri) : HttpMessageHandler
    {
        public bool BodyWasRead { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri == releaseUri)
            {
                string json = $$"""
                    {"tag_name":"v1.2.3","assets":[
                      {"name":"SHA256SUMS.txt","browser_download_url":"{{assetUri}}","size":4096},
                      {"name":"kyber-squad-1.2.3.zip","browser_download_url":"{{assetUri}}","size":8192}]}
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    RequestMessage = request,
                });
            }

            ReadTrackingContent content = new(this);
            content.Headers.ContentLength = 10_000;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }

        private sealed class ReadTrackingContent(DeclaredLengthHandler owner) : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                owner.BodyWasRead = true;
                return Task.CompletedTask;
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 10_000;
                return true;
            }
        }
    }
}
