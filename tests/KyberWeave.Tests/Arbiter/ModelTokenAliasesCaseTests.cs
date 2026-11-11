using KyberWeave.Core.Arbiter.ModelTargets;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A full model id is matched ignoring case throughout, the same as its family and version. Failure this guards: an
/// upper-case <c>CLAUDE-</c> prefix misses the alias rule, so a valid model id is never written to the harness.
/// </summary>
public sealed class ModelTokenAliasesCaseTests
{
    private static readonly HashSet<string> Accepted = new(StringComparer.Ordinal) { "sonnet", "opus", "haiku", "fable" };

    [Theory]
    [InlineData("CLAUDE-sonnet-1", "sonnet")]
    [InlineData("Claude-Opus-4-1", "opus")]
    [InlineData("CLAUDE-HAIKU-4-5", "haiku")]
    public void AFullModelIdWithAnyCasedPrefixWritesItsAcceptedFamily(string token, string expected)
    {
        Assert.Equal(expected, ModelTokenAliases.AliasFor(token, Accepted));
    }
}
