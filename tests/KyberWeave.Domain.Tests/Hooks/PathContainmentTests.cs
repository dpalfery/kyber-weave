using KyberWeave.Domain.Hooks;
using Xunit;

namespace KyberWeave.Domain.Tests.Hooks;

public sealed class PathContainmentTests
{
    [Theory]
    [InlineData("/repo", "/repo", true)]
    [InlineData("/repo", "/repo/src/a.cs", true)]
    [InlineData("/repo/", "/repo/src", true)]
    [InlineData("/repo", "/repo-other/src", false)]
    [InlineData("/repo", "/elsewhere", false)]
    public void ComparesWholePathSegments(string directory, string path, bool expected)
    {
        string dir = Path.GetFullPath(directory);
        string target = Path.GetFullPath(path);

        Assert.Equal(expected, PathContainment.IsWithin(dir, target));
    }
}
