using KyberWeave.Core.Review;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
/// The council's gate run names the one code-review skill reference that applies to its cycle, so which part of the
/// skill a reviewer reads is a machine fact and not a choice made from prose.
/// </summary>
public sealed class ReviewGatesCycleReferenceTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string SkillRoot = "products/kyber-squad/skills/code-review";

    [Theory]
    [InlineData(false, "references/first-review.md")]
    [InlineData(true, "references/verifier-cycle.md")]
    public void EachCycleTypeNamesAReferenceTheSkillShips(bool verifier, string expected)
    {
        string reference = ReviewGateReporting.CycleReference(verifier);

        Assert.Equal(expected, reference);
        Assert.True(Corpus.Exists($"{SkillRoot}/{reference}"), reference);
    }
}
