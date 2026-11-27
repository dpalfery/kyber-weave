using System.Runtime.InteropServices;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Application.Update;
using KyberWeave.Domain.Update;

namespace KyberWeave.Application.Tests.Update;

/// <summary>The release platform name taken from the environment seam, so no test depends on the machine it runs on.</summary>
public sealed class PlatformRidTests
{
    [Theory]
    [InlineData(OperatingSystemKind.Linux, Architecture.X64, "linux-x64")]
    [InlineData(OperatingSystemKind.Linux, Architecture.Arm64, "linux-arm64")]
    [InlineData(OperatingSystemKind.MacOS, Architecture.X64, "osx-x64")]
    [InlineData(OperatingSystemKind.MacOS, Architecture.Arm64, "osx-arm64")]
    [InlineData(OperatingSystemKind.Windows, Architecture.X64, "win-x64")]
    public void AnOperatingSystemAndArchitectureNameThePublishedRelease(
        OperatingSystemKind operatingSystem,
        Architecture architecture,
        string expected)
    {
        Assert.Equal(expected, PlatformRid.Detect(operatingSystem, architecture));
    }

    [Fact]
    public void AnOperatingSystemTheEnvironmentCannotNameHasNoRelease()
    {
        SelfUpdateException failure = Assert.Throws<SelfUpdateException>(
            () => PlatformRid.Detect(new FakeProcessEnvironment(operatingSystem: OperatingSystemKind.Unknown)));

        Assert.Contains("unsupported OS", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsOnArm64HasNoPublishedRelease()
    {
        Assert.Throws<SelfUpdateException>(() => PlatformRid.Detect(OperatingSystemKind.Windows, Architecture.Arm64));
    }

    [Fact]
    public void TheEnvironmentsOperatingSystemDecidesThePlatformPrefix()
    {
        string rid = PlatformRid.Detect(new FakeProcessEnvironment(operatingSystem: OperatingSystemKind.Linux));

        Assert.StartsWith("linux-", rid, StringComparison.Ordinal);
    }
}
