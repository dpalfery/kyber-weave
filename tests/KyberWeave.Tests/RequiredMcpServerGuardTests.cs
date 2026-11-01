using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The promise the declared server record makes on its own, independent of the loader that happens
/// to validate before constructing one today.
/// </summary>
/// <remarks>
/// The loader refuses a malformed declaration before it builds a server, so a hand-fed case here
/// looks like it proves nothing about <c>kyber-weave.yml</c>. It proves something narrower and
/// still worth pinning: <see cref="RequiredMcpServer"/> is public, so the loader is not the only
/// caller it will ever have, and a caller that skipped it would otherwise inherit a server with no
/// name and no program to launch. The cases an operator can write into the document are pinned
/// against real YAML by the declaration surface suite.
/// </remarks>
public sealed class RequiredMcpServerGuardTests
{
    private const string ShippedBinary = "kyber-weave-mcp";

    /// <summary>The name is the server key a harness entry, a diagnostic and a TOML table use.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void TheDeclaredRecordRefusesABlankName(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            new RequiredMcpServer(name, [ShippedBinary], McpProvenance.FirstParty));
    }

    /// <summary>A null command is not "no arguments" — it is a launch with no program at all.</summary>
    [Fact]
    public void TheDeclaredRecordRefusesAMissingCommand()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RequiredMcpServer("kyber-weave", null!, McpProvenance.FirstParty));
    }

    /// <summary>
    /// An empty argv names no program, which is the same misspelling as an absent command and not
    /// something a launcher could do anything with.
    /// </summary>
    [Fact]
    public void TheDeclaredRecordRefusesAnEmptyCommand()
    {
        Assert.Throws<ArgumentException>(() =>
            new RequiredMcpServer("kyber-weave", [], McpProvenance.FirstParty));
    }

    /// <summary>
    /// An argv element that names nothing is passed through as an empty argument. Only the loader
    /// checks for it today, so a constructed server could carry one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void TheDeclaredRecordRefusesABlankCommandElement(string element)
    {
        Assert.Throws<ArgumentException>(() =>
            new RequiredMcpServer("kyber-weave", [ShippedBinary, element], McpProvenance.FirstParty));
    }
}
