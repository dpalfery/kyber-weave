using System.Text.Json;
using KyberDash.Data.Harnesses;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>Unit tests for <see cref="HarnessJson"/> counter parsing.</summary>
public class HarnessJsonTests
{
    /// <summary>
    /// A finite whole number inside the signed 64-bit range is the counter.
    /// </summary>
    [Fact]
    public void ReadOptionalNonNegativeLong_FiniteWholeNumber_ReturnsTheValue()
    {
        using JsonDocument document = JsonDocument.Parse("{\"n\":42.0}");

        Assert.Equal(42L, HarnessJson.ReadOptionalNonNegativeLong(document.RootElement, "n"));
    }

    /// <summary>
    /// A finite double above <see cref="long.MaxValue"/> does not cast. The cast overflows.
    /// </summary>
    [Fact]
    public void ReadOptionalNonNegativeLong_AboveInt64Range_ReturnsNull()
    {
        using JsonDocument document = JsonDocument.Parse("{\"n\":1e20}");

        Assert.Null(HarnessJson.ReadOptionalNonNegativeLong(document.RootElement, "n"));
    }

    [Fact]
    public void ReadCodexMessageText_StringContent_ReturnsTheText()
    {
        using JsonDocument document = JsonDocument.Parse("{\"content\":\"hello\"}");

        Assert.Equal("hello", HarnessJson.ReadCodexMessageText(document.RootElement));
    }

    [Fact]
    public void ReadCodexMessageText_BlockArray_JoinsTheTextBlocks()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"content\":[{\"type\":\"text\",\"text\":\"first\"},{\"type\":\"image\"},{\"type\":\"text\",\"text\":\"second\"}]}");

        Assert.Equal("first\nsecond", HarnessJson.ReadCodexMessageText(document.RootElement));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"content\":5}")]
    [InlineData("{\"content\":\"\"}")]
    public void ReadCodexMessageText_MissingNonTextOrEmptyContent_ReturnsNull(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.Null(HarnessJson.ReadCodexMessageText(document.RootElement));
    }
}
