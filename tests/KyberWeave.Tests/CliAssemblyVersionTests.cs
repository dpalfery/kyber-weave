using KyberWeave.Cli;
using KyberWeave.Cli.Commands.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Doctor compares this version to <c>kyber-weave-arbiter --version</c>. The probe never
/// emits <c>+build</c> metadata or a leading <c>v</c>, so those are dropped here and nowhere else.
/// </summary>
public sealed class CliAssemblyVersionTests
{
    [Theory]
    [InlineData("1.2.3+build.5", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("v1.2.3+sha", "1.2.3")]
    [InlineData("  v1.2.3+meta  ", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    public void NormalizeDropsBuildMetadataAndALeadingV(string raw, string expected)
    {
        Assert.Equal(expected, CliAssemblyVersion.Normalize(raw));
    }

    /// <summary>
    /// The stub doctor accepts is this comparison value, not a second copy of the trim.
    /// </summary>
    [Fact]
    public void ForProbeIsTheInformationalVersionWithBuildMetadataAndALeadingVRemoved()
    {
        string informational = CliAssemblyVersion.Informational(typeof(SquadDoctorCommand).Assembly);
        string forProbe = CliAssemblyVersion.ForProbe(typeof(SquadDoctorCommand).Assembly);

        Assert.Equal(CliAssemblyVersion.Normalize(informational), forProbe);
        Assert.DoesNotContain("+", forProbe, StringComparison.Ordinal);
        Assert.False(forProbe.StartsWith('v'));
    }
}
