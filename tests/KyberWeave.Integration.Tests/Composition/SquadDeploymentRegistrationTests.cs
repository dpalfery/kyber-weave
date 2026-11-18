using KyberWeave.Application;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The deployment planner and the transaction are built by the data and application composition roots, not by a
/// test, so a constructor that gains a dependency the roots do not register fails here instead of at the first
/// real install.
/// </summary>
public sealed class SquadDeploymentRegistrationTests
{
    [Fact]
    public void TheRealContainerResolvesThePlannerAndATransaction()
    {
        ServiceCollection services = [];
        _ = services.AddWeaveApplication();
        _ = services.AddWeaveDataServices();

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<ISquadDeploymentPlanner>());
        Assert.NotNull(provider.GetRequiredService<ISquadTransactionFactory>().Create(observer: null));
    }
}
