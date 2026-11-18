using KyberWeave.Domain.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.FileSystem;

/// <summary>Canonicalizing a documentation root is pure: it needs no file system.</summary>
public sealed class DocsRootPathTests
{
    [Theory]
    [InlineData("docs", "docs")]
    [InlineData(" docs/ ", "docs")]
    [InlineData(".\\docs\\\\guides\\", "docs/guides")]
    [InlineData("./docs/./x", "docs/x")]
    [InlineData(".", "")]
    public void NormalizeGivesForwardSlashesAndNoDotSegments(string value, string expected) =>
        Assert.Equal(expected, DocsRootPath.Normalize(value, "docs-root"));

    [Theory]
    [InlineData("/etc")]
    [InlineData("//server/share")]
    [InlineData("C:/x")]
    [InlineData("c:")]
    [InlineData("\\\\server\\share")]
    public void NormalizeRefusesAnAbsolutePathNamingTheKey(string value)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => DocsRootPath.Normalize(value, "docs-root"));

        Assert.StartsWith("docs-root '", error.Message, StringComparison.Ordinal);
        Assert.Contains("absolute", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("docs/../..")]
    public void NormalizeRefusesAPathThatLeavesTheRepository(string value)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => DocsRootPath.Normalize(value, "docs-root"));

        Assert.Contains("escapes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeRootsKeepsOrderDropsDuplicatesAndMapsTheRepositoryToDot()
    {
        IReadOnlyList<string> roots = DocsRootPath.NormalizeRoots(["docs", "./", "docs/", "guides"], "docs-root");

        Assert.Equal(["docs", DocsRootPath.RepositoryRoot, "guides"], roots);
    }

    [Fact]
    public void NormalizeRootsRefusesAnEmptyList() =>
        Assert.Throws<ArgumentException>(() => DocsRootPath.NormalizeRoots([], "docs-root"));
}
