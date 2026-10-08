using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Release;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the <see cref="GitHubSquadReleaseSource"/> token contract: GITHUB_TOKEN/GH_TOKEN is
/// sent as a Bearer credential to the API host only, never to asset redirect hosts, and a
/// 403 without a token names the remedy without ever disclosing the token value.
/// </summary>
public sealed class GitHubSquadReleaseSourceAuthTests
{
    private const string Version = "1.2.3";
    private const string AssetName = "kyber-squad-1.2.3.zip";
    private const string Repository = "dpalfery/kyber-weave";
    private const string Token = "test-github-token-value";
    private static readonly Uri ApiRoot = new("https://api.github.test/");

    [Fact]
    public async Task DownloadAndExtractAsyncWithGitHubTokenSendsAuthorizationOnApiHostOnly()
    {
        byte[] archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        string checksum = Sha256(archiveBytes);
        using RecordingHandler handler = ReleaseHandler(archiveBytes, $"{checksum}  {AssetName}\n");
        using TempDirectory temp = new();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            ApiRoot,
            readEnvironment: name => name == "GITHUB_TOKEN" ? Token : null);

        SquadReleaseResult result = await source.DownloadAndExtractAsync(
            new SquadReleaseRequest(Repository, Version, Path.Combine(temp.Path, "squad")),
            CancellationToken.None);

        Assert.Equal(checksum, result.Checksum.Sha256);
        Assert.NotEmpty(handler.Requests);
        foreach ((Uri uri, string? authorization) in handler.Requests)
        {
            if (string.Equals(uri.Host, ApiRoot.Host, StringComparison.OrdinalIgnoreCase))
                Assert.Equal($"Bearer {Token}", authorization);
            else
                Assert.Null(authorization);
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsyncWithGhTokenFallbackSendsAuthorizationPreferringGitHubToken()
    {
        byte[] archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        string checksum = Sha256(archiveBytes);
        using RecordingHandler handler = ReleaseHandler(archiveBytes, $"{checksum}  {AssetName}\n");
        using TempDirectory temp = new();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            ApiRoot,
            readEnvironment: name => name switch
            {
                "GITHUB_TOKEN" => Token,
                "GH_TOKEN" => "gh-fallback-token",
                _ => null
            });

        await source.DownloadAndExtractAsync(
            new SquadReleaseRequest(Repository, Version, Path.Combine(temp.Path, "squad")),
            CancellationToken.None);

        (Uri _, string? authorization) = Assert.Single(
            handler.Requests,
            request => string.Equals(request.Uri.Host, ApiRoot.Host, StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"Bearer {Token}", authorization);
    }

    [Fact]
    public async Task DownloadAndExtractAsyncWithGhTokenOnlySendsAuthorization()
    {
        byte[] archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        string checksum = Sha256(archiveBytes);
        using RecordingHandler handler = ReleaseHandler(archiveBytes, $"{checksum}  {AssetName}\n");
        using TempDirectory temp = new();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            ApiRoot,
            readEnvironment: name => name == "GH_TOKEN" ? "gh-only-token" : null);

        await source.DownloadAndExtractAsync(
            new SquadReleaseRequest(Repository, Version, Path.Combine(temp.Path, "squad")),
            CancellationToken.None);

        (Uri _, string? authorization) = Assert.Single(
            handler.Requests,
            request => string.Equals(request.Uri.Host, ApiRoot.Host, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Bearer gh-only-token", authorization);
    }

    [Fact]
    public async Task DownloadAndExtractAsyncWithEmptyGitHubTokenFallsThroughToGhToken()
    {
        const string GhToken = "gh-fallback-real-token";
        byte[] archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        string checksum = Sha256(archiveBytes);
        using RecordingHandler handler = ReleaseHandler(archiveBytes, $"{checksum}  {AssetName}\n");
        using TempDirectory temp = new();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            ApiRoot,
            readEnvironment: name => name switch
            {
                "GITHUB_TOKEN" => string.Empty,
                "GH_TOKEN" => GhToken,
                _ => null
            });

        await source.DownloadAndExtractAsync(
            new SquadReleaseRequest(Repository, Version, Path.Combine(temp.Path, "squad")),
            CancellationToken.None);

        (Uri _, string? authorization) = Assert.Single(
            handler.Requests,
            request => string.Equals(request.Uri.Host, ApiRoot.Host, StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"Bearer {GhToken}", authorization);
    }

    [Fact]
    public async Task DownloadAndExtractAsyncWithoutTokenSendsNoAuthorization()
    {
        byte[] archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        string checksum = Sha256(archiveBytes);
        using RecordingHandler handler = ReleaseHandler(archiveBytes, $"{checksum}  {AssetName}\n");
        using TempDirectory temp = new();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            ApiRoot,
            readEnvironment: _ => null);

        await source.DownloadAndExtractAsync(
            new SquadReleaseRequest(Repository, Version, Path.Combine(temp.Path, "squad")),
            CancellationToken.None);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request => Assert.Null(request.Authorization));
    }

    [Fact]
    public async Task DownloadAndExtractAsyncStripsAuthorizationOnRedirectOffApiHost()
    {
        byte[] archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        string checksum = Sha256(archiveBytes);
        Uri redirectTarget = new("https://objects.github.test/squad.zip");
        using RecordingHandler handler = ReleaseHandler(
            archiveBytes,
            $"{checksum}  {AssetName}\n",
            redirectArchiveTo: redirectTarget);
        using TempDirectory temp = new();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            ApiRoot,
            readEnvironment: name => name == "GITHUB_TOKEN" ? Token : null);

        SquadReleaseResult result = await source.DownloadAndExtractAsync(
            new SquadReleaseRequest(Repository, Version, Path.Combine(temp.Path, "squad")),
            CancellationToken.None);

        Assert.Equal(checksum, result.Checksum.Sha256);
        (Uri _, string? apiAuthorization) = Assert.Single(
            handler.Requests,
            request => string.Equals(request.Uri.Host, ApiRoot.Host, StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"Bearer {Token}", apiAuthorization);
        (Uri _, string? redirectAuthorization) = Assert.Single(
            handler.Requests,
            request => request.Uri == redirectTarget);
        Assert.Null(redirectAuthorization);
    }

    [Fact]
    public async Task DownloadAndExtractAsyncStripsAuthorizationOnSameHostDifferentPortRedirect()
    {
        byte[] archiveBytes = CreateArchive(("payload/manifest.json", "canonical"));
        string checksum = Sha256(archiveBytes);
        Uri redirectTarget = new UriBuilder(ApiRoot.Scheme, ApiRoot.Host, 8443, "/squad.zip").Uri;
        using RecordingHandler handler = ReleaseHandler(
            archiveBytes,
            $"{checksum}  {AssetName}\n",
            redirectArchiveTo: redirectTarget);
        using TempDirectory temp = new();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            ApiRoot,
            readEnvironment: name => name == "GITHUB_TOKEN" ? Token : null);

        SquadReleaseResult result = await source.DownloadAndExtractAsync(
            new SquadReleaseRequest(Repository, Version, Path.Combine(temp.Path, "squad")),
            CancellationToken.None);

        Assert.Equal(checksum, result.Checksum.Sha256);
        (Uri _, string? apiAuthorization) = Assert.Single(
            handler.Requests,
            request => request.Uri.Host.Equals(ApiRoot.Host, StringComparison.OrdinalIgnoreCase) &&
                request.Uri.Port == ApiRoot.Port &&
                !request.Uri.Equals(redirectTarget));
        Assert.Equal($"Bearer {Token}", apiAuthorization);
        (Uri _, string? redirectAuthorization) = Assert.Single(
            handler.Requests,
            request => request.Uri == redirectTarget);
        Assert.Null(redirectAuthorization);
    }

    [Fact]
    public async Task DownloadAndExtractAsyncWithForbiddenAndNoTokenNamesTokenRemedy()
    {
        Uri releaseUri = new Uri(ApiRoot, $"repos/{Repository}/releases/tags/v{Version}");
        using RecordingHandler handler = new();
        handler.Enqueue(releaseUri, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using TempDirectory temp = new();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            ApiRoot,
            readEnvironment: _ => null);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            source.DownloadAndExtractAsync(
                new SquadReleaseRequest(Repository, Version, Path.Combine(temp.Path, "squad")),
                CancellationToken.None));

        Assert.Contains("GITHUB_TOKEN", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GH_TOKEN", exception.Message, StringComparison.Ordinal);
        Assert.Contains("anonymous rate limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAndExtractAsyncWithForbiddenAndTokenOmitsAnonymousClaimAndLeaksNothing()
    {
        Uri releaseUri = new Uri(ApiRoot, $"repos/{Repository}/releases/tags/v{Version}");
        using RecordingHandler handler = new();
        handler.Enqueue(releaseUri, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using TempDirectory temp = new();
        using ISquadReleaseSource source = new GitHubSquadReleaseSource(
            handler,
            ApiRoot,
            readEnvironment: name => name == "GITHUB_TOKEN" ? Token : null);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            source.DownloadAndExtractAsync(
                new SquadReleaseRequest(Repository, Version, Path.Combine(temp.Path, "squad")),
                CancellationToken.None));

        Assert.DoesNotContain("anonymous", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, exception.ToString(), StringComparison.Ordinal);
    }

    private static RecordingHandler ReleaseHandler(
        byte[] archiveBytes,
        string checksumManifest,
        Uri? redirectArchiveTo = null)
    {
        Uri releaseUri = new Uri(ApiRoot, $"repos/{Repository}/releases/tags/v{Version}");
        Uri checksumUri = new Uri("https://downloads.github.test/SHA256SUMS.txt");
        Uri archiveUri = new Uri("https://downloads.github.test/" + AssetName);
        RecordingHandler handler = new();
        handler.Enqueue(
            releaseUri,
            _ => JsonResponse($$"""
                {
                  "tag_name": "v{{Version}}",
                  "assets": [
                    {
                      "name": "SHA256SUMS.txt",
                      "browser_download_url": "{{checksumUri}}",
                      "size": 4096
                    },
                    {
                      "name": "{{AssetName}}",
                      "browser_download_url": "{{archiveUri}}",
                      "size": 8192
                    }
                  ]
                }
                """));
        handler.Enqueue(
            checksumUri,
            _ => BytesResponse(Encoding.UTF8.GetBytes(checksumManifest), "text/plain"));

        if (redirectArchiveTo is null)
        {
            handler.Enqueue(
                archiveUri,
                _ => BytesResponse(archiveBytes, "application/zip"));
        }
        else
        {
            handler.Enqueue(archiveUri, _ => RedirectResponse(redirectArchiveTo));
            handler.Enqueue(
                redirectArchiveTo,
                _ => BytesResponse(archiveBytes, "application/zip"));
        }

        return handler;
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        BytesResponse(Encoding.UTF8.GetBytes(json), "application/json");

    private static HttpResponseMessage BytesResponse(byte[] bytes, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) }
        }
    };

    private static HttpResponseMessage RedirectResponse(Uri location) => new(HttpStatusCode.Redirect)
    {
        Headers = { Location = location }
    };

    private static byte[] CreateArchive(params (string Name, string Content)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string? name, string? content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using StreamWriter writer = new(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, Queue<Func<HttpRequestMessage, HttpResponseMessage>>> _responses = [];

        public List<(Uri Uri, string? Authorization)> Requests { get; } = [];

        public void Enqueue(Uri uri, Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            if (!_responses.TryGetValue(uri, out Queue<Func<HttpRequestMessage, HttpResponseMessage>>? queue))
            {
                queue = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
                _responses.Add(uri, queue);
            }

            queue.Enqueue(responseFactory);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.RequestUri);
            Requests.Add((request.RequestUri, request.Headers.Authorization?.ToString()));
            if (!_responses.TryGetValue(request.RequestUri, out Queue<Func<HttpRequestMessage, HttpResponseMessage>>? queue) ||
                queue.Count == 0)
            {
                throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}");
            }

            HttpResponseMessage response = queue.Dequeue()(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
