using KyberWeave.Domain.Hooks;
using Xunit;

namespace KyberWeave.Domain.Tests.Hooks;

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("**/*.cs", "Foo.cs", true)]
    [InlineData("**/*.cs", "src/App/Foo.cs", true)]
    [InlineData("**/*.cs", "src/App/Foo.csproj", false)]
    [InlineData("*.cs", "src/Foo.cs", false)]
    [InlineData("src/*/Foo.cs", "src/App/Foo.cs", true)]
    [InlineData("src/*/Foo.cs", "src/App/Deep/Foo.cs", false)]
    [InlineData("**/obj/**", "src/App/obj/Debug/x.cs", true)]
    [InlineData("**/obj/**", "src/App/Foo.cs", false)]
    [InlineData("Foo?.cs", "Foo1.cs", true)]
    [InlineData("Foo?.cs", "Foo12.cs", false)]
    [InlineData("src/**", "src/a/b/c.txt", true)]
    [InlineData("a.b", "aXb", false)]
    public void MatchesGlobs(string pattern, string path, bool expected) =>
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, path));

    [Fact]
    public void TreatsBackslashesAsSeparators() =>
        Assert.True(GlobMatcher.IsMatch("src/**/*.cs", "src\\App\\Foo.cs"));

    [Fact]
    public void IsMatchAnyNeedsOnlyOneGlob() =>
        Assert.True(GlobMatcher.IsMatchAny(["**/*.ts", "**/*.cs"], "a/b.cs"));
}
