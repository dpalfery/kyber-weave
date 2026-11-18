using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// The shared user-paths fake reports the directories the test configured and never the real profile, so no test
/// needs user-paths double of its own.
/// </summary>
public sealed class FakeSquadUserPathsTests
{
    [Fact]
    public void ADefaultHomeSitsBeneathTheApplicationDataDirectory()
    {
        FakeSquadUserPaths userPaths = new("/tmp/app-data");

        Assert.Equal("/tmp/app-data", userPaths.ApplicationDataDirectory);
        Assert.Equal(Path.Combine("/tmp/app-data", "home"), userPaths.HomeDirectory);
    }

    [Fact]
    public void AnExplicitHomeIsReportedAsGiven()
    {
        FakeSquadUserPaths userPaths = new("/tmp/app-data", "/tmp/elsewhere");

        Assert.Equal("/tmp/app-data", userPaths.ApplicationDataDirectory);
        Assert.Equal("/tmp/elsewhere", userPaths.HomeDirectory);
    }

    [Fact]
    public void FromHomePutsTheApplicationDataDirectoryBesideTheHome()
    {
        FakeSquadUserPaths userPaths = FakeSquadUserPaths.FromHome(Path.Combine(Path.GetTempPath(), "from-home"));

        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "application-data"),
            userPaths.ApplicationDataDirectory);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "from-home"), userPaths.HomeDirectory);
    }
}
