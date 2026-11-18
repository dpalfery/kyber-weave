using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Host;
using Xunit;

namespace KyberWeave.Tests.Host;

/// <summary>
/// The platform family does not decide every volume's name rule, but where a fallback is needed each family must
/// answer the way its own default disk does: only Windows and macOS volumes ignore case.
/// </summary>
public sealed class ProcessEnvironmentCaseRuleTests
{
    [Theory]
    [InlineData(OperatingSystemKind.Windows, true)]
    [InlineData(OperatingSystemKind.MacOS, true)]
    [InlineData(OperatingSystemKind.Linux, false)]
    [InlineData(OperatingSystemKind.FreeBSD, false)]
    [InlineData(OperatingSystemKind.Unknown, false)]
    public void OnlyWindowsAndMacOsAreCaseInsensitive(OperatingSystemKind family, bool expected) =>
        Assert.Equal(expected, ProcessEnvironment.IsCaseInsensitivePlatform(family));
}
