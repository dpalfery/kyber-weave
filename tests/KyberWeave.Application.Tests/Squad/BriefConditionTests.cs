using KyberWeave.Core.Squad.Briefs;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>A brief condition is data in Domain; Application parses, spells and evaluates it.</summary>
public sealed class BriefConditionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("always")]
    public void NoClausesAlwaysHold(string text)
    {
        BriefCondition condition = BriefConditionExtensions.ParseCondition(text, "test");

        Assert.Empty(condition.Clauses);
        Assert.Equal("always", condition.Spell());
        Assert.True(condition.Holds(new BriefFacts("plan", "supervised", "standard")));
    }

    [Fact]
    public void AClauseHoldsOnlyForItsValuesAndAnUnknownFactHolds()
    {
        BriefCondition condition = BriefConditionExtensions.ParseCondition("path=plan|spec; mode=test-first", "test");

        Assert.Equal("path=plan|spec; mode=test-first", condition.Spell());
        Assert.True(condition.Holds(new BriefFacts("spec", null, "test-first")));
        Assert.True(condition.Holds(new BriefFacts(null, null, null)));
        Assert.False(condition.Holds(new BriefFacts("intake", null, "test-first")));
        Assert.False(condition.Holds(new BriefFacts("plan", null, "standard")));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("colour=red")]
    [InlineData("path=nowhere")]
    [InlineData("path=")]
    public void AnUnknownFactOrValueNamesTheLocation(string text)
    {
        BriefMapException error = Assert.Throws<BriefMapException>(() => BriefConditionExtensions.ParseCondition(text, "agent § heading"));

        Assert.StartsWith("agent § heading:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FactsAreReadByName()
    {
        BriefFacts facts = new("plan", "autonomous", "standard");

        Assert.Equal("plan", facts.Get("path"));
        Assert.Equal("autonomous", facts.Get("autonomy"));
        Assert.Equal("standard", facts.Get("mode"));
        Assert.Null(facts.Get("other"));
    }
}
