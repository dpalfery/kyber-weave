using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.RunStart;

/// <summary>The path rules of the run record, over a fake platform and a fake path resolver, with no disk.</summary>
public sealed class RunBaseStateFormatTests
{
    [Theory]
    [InlineData(OperatingSystemKind.Linux, StringComparison.Ordinal)]
    [InlineData(OperatingSystemKind.MacOS, StringComparison.OrdinalIgnoreCase)]
    [InlineData(OperatingSystemKind.Windows, StringComparison.OrdinalIgnoreCase)]
    public void PathComparison_FollowsThePlatform(OperatingSystemKind kind, StringComparison expected)
    {
        FakeProcessEnvironment environment = new(operatingSystem: kind);

        Assert.Equal(expected, RunBaseStateFormat.PathComparison(environment));
    }

    [Fact]
    public void NormalizeDirectory_DropsTheTrailingSeparatorAndKeepsAnAbsolutePath()
    {
        FakePathResolver paths = FakePathResolver.OverEmptyFileSystem();

        string normalized = RunBaseStateFormat.NormalizeDirectory("/work/repo/", paths, new FakeProcessEnvironment());

        Assert.Equal("/work/repo", normalized);
    }

    [Fact]
    public void NormalizeDirectory_TakesARelativePathAgainstTheEnvironmentsCurrentDirectory()
    {
        FakePathResolver paths = FakePathResolver.OverEmptyFileSystem();
        FakeProcessEnvironment environment = new(currentDirectory: "/work/here");

        string normalized = RunBaseStateFormat.NormalizeDirectory("../repo-run", paths, environment);

        Assert.Equal("/work/repo-run", normalized);
    }
}
