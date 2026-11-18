using KyberWeave.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Every composition root registers the clock through the application root: the CLI, the MCP server, the Arbiter
/// and the hooks all start from <c>AddWeaveApplication</c>. These tests pin that registration and the
/// <c>TryAdd</c> rule that lets a test replace it.
/// </summary>
public sealed class TimeProviderRegistrationTests
{
    [Fact]
    public void TheApplicationRootRegistersTheSystemClock()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void ARegisteredFakeClockWins()
    {
        FakeTimeProvider clock = new();

        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddWeaveApplication()
            .BuildServiceProvider();

        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
    }
}
