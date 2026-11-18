using System.Net;
using KyberWeave.Application;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The application root resolves the CodeGraph, analysis cache and embedding seams every host (CLI, MCP, hooks) starts from.</summary>
public sealed class AnalysisSeamRegistrationTests
{
    [Fact]
    public void TheApplicationRootResolvesTheCodeGraphCacheAndEmbeddingSeams()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ICodeGraphPortsFactory>());
        Assert.NotNull(provider.GetRequiredService<IAnalysisPersistenceFactory>());
        Assert.NotNull(provider.GetRequiredService<IEmbeddingTransport>());
        using IDisposable? generator = provider.GetRequiredService<IEmbeddingGeneratorFactory>().Create() as IDisposable;
        Assert.NotNull(generator);
    }

    [Fact]
    public void ARegisteredTransportWinsOverTheLoopbackOne()
    {
        FakeTransport transport = new();

        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IEmbeddingTransport>(transport)
            .AddWeaveApplication()
            .BuildServiceProvider();
        using IDisposable? generator = provider.GetRequiredService<IEmbeddingGeneratorFactory>().Create() as IDisposable;

        Assert.Equal(1, transport.HandlersCreated);
    }

    private sealed class FakeTransport : IEmbeddingTransport
    {
        public int HandlersCreated { get; private set; }

        public HttpMessageHandler CreateHandler()
        {
            HandlersCreated++;
            return new HttpClientHandler();
        }

        public IReadOnlyList<IPAddress> ResolveHost(string host) => [IPAddress.Loopback];
    }
}
