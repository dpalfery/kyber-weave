using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Data.Arbiter;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The provider factories live in Application now, over the environment, transport and clock seams, so the application
/// composition root registers them: these fail if a registration or a seam is dropped, which a factory left
/// unregistered would surface as only at run time.
/// </summary>
public sealed class ArbiterProviderCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesBothFactories()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        _ = Assert.IsType<SystemOneProviderFactory>(provider.GetRequiredService<IArbiterProviderFactory>());
        _ = provider.GetRequiredService<ConfiguredArbiterProviderFactory>();
    }

    [Fact]
    public void TheProductFactoryBuildsItsClientOverTheTransportTheSeamSupplies()
    {
        int created = 0;
        ServiceCollection services = new();
        services.AddSingleton<IArbiterHttpHandlers>(new CountingHandlers(() => created++));
        _ = services.AddWeaveApplication();
        using ServiceProvider provider = services.BuildServiceProvider();

        IArbiterProvider client = provider.GetRequiredService<IArbiterProviderFactory>()
            .CreateSystemOne(new Uri("https://api.typesafe.ai/v1"), "jev-1.13.0", 5000, null);
        (client as IDisposable)?.Dispose();

        Assert.Equal(1, created);
    }

    [Fact]
    public void TheConfiguredFactoryReadsTheKeyThroughTheEnvironmentSeam()
    {
        ServiceCollection services = new();
        services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(readVariable: name => name == "TYPESAFE_API_KEY" ? "env-key" : null));
        _ = services.AddWeaveApplication();
        using ServiceProvider provider = services.BuildServiceProvider();

        IArbiterProvider? client = provider.GetRequiredService<ConfiguredArbiterProviderFactory>().TryCreate(
            new ArbiterProviderConfig(ArbiterProviderKind.Systemone, "https://api.typesafe.ai/v1", "jev-1.13.0", 5000),
            "/kw/home");

        Assert.NotNull(client);
        (client as IDisposable)?.Dispose();
    }

    private sealed class CountingHandlers(Action created) : IArbiterHttpHandlers
    {
        public HttpMessageHandler Create()
        {
            created();
            return new HttpClientHandler();
        }
    }
}
