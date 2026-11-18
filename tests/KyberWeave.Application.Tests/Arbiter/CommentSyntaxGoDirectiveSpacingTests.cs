using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Go reads a build constraint whatever the spacing after <c>//</c>: the parser trims the whitespace
/// before <c>+build</c>, so <c>//+build</c> and <c>//  +build</c> are constraints just like <c>// +build</c>.
/// The compiler also reads <c>//line</c>, <c>/*line</c> and <c>//export</c> lines as instructions.
/// Each is code, never a comment, so a review lens cannot skip a change that flips one.
/// </summary>
public sealed class CommentSyntaxGoDirectiveSpacingTests
{
    [Theory]
    [InlineData("// +build linux")]
    [InlineData("//+build linux")]
    [InlineData("//  +build linux")]
    [InlineData("//\t+build linux")]
    [InlineData("//+build")]
    [InlineData("// +build")]
    [InlineData("//+build linux,amd64")]
    [InlineData("//line main.go:10")]
    [InlineData("/*line main.go:10*/")]
    [InlineData("//export MyFunc")]
    public void AGoDirectiveRegardlessOfSpacingIsCode(string line)
    {
        Assert.False(
            CommentSyntax.IsCommentOnly("Program.go", [line]),
            $"'{line}' is a Go directive the compiler or go/build reads, so it is code.");
    }
}
