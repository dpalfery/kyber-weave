using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 4538775203: the evaluator's refutation slug and <see cref="ReturnedFinding.IdKey"/> were the same
/// logic written twice. The evaluator now calls the one normaliser, and this pins what it returns for the shapes a
/// refutation header and a stored finding id take.
/// </summary>
public sealed class ArbiterFindingKeyTests
{
    [Theory]
    [InlineData("review-lens/abc", "abc")]
    [InlineData("abc", "abc")]
    [InlineData("a/b/c", "c")]
    [InlineData("review-lens/", "")]
    public void IdKey_ReturnsTheSlugAfterTheLastSlash(string id, string expected) =>
        Assert.Equal(expected, ReturnedFinding.IdKey(id));
}
