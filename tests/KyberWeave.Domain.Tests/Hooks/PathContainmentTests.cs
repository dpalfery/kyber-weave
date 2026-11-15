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

    [Fact]
    public void ComparesCaseSensitivelyOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the case-sensitivity expectation is for Linux only");
        }

        Assert.False(PathContainment.IsWithin("/repo", "/REPO/a.cs"));
    }

    [Theory]
    [InlineData(StringComparison.Ordinal, false)]
    [InlineData(StringComparison.OrdinalIgnoreCase, true)]
    public void AppliesTheGivenComparison(StringComparison comparison, bool expected)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kw-containment"));
        string recased = Path.Combine(root.ToUpperInvariant(), "a.cs");

        Assert.Equal(expected, PathContainment.IsWithinUsing(root, recased, comparison));
    }

    [Fact]
    public void RejectsDotDotTraversalOutOfTheDirectory()
    {
        string dir = Path.GetFullPath("/repo");
        string escaping = dir + Path.DirectorySeparatorChar + "sub" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "etc" + Path.DirectorySeparatorChar + "passwd";

        Assert.False(PathContainment.IsWithin(dir, escaping));
    }

    [Fact]
    public void AcceptsDotDotThatStaysInside()
    {
        string dir = Path.GetFullPath("/repo");
        string inside = dir + Path.DirectorySeparatorChar + "a" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "b.cs";

        Assert.True(PathContainment.IsWithin(dir, inside));
    }

    [Theory]
    [InlineData("", "/repo/a.cs")]
    [InlineData("  ", "/repo/a.cs")]
    [InlineData("/repo", "")]
    [InlineData("repo", "/repo/a.cs")]
    [InlineData("/repo", "repo/a.cs")]
    public void FailsClosedOnEmptyOrRelativeInput(string directory, string path)
    {
        string dir = directory.StartsWith('/') ? Path.GetFullPath(directory) : directory;
        string target = path.StartsWith('/') ? Path.GetFullPath(path) : path;

        Assert.False(PathContainment.IsWithin(dir, target));
    }
}
