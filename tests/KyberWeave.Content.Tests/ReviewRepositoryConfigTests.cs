using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Configuration;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>This repository's own checked-in <c>review:</c> configuration, as the config reader loads it.</summary>
public sealed class ReviewRepositoryConfigTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>
    /// This repository runs its own review through this configuration, so the checked-in file
    /// has to actually parse — a broken one would otherwise only be discovered by a reviewer
    /// mid-review.
    /// </summary>
    [Fact]
    public void ThisRepositorysOwnReviewConfigurationLoadsCleanlyWithStrictPolicy()
    {
        KyberWeaveConfigLoadResult result = ConfigFileReader.TryLoad(Corpus.Root);

        Assert.True(result.Success, result.Error);
        ReviewConfig review = result.Config!.Review;

        Assert.Contains(review.Gates, g => g.Id == "build");
        Assert.Contains(review.Gates, g => g.Id == "test");
        Assert.All(review.Gates, g => Assert.NotEmpty(g.Run));
        Assert.Equal(ReviewStrictness.Strict, review.Policy.Strictness);

        string configText = Corpus.ReadText(".kyber-weave/kyber-weave.yml");
        Assert.DoesNotContain("always-human", configText, StringComparison.Ordinal);
        Assert.DoesNotContain("max-reviewable-lines", configText, StringComparison.Ordinal);
    }
}
