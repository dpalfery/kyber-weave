using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Host;

/// <summary>
/// The chosen-home constructor is the shape most suite helpers use. It must stay a fake: no value may come from the
/// running host, and the home a test supplies must be read when it is asked for, not when the fake is built.
/// </summary>
public sealed class FakeProcessEnvironmentTests
{
    [Fact]
    public void TheChosenHomeIsReadOnEachAccess()
    {
        string home = "/first";
        FakeProcessEnvironment environment = new(() => home);

        home = "/second";

        Assert.Equal("/second", environment.HomeDirectory);
    }

    [Fact]
    public void TheChosenHomeConstructorClaimsNoPlatformFacts()
    {
        FakeProcessEnvironment environment = new(() => "/home");

        Assert.Equal("kw-user", environment.UserName);
        Assert.Equal("kw-machine", environment.MachineName);
        Assert.Equal(OperatingSystemKind.Unknown, environment.OperatingSystem);
        Assert.True(environment.IsInputRedirected);
    }
}
