using KyberWeave.Testing.Update;
using Xunit;
using KyberWeave.Domain.Update;

namespace KyberWeave.Application.Tests.Update;

/// <summary>The stand-in for the installed binary an update hands off to: it answers the rehearsal, runs finish mode in process, and refuses anything else.</summary>
public sealed class LoopbackFinishingProcessTests : IDisposable
{
    private readonly UpdateWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public void TheRehearsalIsAnsweredWithZeroAndRunsNothing()
    {
        using ReleaseMapHandler handler = new();
        SelfUpdateHost host = _world.CreateHost("0.1.0", "osx-arm64");
        LoopbackFinishingProcess loopback = _world.Loopback(handler, host, _ => { });

        int exit = loopback.Invoke(host.ProcessPath, ["--version"], true);

        Assert.Equal(0, exit);
        Assert.Empty(handler.Uris);
        Assert.Null(loopback.LastChildOutcome);
    }

    [Fact]
    public void AnythingBesidesTheRehearsalAndFinishModeIsRefused()
    {
        using ReleaseMapHandler handler = new();
        SelfUpdateHost host = _world.CreateHost("0.1.0", "osx-arm64");
        LoopbackFinishingProcess loopback = _world.Loopback(handler, host, _ => { });

        Assert.Throws<InvalidOperationException>(() => loopback.Invoke(host.ProcessPath, ["status"], false));
    }

    [Fact]
    public void FinishModeRunsTheChildInProcessAtTheRequestedVersionAndKeepsItsOutcome()
    {
        using ReleaseMapHandler handler = new();
        SelfUpdateHost host = _world.CreateHost("0.1.0", "osx-arm64");
        LoopbackFinishingProcess loopback = _world.Loopback(handler, host, _ => { });

        int exit = loopback.Invoke(host.ProcessPath, ["update", "0.2.0", "--finish-update"], false);

        Assert.Equal(0, exit);
        Assert.Equal(0, loopback.LastChildOutcome?.ExitCode);
        Assert.Empty(handler.Uris);
    }
}
