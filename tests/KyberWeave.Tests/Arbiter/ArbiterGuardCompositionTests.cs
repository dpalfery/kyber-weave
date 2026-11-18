using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The guards container is built on its own by the hook processes, so it must register every adapter the guards read
/// through. The host-config adapter reads the configuration repository: a missing registration does not fail the
/// build, it fails at resolve time inside the guard's catch-all, which denies every conductor call and allows every
/// worker call.
/// </summary>
public sealed class ArbiterGuardCompositionTests
{
    [Fact]
    public void TheGuardsContainerResolvesTheHostConfigLoaderTheGuardsReadThrough()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.CreateGuards();

        IHostConfigLoader loader = services.GetRequiredService<IHostConfigLoader>();

        Assert.NotNull(loader);
        Assert.NotNull(services.GetRequiredService<IKyberWeaveConfigRepository>());
    }

    [Fact]
    public void TheGuardsContainerResolvesBothGuards()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.CreateGuards();

        Assert.NotNull(services.GetRequiredService<IConductorGuard>());
        Assert.NotNull(services.GetRequiredService<IWorkerGuard>());
    }

    [Fact]
    public void TheGuardsContainerResolvesTheGitFactsThroughTheProcessAndFileSystemSeams()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.CreateGuards();

        IGitRepositoryFacts facts = services.GetRequiredService<IGitRepositoryFacts>();

        Assert.NotNull(facts);
    }

    [Fact]
    public void TheGuardsContainerResolvesTheTreeProbeThroughTheFileSystemSeam()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.CreateGuards();

        IRepositoryTreeProbe probe = services.GetRequiredService<IRepositoryTreeProbe>();

        Assert.IsType<FileRepositoryTreeProbe>(probe);
        Assert.True(probe.ProbeRoot(AppContext.BaseDirectory).Exists);
        Assert.False(probe.ProbeRoot(Path.Combine(AppContext.BaseDirectory, "no-such-root")).Exists);
    }
}
