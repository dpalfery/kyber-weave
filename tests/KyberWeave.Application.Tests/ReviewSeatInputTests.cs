using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The limits every review command applies to what a seat wrote. A seat is untrusted, so text is cut at one rule
/// everywhere, and the cut never leaves half of a surrogate pair.
/// </summary>
public sealed class ReviewSeatInputTests
{
    [Fact]
    public void TextAtTheLimitIsKeptWholeAndOneOverIsCutAndMarked()
    {
        string atLimit = new('a', ReviewSeatInput.MaxTextCharacters);
        string overLimit = new('a', ReviewSeatInput.MaxTextCharacters + 1);

        Assert.Same(atLimit, ReviewSeatInput.Clip(atLimit));
        Assert.Equal(new string('a', ReviewSeatInput.MaxTextCharacters) + "…", ReviewSeatInput.Clip(overLimit));
    }

    [Fact]
    public void ANullStaysNull()
    {
        Assert.Null(ReviewSeatInput.Clip(null));
    }

    /// <summary>A cut between the two halves of a surrogate pair would leave an unpaired surrogate in a report.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(200)]
    [InlineData(60)]
    public void ASurrogatePairAtTheCutIsDroppedWholeNotSplit(int limit)
    {
        string text = new string('a', limit - 1) + "😀" + "tail";

        string clipped = ReviewSeatInput.Clip(text, limit);

        Assert.Equal(new string('a', limit - 1) + "…", clipped);
        Assert.DoesNotContain(clipped, character => char.IsSurrogate(character));
    }

    [Fact]
    public void ASurrogatePairAtTheDefaultCutIsDroppedWholeNotSplit()
    {
        string text = new string('a', ReviewSeatInput.MaxTextCharacters - 1) + "😀" + "tail";

        string? clipped = ReviewSeatInput.Clip(text);

        Assert.Equal(new string('a', ReviewSeatInput.MaxTextCharacters - 1) + "…", clipped);
    }

    [Fact]
    public void ASurrogatePairEndingExactlyAtTheLimitIsKept()
    {
        string text = new string('a', 8) + "😀" + "tail";

        Assert.Equal(new string('a', 8) + "😀…", ReviewSeatInput.Clip(text, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ALimitBelowOneIsRefused(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReviewSeatInput.Clip("text", limit));
    }

    [Theory]
    [InlineData(8192, 0, true)]
    [InlineData(8193, 0, false)]
    [InlineData(1, 99, true)]
    [InlineData(1, 100, false)]
    public void AnExcerptIsWithinLimitsUpToTheCharacterAndLineCaps(int characters, int newlines, bool within)
    {
        string excerpt = new string('x', characters) + new string('\n', newlines);

        Assert.Equal(within, ReviewSeatInput.ExcerptWithinLimits(excerpt));
    }
}
