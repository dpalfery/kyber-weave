using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Release;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using KyberWeave.Application.Squad.Release;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>The application composition builds the GitHub release source with every seam it takes.</summary>
public sealed class SquadReleaseSourceCompositionTests
{
    [Fact]
    public void TheReleaseSourceResolvesAsTheGitHubSourceOverEverySeam()
    {
        ServiceCollection services = [];
        _ = services.AddWeaveApplication();
        using ServiceProvider provider = services.BuildServiceProvider();

        ISquadReleaseSource source = provider.GetRequiredService<ISquadReleaseSource>();

        Assert.IsType<GitHubSquadReleaseSource>(source);
    }

    [Fact]
    public void TheReleaseTransportDoesNotFollowRedirects()
    {
        ServiceCollection services = [];
        _ = services.AddWeaveApplication();
        using ServiceProvider provider = services.BuildServiceProvider();

        using HttpMessageHandler handler = provider.GetRequiredService<ISquadReleaseHttpHandlers>().Create();

        SocketsHttpHandler sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect);
    }
}
