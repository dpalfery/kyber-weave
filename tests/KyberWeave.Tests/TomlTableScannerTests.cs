using KyberWeave.Core.Docs.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The Codex read side: a string-region-aware, line-oriented classification of a TOML document
/// that answers one question — which <c>[mcp_servers.&lt;name&gt;]</c> tables does this file declare?
/// </summary>
/// <remarks>
/// <para>
/// The scanner exists because no TOML parser ships in this repository — Core takes Markdig and
/// YamlDotNet only, and a hand-rolled parser is the duplicate-table corruption risk the write
/// bounds forbid. It is a classifier rather than a parser, so it answers only what it can see and
/// refuses everything else. The direction of that refusal is the whole point: a false "absent"
/// tells the operator a working configuration is broken, and a false "present" lets a writer add
/// a table to a document it never understood.
/// </para>
/// <para>
/// Every case below is a shape a real Codex <c>config.toml</c> carries: the header itself, a
/// header-shaped line inside a multi-line string (a note explaining how to wire it), a
/// commented-out header left behind by a failed setup, and the ways a document stops being
/// classifiable — an unterminated <c>"""</c> region, an unterminated single-line string, a header
/// missing its closing bracket, and a header name TOML cannot express as a key.
/// </para>
/// </remarks>
public sealed class TomlTableScannerTests
{
    private const string MultilineBasicDelimiter = "\"\"\"";

    [Fact]
    public void AHeaderOutsideAStringRegionNamesTheServerItDeclares()
    {
        TomlScanReport report = TomlTableScanner.Scan("""
            [mcp_servers.kyber-weave]
            command = "kyber-weave-mcp"
            args = ["--repo-root", "."]
            """);

        Assert.Equal(TomlScanStatus.Classified, report.Status);
        string server = Assert.Single(report.Servers);
        Assert.Equal("kyber-weave", server);
    }

    /// <summary>
    /// A <c>"""</c> region is where a Codex user documents their own configuration, so a header
    /// quoted inside one is prose. Counting it would report the server wired on a file that
    /// declares nothing, which is the failure that lets a broken session ship.
    /// </summary>
    [Fact]
    public void AHeaderInsideAMultilineBasicStringIsNotAServer()
    {
        string document = string.Join(
            '\n',
            $"note = {MultilineBasicDelimiter}",
            "[mcp_servers.ghost]",
            MultilineBasicDelimiter,
            "command = \"kyber-weave-mcp\"");

        TomlScanReport report = TomlTableScanner.Scan(document);

        Assert.Equal(TomlScanStatus.Classified, report.Status);
        AssertNoServers(report);
    }

    [Fact]
    public void AHeaderInsideAMultilineLiteralStringIsNotAServer()
    {
        string document = string.Join(
            '\n',
            "note = '''",
            "[mcp_servers.ghost]",
            "'''",
            "command = \"kyber-weave-mcp\"");

        TomlScanReport report = TomlTableScanner.Scan(document);

        Assert.Equal(TomlScanStatus.Classified, report.Status);
        AssertNoServers(report);
    }

    [Fact]
    public void AHeaderInsideASingleLineStringIsNotAServer()
    {
        TomlScanReport report = TomlTableScanner.Scan(
            "note = \"see [mcp_servers.ghost] in the runbook\"\ncommand = \"kyber-weave-mcp\"\n");

        Assert.Equal(TomlScanStatus.Classified, report.Status);
        AssertNoServers(report);
    }

    /// <summary>
    /// A commented-out header is not a table. It is also the most common shape of a deliberately
    /// unwired server, so treating it as wired is the difference between a working check and one
    /// that silently confirms a configuration nobody has.
    /// </summary>
    [Fact]
    public void ACommentedOutHeaderIsNotAServer()
    {
        string document = string.Join(
            '\n',
            "# [mcp_servers.ghost]",
            "[mcp_servers.kyber-weave]",
            "command = \"kyber-weave-mcp\"");

        TomlScanReport report = TomlTableScanner.Scan(document);

        Assert.Equal(TomlScanStatus.Classified, report.Status);
        string server = Assert.Single(report.Servers);
        Assert.Equal("kyber-weave", server);
    }

    [Fact]
    public void ARealHeaderBesideAStringRegionHeaderFindsOnlyTheRealOne()
    {
        string document = string.Join(
            '\n',
            $"note = {MultilineBasicDelimiter}",
            "[mcp_servers.ghost]",
            MultilineBasicDelimiter,
            "[mcp_servers.kyber-weave]",
            "command = \"kyber-weave-mcp\"");

        TomlScanReport report = TomlTableScanner.Scan(document);

        Assert.Equal(TomlScanStatus.Classified, report.Status);
        string server = Assert.Single(report.Servers);
        Assert.Equal("kyber-weave", server);
    }

    /// <summary>
    /// An unterminated <c>"""</c> region leaves the rest of the document unclassifiable: any
    /// header after it may be prose. The refusal carries no partial result on purpose — a caller
    /// handed the tables found before the uncertainty would treat "not in that list" as "absent".
    /// </summary>
    [Fact]
    public void AnUnbalancedMultilineDelimiterIsRefusedWithNoPartialResult()
    {
        string document = string.Join(
            '\n',
            "[mcp_servers.kyber-weave]",
            $"note = {MultilineBasicDelimiter}",
            "[mcp_servers.ghost]");

        TomlScanReport report = TomlTableScanner.Scan(document);

        Assert.Equal(TomlScanStatus.Refused, report.Status);
        AssertNoServers(report);
        Assert.False(string.IsNullOrWhiteSpace(report.Reason), "a refusal must say why it refused");
    }

    [Fact]
    public void AnUnterminatedSingleLineStringIsRefusedWithNoPartialResult()
    {
        TomlScanReport report = TomlTableScanner.Scan("[mcp_servers.kyber-weave]\ncommand = \"kyber-weave-mcp\n");

        Assert.Equal(TomlScanStatus.Refused, report.Status);
        AssertNoServers(report);
    }

    [Fact]
    public void AnUnterminatedHeaderLineIsRefusedWithNoPartialResult()
    {
        TomlScanReport report = TomlTableScanner.Scan("[mcp_servers.kyber-weave\ncommand = \"kyber-weave-mcp\"\n");

        Assert.Equal(TomlScanStatus.Refused, report.Status);
        AssertNoServers(report);
    }

    /// <summary>
    /// A name with a space is not a TOML key. Reporting it under a repaired spelling would let
    /// the check answer for a table the file never declared.
    /// </summary>
    [Fact]
    public void AHeaderNameThatCannotBeAKeyIsRefusedRatherThanRenamed()
    {
        TomlScanReport report = TomlTableScanner.Scan("[mcp_servers.ky ber]\ncommand = \"kyber-weave-mcp\"\n");

        Assert.Equal(TomlScanStatus.Refused, report.Status);
        AssertNoServers(report);
    }

    /// <summary>
    /// An empty document is classified and declares nothing: a `.codex/` marker beside an empty
    /// <c>config.toml</c> is a harness in play with the server unwired, not an unreadable file.
    /// </summary>
    [Fact]
    public void AnEmptyDocumentIsClassifiedWithNoServers()
    {
        TomlScanReport empty = TomlTableScanner.Scan(string.Empty);
        TomlScanReport blank = TomlTableScanner.Scan("   \n\n");

        Assert.Equal(TomlScanStatus.Classified, empty.Status);
        AssertNoServers(empty);
        Assert.Equal(TomlScanStatus.Classified, blank.Status);
        AssertNoServers(blank);
    }

    private static void AssertNoServers(TomlScanReport report)
    {
        Assert.True(
            report.Servers.Count == 0,
            $"expected no [mcp_servers.*] table, but the scanner reported: {string.Join(", ", report.Servers)}");
    }
}
