using KyberWeave.Core.Arbiter.ModelTargets;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review-round contract for the alias rule: a token is trimmed, matched to the harness's accepted aliases
/// ignoring case, and written as the accepted set's own spelling. A full model id must be
/// <c>claude-&lt;family&gt;-&lt;non-blank version&gt;</c> with the family accepted.
/// </summary>
public sealed class ModelTokenAliasesTests
{
    private static readonly HashSet<string> Accepted = new(StringComparer.Ordinal) { "sonnet", "opus", "haiku", "fable" };

    [Theory]
    [InlineData("haiku", "haiku")]
    [InlineData(" haiku", "haiku")]
    [InlineData("haiku\n", "haiku")]
    [InlineData("HAIKU", "haiku")]
    [InlineData("claude-opus-4-1", "opus")]
    [InlineData("claude-haiku-4-5", "haiku")]
    [InlineData("claude-HAIKU-4-5", "haiku")]
    public void AcceptedToken_WritesTheAcceptedSpelling(string token, string expected)
    {
        Assert.Equal(expected, ModelTokenAliases.AliasFor(token, Accepted));
    }

    [Fact]
    public void AcceptedToken_WritesTheCallersOwnSpelling()
    {
        HashSet<string> canonical = new(StringComparer.Ordinal) { "Haiku" };

        Assert.Equal("Haiku", ModelTokenAliases.AliasFor("haiku", canonical));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("claude")]
    [InlineData("claude-")]
    [InlineData("claude-opus")]
    [InlineData("claude-opus-")]
    [InlineData("claude-opus- ")]
    [InlineData("claude--4-5")]
    [InlineData("claude-foo-bar")]
    [InlineData("claude-unknown-9")]
    [InlineData("gpt-4o")]
    public void TokenWithNoAcceptedFamilyOrVersion_IsNotAccepted(string token)
    {
        Assert.Null(ModelTokenAliases.AliasFor(token, Accepted));
    }
}
