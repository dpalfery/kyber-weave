using KyberWeave.Domain.Hooks;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Domain.Tests.Hooks;

public sealed class PathContainmentTests
{
    private readonly FakePathResolver _paths = FakePathResolver.OverEmptyFileSystem();

    private static string Abs(string path) => Path.GetFullPath(path);

    [Theory]
    [InlineData("/repo", "/repo", true)]
    [InlineData("/repo", "/repo/src/a.cs", true)]
    [InlineData("/repo/", "/repo/src", true)]
    [InlineData("/repo", "/repo-other/src", false)]
    [InlineData("/repo", "/elsewhere", false)]
    public void ComparesWholePathSegments(string directory, string path, bool expected)
    {
        string dir = Abs(directory);
        string target = Abs(path);

        Assert.Equal(expected, PathContainment.IsWithin(_paths, dir, target));
    }

    [Fact]
    public void ComparesCaseSensitivelyOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the case-sensitivity expectation is for Linux only");
        }

        Assert.False(PathContainment.IsWithin(_paths, "/repo", "/REPO/a.cs"));
    }

    [Theory]
    [InlineData(StringComparison.Ordinal, false)]
    [InlineData(StringComparison.OrdinalIgnoreCase, true)]
    public void AppliesTheGivenComparison(StringComparison comparison, bool expected)
    {
        string root = Abs("/kw-containment");
        string recased = Path.Combine(root.ToUpperInvariant(), "a.cs");

        Assert.Equal(expected, PathContainment.IsWithinUsing(_paths, root, recased, comparison));
    }

    [Fact]
    public void RejectsDotDotTraversalOutOfTheDirectory()
    {
        string dir = Abs("/repo");
        string escaping = dir + Path.DirectorySeparatorChar + "sub" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "etc" + Path.DirectorySeparatorChar + "passwd";

        Assert.False(PathContainment.IsWithin(_paths, dir, escaping));
    }

    [Fact]
    public void AcceptsDotDotThatStaysInside()
    {
        string dir = Abs("/repo");
        string inside = dir + Path.DirectorySeparatorChar + "a" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "b.cs";

        Assert.True(PathContainment.IsWithin(_paths, dir, inside));
    }

    [Theory]
    [InlineData("", "/repo/a.cs")]
    [InlineData("  ", "/repo/a.cs")]
    [InlineData("/repo", "")]
    [InlineData("repo", "/repo/a.cs")]
    [InlineData("/repo", "repo/a.cs")]
    public void FailsClosedOnEmptyOrRelativeInput(string directory, string path)
    {
        string dir = directory.StartsWith('/') ? Abs(directory) : directory;
        string target = path.StartsWith('/') ? Abs(path) : path;

        Assert.False(PathContainment.IsWithin(_paths, dir, target));
    }
}
