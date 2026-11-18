using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// The shipped operator-channel overlay for the Copilot CLI conductor is a contract file: it exists, it is generic,
/// and it carries the placeholder the renderer replaces with the configured server name.
/// </summary>
public sealed class SquadOperatorChannelOverlayContentTests
{
    [Fact]
    public void OperatorChannelOverlay_IsPresentAsTheContractFile()
    {
        // OverlayText asserts the file exists through the corpus seam before it reads it.
        Assert.NotEmpty(SquadOperatorChannelFixture.OverlayText());
    }

    [Theory]
    [InlineData("Teams")]
    [InlineData("APIM")]
    [InlineData("Azure")]
    public void OperatorChannelOverlay_NamesNeitherTheProductNorItsHosting(string forbiddenTerm)
    {
        string overlay = SquadOperatorChannelFixture.OverlayText();

        Assert.DoesNotContain(forbiddenTerm, overlay, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The overlay is generic: it carries the placeholder that the renderer replaces with the configured
    /// server name, and it hard-codes no server alias.
    /// </summary>
    [Fact]
    public void OperatorChannelOverlay_UsesThePlaceholderAndHardCodesNoServerAlias()
    {
        string overlay = SquadOperatorChannelFixture.OverlayText();

        Assert.Contains(SquadOperatorChannelFixture.Placeholder, overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-channel/*", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("`operator-channel`", overlay, StringComparison.Ordinal);
    }
}
