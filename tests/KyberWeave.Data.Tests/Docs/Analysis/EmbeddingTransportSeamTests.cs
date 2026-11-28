using System.Net;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The embedding client over the network seam: a fake transport and clock, so no socket opens and no time passes.</summary>
public sealed class EmbeddingTransportSeamTests
{
    private const string OneVector = """{ "data": [{ "index": 0, "embedding": [1, 0] }] }""";

    [Fact]
    public async Task TheClientCallsTheEndpointThroughTheTransportAndReadsTheTokenFromTheEnvironment()
    {
        using FakeHttpMessageHandler handler = new(FakeHttpMessageHandler.Response(HttpStatusCode.OK, OneVector));
        FakeEmbeddingTransport transport = new(handler, IPAddress.Loopback);
        FakeProcessEnvironment environment = new(readVariable: name => name == "LOCAL_TOKEN" ? "token-1" : null);
        using OpenAiCompatibleEmbeddingGenerator generator = new(transport, environment, new FakeTimeProvider());

        EmbeddingGenerationResult result = await generator.GenerateAsync([Key()], ["input"], Config());

        Assert.Single(result.Embeddings);
        RecordedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal("token-1", request.Authorization?.Parameter);
        Assert.Equal(["127.0.0.1"], transport.ResolvedHosts);
    }

    [Fact]
    public async Task AHostThatResolvesOffLoopbackIsRefusedBeforeAnyRequestLeaves()
    {
        using FakeHttpMessageHandler handler = new(FakeHttpMessageHandler.Response(HttpStatusCode.OK, OneVector));
        using OpenAiCompatibleEmbeddingGenerator generator = new(
            new FakeEmbeddingTransport(handler, IPAddress.Parse("203.0.113.9")),
            new FakeProcessEnvironment(),
            new FakeTimeProvider());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync([Key()], ["input"], Config()));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AServerErrorFailsTheCallAndNamesTheStatus()
    {
        using FakeHttpMessageHandler handler = new(FakeHttpMessageHandler.Response(HttpStatusCode.InternalServerError, "boom"));
        using OpenAiCompatibleEmbeddingGenerator generator = Generator(handler, new FakeTimeProvider());

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            generator.GenerateAsync([Key()], ["input"], Config()));

        Assert.Contains("500", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableServerSurfacesTheTransportFailure()
    {
        using FakeHttpMessageHandler handler = FakeHttpMessageHandler.Failing(new HttpRequestException("connection refused"));
        using OpenAiCompatibleEmbeddingGenerator generator = Generator(handler, new FakeTimeProvider());

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            generator.GenerateAsync([Key()], ["input"], Config()));

        Assert.Equal("connection refused", failure.Message);
    }

    [Fact]
    public async Task ACallThatOutlastsTheConfiguredTimeoutIsCancelledWhenTheClockPassesIt()
    {
        FakeTimeProvider clock = new();
        using FakeHttpMessageHandler handler = FakeHttpMessageHandler.Hanging();
        using OpenAiCompatibleEmbeddingGenerator generator = Generator(handler, clock);

        Task<EmbeddingGenerationResult> call = generator.GenerateAsync([Key()], ["input"], Config(timeoutSeconds: 5));
        await WaitUntil(() => handler.Requests.Count == 1);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(call.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    private static OpenAiCompatibleEmbeddingGenerator Generator(FakeHttpMessageHandler handler, TimeProvider clock) =>
        new(handler, _ => [IPAddress.Loopback], _ => null, clock);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition(), "The call never reached the transport.");
    }

    private static EmbeddingCacheKey Key() => new("hash", "provider", "model", 2, "float");

    private static DocsAnalysisEmbeddingConfig Config(int timeoutSeconds = 5) => new()
    {
        Mode = DocsAnalysisEmbeddingMode.Required,
        Endpoint = new Uri("http://127.0.0.1:1234/v1/embeddings"),
        Model = "model",
        Dimensions = 2,
        BatchSize = 8,
        TimeoutSeconds = timeoutSeconds,
        ApiKeyEnv = "LOCAL_TOKEN"
    };

    private sealed class FakeEmbeddingTransport(HttpMessageHandler handler, IPAddress resolved) : IEmbeddingTransport
    {
        public List<string> ResolvedHosts { get; } = [];

        public HttpMessageHandler CreateHandler() => handler;

        public IReadOnlyList<IPAddress> ResolveHost(string host)
        {
            ResolvedHosts.Add(host);
            return [resolved];
        }
    }
}
