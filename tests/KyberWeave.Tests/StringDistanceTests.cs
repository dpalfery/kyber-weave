using KyberWeave.Core.Text;
using Xunit;

namespace KyberWeave.Tests;

public sealed class StringDistanceTests
{
    [Theory]
    [InlineData("test", "test")]
    [InlineData("", "")]
    [InlineData("a", "a")]
    [InlineData("Levenshtein", "Levenshtein")]
    public void IdenticalStringsReturnZeroDistance(string a, string b)
    {
        int distance = StringDistance.Levenshtein(a, b);

        Assert.Equal(0, distance);
    }

    [Theory]
    [InlineData("", "abc", 3)]
    [InlineData("hello", "", 5)]
    [InlineData("", "", 0)]
    public void EmptyStringVersusNonEmptyStringReturnsNonEmptyLength(string a, string b, int expected)
    {
        int distance = StringDistance.Levenshtein(a, b);

        Assert.Equal(expected, distance);
    }

    [Theory]
    [InlineData("abc", "ABC", 0)]
    [InlineData("Kitten", "kitten", 0)]
    [InlineData("CaseTest", "casetest", 0)]
    public void CaseInsensitiveComparisonIgnoresCasingDifferences(string a, string b, int expected)
    {
        int distance = StringDistance.Levenshtein(a, b, ignoreCase: true);

        Assert.Equal(expected, distance);
    }

    [Theory]
    [InlineData("abc", "ABC", 3)]
    [InlineData("Kitten", "kitten", 1)]
    [InlineData("CaseTest", "casetest", 2)]
    public void CaseSensitiveComparisonTreatsCasingDifferencesAsSubstitutions(string a, string b, int expected)
    {
        int distance = StringDistance.Levenshtein(a, b, ignoreCase: false);

        Assert.Equal(expected, distance);
    }

    [Theory]
    [InlineData("cat", "cats", 1)]
    [InlineData("cat", "scat", 1)]
    [InlineData("dog", "doog", 1)]
    public void SingleCharacterInsertionReturnsDistanceOfOne(string a, string b, int expected)
    {
        int distance = StringDistance.Levenshtein(a, b);

        Assert.Equal(expected, distance);
    }

    [Theory]
    [InlineData("cats", "cat", 1)]
    [InlineData("scat", "cat", 1)]
    [InlineData("doog", "dog", 1)]
    public void SingleCharacterDeletionReturnsDistanceOfOne(string a, string b, int expected)
    {
        int distance = StringDistance.Levenshtein(a, b);

        Assert.Equal(expected, distance);
    }

    [Theory]
    [InlineData("kitten", "sitten", 1)]
    [InlineData("cat", "cot", 1)]
    [InlineData("bat", "bit", 1)]
    public void SingleCharacterSubstitutionReturnsDistanceOfOne(string a, string b, int expected)
    {
        int distance = StringDistance.Levenshtein(a, b);

        Assert.Equal(expected, distance);
    }

    [Theory]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("rosettacode", "raisethysword", 8)]
    [InlineData("saturday", "sunday", 3)]
    public void MultipleOperationsReturnExpectedDistance(string a, string b, int expected)
    {
        int distance = StringDistance.Levenshtein(a, b);

        Assert.Equal(expected, distance);
    }

    [Fact]
    public void NullSourceStringThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => StringDistance.Levenshtein(null!, "test"));
    }

    [Fact]
    public void NullTargetStringThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => StringDistance.Levenshtein("test", null!));
    }
}
