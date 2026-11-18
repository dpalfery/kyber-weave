using KyberWeave.Application;
using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Testing.Update;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The application root resolves the self-update system over the seams it is built from.</summary>
public sealed class SelfUpdateSeamRegistrationTests
{
    [Fact]
    public void TheApplicationRootResolvesTheSelfUpdateSystemAndTheUpdateService()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        Assert.IsType<SelfUpdateSystem>(provider.GetRequiredService<ISelfUpdateSystem>());
        Assert.IsType<ProcessConsoleLauncher>(provider.GetRequiredService<IConsoleProcessLauncher>());
    }

    [Fact]
    public void ARegisteredFileModesWinOverTheOperatingSystemOnes()
    {
        FakeUpdateFileModes modes = new();

        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IUpdateFileModes>(modes)
            .AddWeaveApplication()
            .BuildServiceProvider();

        Assert.Same(modes, provider.GetRequiredService<IUpdateFileModes>());
        Assert.NotNull(provider.GetRequiredService<ISelfUpdateSystem>());
    }
}
