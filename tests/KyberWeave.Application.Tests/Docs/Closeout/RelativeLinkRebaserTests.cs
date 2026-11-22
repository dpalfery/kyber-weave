using System.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The link rebaser a plan closeout uses before writing the plan into the archive: a relative target is re-expressed
/// for the folder the document is moving to, while everything that is not a relative link stays exactly as written.
/// </summary>
public sealed class RelativeLinkRebaserTests
{
    private const string Plans = "6-Docs/plans";
    private const string Archive = "6-Docs/archive/plans";

    private static string Rebase(string markdown, string from = Plans, string to = Archive) =>
        RelativeLinkRebaser.Rebase(markdown, from, to);

    [Fact]
    public void AMoveOneFolderDeeperRewritesAnInlineLink()
    {
        Assert.Equal(
            "See [the architecture](../../admin-dashboard/architecture.md).",
            Rebase("See [the architecture](../admin-dashboard/architecture.md)."));
    }

    [Fact]
    public void AFragmentIsKept()
    {
        Assert.Equal(
            "[install](../../admin-dashboard/architecture.md#installing)",
            Rebase("[install](../admin-dashboard/architecture.md#installing)"));
    }

    [Fact]
    public void ATitleIsKept()
    {
        Assert.Equal(
            "[b](../../reference/b.md \"the b document\")",
            Rebase("[b](../reference/b.md \"the b document\")"));
    }

    [Fact]
    public void AnImageIsRewritten()
    {
        Assert.Equal(
            "![a screenshot](../../images/diagram.png)",
            Rebase("![a screenshot](../images/diagram.png)"));
    }

    [Fact]
    public void AReferenceDefinitionIsRewritten()
    {
        Assert.Equal(
            "[diagram]: ../../images/diagram.png",
            Rebase("[diagram]: ../images/diagram.png"));
    }

    [Fact]
    public void AnAngleBracketedDestinationIsRewrittenAndKeepsItsBrackets()
    {
        Assert.Equal(
            "[b](<../../reference/a b.md>)",
            Rebase("[b](<../reference/a b.md>)"));
    }

    [Fact]
    public void AnApostropheInADestinationIsNotMistakenForATitle()
    {
        Assert.Equal(
            "[b](../../team/bob's/file.md)",
            Rebase("[b](../team/bob's/file.md)"));
    }

    [Theory]
    [InlineData("[site](https://example.com/docs)")]
    [InlineData("[site](http://example.com/docs)")]
    [InlineData("[file](ftp://example.com/file.md)")]
    [InlineData("[mail](mailto:owner@example.com)")]
    [InlineData("[cdn](//cdn.example.com/app.js)")]
    [InlineData("[docs](/docs/README.md)")]
    [InlineData("[top](#contents)")]
    public void ALinkThatIsNotRelativeToTheFolderIsLeftAlone(string markdown)
    {
        Assert.Equal(markdown, Rebase(markdown));
    }

    [Theory]
    [InlineData("```")]
    [InlineData("~~~")]
    public void LinksInsideAFencedCodeBlockAreLeftAlone(string fence)
    {
        string markdown = $"Before\n\n{fence}text\n[plan](../reference/plan.md)\n{fence}\n\nAfter\n";

        Assert.Equal(markdown, Rebase(markdown));
    }

    [Fact]
    public void ALinkInsideAnInlineCodeSpanIsLeftAlone()
    {
        const string Markdown = "Write `[plan](../reference/plan.md)` to link it.";

        Assert.Equal(Markdown, Rebase(Markdown));
    }

    [Fact]
    public void ALinkThatClimbsAboveTheRepositoryRootIsLeftAsWritten()
    {
        const string Markdown = "[outside](../outside.md)";

        Assert.Equal(Markdown, RelativeLinkRebaser.Rebase(Markdown, string.Empty, "6-Docs/archive/plans"));
    }

    [Theory]
    [InlineData("[a](./a.md)", "[a](../../plans/a.md)")]
    [InlineData("[c](b/../c.md)", "[c](../../plans/c.md)")]
    public void DotSegmentsAreNormalized(string markdown, string expected)
    {
        Assert.Equal(expected, RelativeLinkRebaser.Rebase(markdown, "6-Docs/plans", Archive));
    }

    [Fact]
    public void TheCountReportsOnlyTheTargetsThatChanged()
    {
        const string Markdown = "[a](../reference/a.md) [site](https://example.com) [top](#contents)";

        string rebased = RelativeLinkRebaser.Rebase(Markdown, Plans, Archive, out int rewritten);

        Assert.Equal("[a](../../reference/a.md) [site](https://example.com) [top](#contents)", rebased);
        Assert.Equal(1, rewritten);
    }

    [Theory]
    [InlineData("[^1]: See the design note.")]
    [InlineData("[note]: See the design note.")]
    [InlineData("[note]: ../reference/a.md and some prose")]
    public void ALineThatOnlyLooksLikeAReferenceDefinitionIsLeftAlone(string markdown)
    {
        Assert.Equal(markdown, Rebase(markdown));
    }

    [Theory]
    [InlineData("[a]: ../reference/a.md \"the a document\"", "[a]: ../../reference/a.md \"the a document\"")]
    [InlineData("[a]: ../reference/a.md 'the a document'", "[a]: ../../reference/a.md 'the a document'")]
    [InlineData("[a]: ../reference/a.md (the a document)", "[a]: ../../reference/a.md (the a document)")]
    public void AReferenceDefinitionWithATitleIsRewritten(string markdown, string expected)
    {
        Assert.Equal(expected, Rebase(markdown));
    }

    [Fact]
    public void AFenceAfterABulletMarkerIsRecognised()
    {
        const string Markdown = "- ~~~\n  [a](x.md)\n  ~~~\n- [b](y.md)";

        Assert.Equal("- ~~~\n  [a](x.md)\n  ~~~\n- [b](../../plans/y.md)", Rebase(Markdown));
    }

    [Fact]
    public void AFenceAfterAnOrderedListMarkerIsRecognised()
    {
        const string Markdown = "1. ```\n   [a](x.md)\n   ```\n[a](y.md)";

        Assert.Equal("1. ```\n   [a](x.md)\n   ```\n[a](../../plans/y.md)", Rebase(Markdown));
    }

    [Fact]
    public void AFenceAfterABlockquoteMarkerIsRecognised()
    {
        const string Markdown = "> ~~~\n> [a](x.md)\n> ~~~\n[a](y.md)";

        Assert.Equal("> ~~~\n> [a](x.md)\n> ~~~\n[a](../../plans/y.md)", Rebase(Markdown));
    }

    [Fact]
    public void ACodeSpanDoesNotCrossABlankLine()
    {
        const string Markdown = "Use the ` char.\n\n[a](x.md)\n\nAnd another ` here.";

        Assert.Equal("Use the ` char.\n\n[a](../../plans/x.md)\n\nAnd another ` here.", Rebase(Markdown));
    }

    [Fact]
    public void TheYamlFrontmatterIsNeverRewritten()
    {
        const string Markdown = "---\nid: plans/x\nsource: ../reference/a.md\n---\n\n[a](x.md)";

        Assert.Equal("---\nid: plans/x\nsource: ../reference/a.md\n---\n\n[a](../../plans/x.md)", Rebase(Markdown));
    }

    [Fact]
    public void AnIndexIntoAnArrayIsNotALink()
    {
        const string Markdown = "arr[0](1)";

        Assert.Equal(Markdown, Rebase(Markdown));
    }

    [Fact]
    public void ABackslashSeparatedTargetIsNormalisedBeforeRebasing()
    {
        Assert.Equal("[a](../../x.md)", Rebase("[a](..\\x.md)"));
    }

    [Fact]
    public void AWindowsDrivePathIsLeftAlone()
    {
        const string Markdown = "[a](C:\\docs\\x.md)";

        Assert.Equal(Markdown, Rebase(Markdown));
    }

    [Theory]
    [InlineData("<a href=\"../reference/a.md\">a</a>", "<a href=\"../../reference/a.md\">a</a>")]
    [InlineData("<img src='../images/d.png' alt=\"d\">", "<img src='../../images/d.png' alt=\"d\">")]
    public void AnHtmlAttributeIsRebasedLikeALink(string markdown, string expected)
    {
        Assert.Equal(expected, Rebase(markdown));
    }

    [Theory]
    [InlineData("> [a]: x.md", "> [a]: ../../plans/x.md")]
    [InlineData("- [a]: x.md", "- [a]: ../../plans/x.md")]
    public void AReferenceDefinitionInsideABlockMarkerIsRebased(string markdown, string expected)
    {
        Assert.Equal(expected, Rebase(markdown));
    }

    [Fact]
    public void AReferenceDefinitionWhoseDestinationIsOnTheNextLineIsRebased()
    {
        Assert.Equal("[a]:\n  ../../plans/x.md", Rebase("[a]:\n  x.md"));
    }

    [Fact]
    public void AnUnbalancedLinkOpenerDoesNotScanToTheEndOfTheFile()
    {
        string markdown = string.Concat(Enumerable.Repeat("[a](\n", 8000));

        Stopwatch stopwatch = Stopwatch.StartNew();
        _ = Rebase(markdown);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 200, $"Rebasing 8000 unbalanced link openers took {stopwatch.ElapsedMilliseconds} ms.");
    }
}
