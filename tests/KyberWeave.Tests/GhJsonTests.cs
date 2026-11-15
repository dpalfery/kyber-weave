using System.Text.Json;
using KyberWeave.Core.Docs.Delivery;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>Every field read from <c>gh</c> JSON is checked for being there, the right type, and (for text) not empty.</summary>
public sealed class GhJsonTests
{
    private static JsonElement Object(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("""{"f":"x"}""", true, "x", null)]
    [InlineData("""{"f":""}""", false, "", "'f' is empty")]
    [InlineData("""{"f":"  \t\n"}""", false, "", "'f' is empty")]
    [InlineData("""{}""", false, "", "'f' is missing")]
    [InlineData("""{"f":null}""", false, "", "'f' is not a string")]
    [InlineData("""{"f":5}""", false, "", "'f' is not a string")]
    [InlineData("""{"f":["x"]}""", false, "", "'f' is not a string")]
    [InlineData("""[]""", false, "", "the answer is not an object")]
    public void RequiredTextNeedsANonBlankString(string json, bool usable, string expected, string? problem)
    {
        bool ok = GhJson.TryRequiredText(Object(json), "f", out string value, out string? reason);

        Assert.Equal(usable, ok);
        Assert.Equal(expected, value);
        Assert.Equal(problem, reason);
    }

    [Theory]
    [InlineData("""{"f":""}""", true, "")]
    [InlineData("""{"f":"x"}""", true, "x")]
    [InlineData("""{}""", false, "")]
    [InlineData("""{"f":false}""", false, "")]
    public void RequiredStringMayBeEmptyButNotAbsentOrAnotherType(string json, bool usable, string expected)
    {
        Assert.Equal(usable, GhJson.TryRequiredString(Object(json), "f", out string value, out _));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("""{"f":7}""", true, 7, null)]
    [InlineData("""{"f":0}""", true, 0, null)]
    [InlineData("""{}""", false, 0, "'f' is missing")]
    [InlineData("""{"f":"7"}""", false, 0, "'f' is not a whole number")]
    [InlineData("""{"f":1.5}""", false, 0, "'f' is not a whole number")]
    [InlineData("""{"f":99999999999}""", false, 0, "'f' is not a whole number")]
    [InlineData("""{"f":null}""", false, 0, "'f' is not a whole number")]
    public void RequiredNumberNeedsAWholeNumberThatFits(string json, bool usable, int expected, string? problem)
    {
        Assert.Equal(usable, GhJson.TryRequiredNumber(Object(json), "f", out int value, out string? reason));
        Assert.Equal(expected, value);
        Assert.Equal(problem, reason);
    }

    [Theory]
    [InlineData("""{"f":true}""", true, true)]
    [InlineData("""{"f":false}""", true, false)]
    [InlineData("""{}""", false, false)]
    [InlineData("""{"f":"true"}""", false, false)]
    [InlineData("""{"f":null}""", false, false)]
    public void RequiredBooleanNeedsTrueOrFalse(string json, bool usable, bool expected)
    {
        Assert.Equal(usable, GhJson.TryRequiredBoolean(Object(json), "f", out bool value, out _));
        Assert.Equal(expected, value);
    }
}
