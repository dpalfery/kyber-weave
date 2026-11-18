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

    /// <summary>
    /// A constructed first-party server makes the same claim a document does, so it carries the same
    /// rule. Anything downstream may report "this installation is broken" on provenance alone, and
    /// that sentence has to be true of the binary the server actually launches.
    /// </summary>
    [Fact]
    public void TheDeclaredRecordRefusesAFirstPartyClaimAboutAnotherBinary()
    {
        Assert.Throws<ArgumentException>(() =>
            new RequiredMcpServer("kyber-weave", ["codegraph", "serve"], McpProvenance.FirstParty));
    }

    /// <summary>
    /// A program whose file name merely carries the shipped binary's name inside it is a different
    /// program, so a first-party claim about it is false.
    /// </summary>
    /// <remarks>
    /// The claim is what every downstream remedy is built on, and one of them is "run
    /// <c>kyber-weave update</c>". Accepting a lookalike made that advice a promise about a program
    /// Kyber-Weave never shipped: the operator would run the update, the missing binary would stay
    /// missing, and the report would keep asserting the installation was broken.
    /// </remarks>
    [Theory]
    [InlineData("fake-kyber-weave-mcp")]
    [InlineData("kyber-weave-mcp-helper")]
    [InlineData("my-kyber-weave-mcp")]
    [InlineData("kyber-weave-mcp.bak")]
    [InlineData("./tools/fake-kyber-weave-mcp")]
    public void TheDeclaredRecordRefusesAFirstPartyClaimAboutALookalikeName(string program)
    {
        Assert.Throws<ArgumentException>(() =>
            new RequiredMcpServer("kyber-weave", [program, "--repo-root", "."], McpProvenance.FirstParty));
    }

    /// <summary>
    /// The shipped name is compared as a whole file name, so how a platform spells that name is not
    /// a different program: a directory prefix, the case a file system ignores, and the executable
    /// suffix a Windows install appends all still launch the binary Kyber-Weave ships.
    /// </summary>
    /// <remarks>
    /// The suffix is accepted on any platform on purpose. A declaration is committed, so the same
    /// <c>kyber-weave.yml</c> is read on a machine that installed the binary as
    /// <c>kyber-weave-mcp.exe</c> and on one that did not, and a host whose declaration loads on one
    /// platform and is refused on another would make the guard an accident of where it ran.
    /// </remarks>
    [Theory]
    [InlineData("kyber-weave-mcp")]
    [InlineData("./bin/kyber-weave-mcp")]
    [InlineData("/opt/kyber/bin/kyber-weave-mcp")]
    [InlineData("KYBER-WEAVE-MCP")]
    [InlineData("kyber-weave-mcp.exe")]
    [InlineData("/opt/kyber/bin/kyber-weave-mcp.exe")]
    [InlineData("kyber-weave-mcp.EXE")]
    public void TheDeclaredRecordAcceptsTheShippedBinaryHoweverAPlatformSpellsIt(string program)
    {
        RequiredMcpServer server =
            new("kyber-weave", [program, "--repo-root", "."], McpProvenance.FirstParty);

        Assert.Equal(McpProvenance.FirstParty, server.Provenance);
        Assert.Equal(program, server.Command[0]);
    }

    /// <summary>
    /// A third-party server is a binary Kyber-Weave does not ship by definition, so its command is
    /// somebody else's program and there is nothing to refuse.
    /// </summary>
    [Fact]
    public void TheDeclaredRecordAcceptsAThirdPartyCommand()
    {
        RequiredMcpServer server = new("codegraph", ["codegraph", "serve"], McpProvenance.ThirdParty);

        Assert.Equal(McpProvenance.ThirdParty, server.Provenance);
    }
}
