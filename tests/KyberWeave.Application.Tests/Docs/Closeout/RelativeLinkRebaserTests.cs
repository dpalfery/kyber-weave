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
}
