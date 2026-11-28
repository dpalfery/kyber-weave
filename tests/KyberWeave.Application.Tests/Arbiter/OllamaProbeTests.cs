using System.Net;
using System.Text;
using KyberWeave.Core.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The Ollama probe over a stubbed transport: a 0.35 or later daemon is detected with its version, and anything else —
/// no answer, a bad status, an unparseable body or an old version — means not found, never an error.
/// </summary>
public sealed class OllamaProbeTests
{
    private static readonly Uri VersionUri = new("http://127.0.0.1:11434/api/version");

    [Fact]
    public async Task ASupportedVersion_IsDetectedWithItsVersion()
    {
        OllamaProbe probe = new(Handlers("{\"version\":\"0.35.2\"}"));

        OllamaProbeResult result = await probe.TryDetectAsync(VersionUri, CancellationToken.None);

        Assert.True(result.Detected);
        Assert.Equal("0.35.2", result.Version);
    }

    [Fact]
    public async Task AOnePointZeroMajor_IsDetected()
    {
        OllamaProbe probe = new(Handlers("{\"version\":\"1.0.0\"}"));

        OllamaProbeResult result = await probe.TryDetectAsync(VersionUri, CancellationToken.None);

        Assert.True(result.Detected);
        Assert.Equal("1.0.0", result.Version);
    }

    [Fact]
    public async Task AnOldVersion_IsNotDetected()
    {
        OllamaProbe probe = new(Handlers("{\"version\":\"0.1.26\"}"));

        OllamaProbeResult result = await probe.TryDetectAsync(VersionUri, CancellationToken.None);

        Assert.False(result.Detected);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task ANonSuccessStatus_IsNotDetected()
    {
        OllamaProbe probe = new(Handlers("{\"version\":\"0.35.2\"}", HttpStatusCode.InternalServerError));

        OllamaProbeResult result = await probe.TryDetectAsync(VersionUri, CancellationToken.None);

        Assert.False(result.Detected);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task ABodyWithoutAStringVersion_IsNotDetected()
    {
        OllamaProbe probe = new(Handlers("{\"other\":1}"));

        OllamaProbeResult result = await probe.TryDetectAsync(VersionUri, CancellationToken.None);

        Assert.False(result.Detected);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task AnUnparseableBody_IsNotDetected()
    {
        OllamaProbe probe = new(Handlers("not json"));

        OllamaProbeResult result = await probe.TryDetectAsync(VersionUri, CancellationToken.None);

        Assert.False(result.Detected);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task ACancelledProbe_IsNotDetected()
    {
        OllamaProbe probe = new(Handlers("{\"version\":\"0.35.2\"}"));

        OllamaProbeResult result = await probe.TryDetectAsync(VersionUri, CancelledToken());

        Assert.False(result.Detected);
        Assert.Null(result.Version);
    }

    private static CancellationToken CancelledToken()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        return cancelled.Token;
    }

    [Fact]
    public async Task ANonHttpScheme_IsNotProbed()
    {
        CountingHandlers handlers = Handlers(string.Empty);
        OllamaProbe probe = new(handlers);

        OllamaProbeResult result = await probe.TryDetectAsync(new Uri("file:///tmp/version"), CancellationToken.None);

        Assert.False(result.Detected);
        Assert.Null(result.Version);
        Assert.Equal(0, handlers.Created);
    }

    private static CountingHandlers Handlers(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(body, status);

    /// <summary>Answers one fixed body, honors a cancelled token the way the real transport does, and counts transports.</summary>
    private sealed class CountingHandlers : IArbiterHttpHandlers
    {
        internal int Created { get; private set; }

        public CountingHandlers(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            Response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        internal HttpResponseMessage Response { get; }

        public HttpMessageHandler Create()
        {
            Created++;
            return new StubHandler(Response);
        }

        private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromException<HttpResponseMessage>(new TaskCanceledException("cancelled", null, cancellationToken));
                return Task.FromResult(response);
            }
        }
    }
}
