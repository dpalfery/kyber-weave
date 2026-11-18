using KyberWeave.Data.Host;
using Xunit;

namespace KyberWeave.Tests.Host;

/// <summary>
/// A host can mount a volume whose name rule differs from its platform's default: a case-sensitive APFS volume on
/// macOS, or a case-insensitive image on Linux. The adapter must report the volume it actually uses.
/// </summary>
public sealed class ProcessEnvironmentCaseProbeTests
{
    [Fact]
    public void ItReportsTheVolumesRuleRatherThanThePlatforms()
    {
        ProcessEnvironment environment = new(_ => false);

        Assert.False(environment.IsFileSystemCaseInsensitive);
    }

    [Fact]
    public void AProbeThatFindsTheNameUnderAnotherCaseReportsInsensitivity()
    {
        ProcessEnvironment environment = new(_ => true);

        Assert.True(environment.IsFileSystemCaseInsensitive);
    }
}
