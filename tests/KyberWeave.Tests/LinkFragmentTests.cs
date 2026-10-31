using System.Text;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T7 RED — body links must keep path, fragment, link text, and source line, including
/// same-document <c>#</c> fragments. Replace <see cref="ProductionBodyLinks"/> with
/// <c>DocumentLoader.ExtractBodyLinks</c> in T8.
/// </summary>
public sealed class LinkFragmentTests
{
    private static readonly MarkdownPipeline LinkPipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .Build();

    [Fact]
    public void ARelativeLinkKeepsPathFragmentTextAndSourceLine()
    {
        const string markdown = "See [the guide](other/doc.md#install-section) for steps.\n";

        IReadOnlyList<BodyLinkExpectation> expected = ContractLinks(markdown);
        IReadOnlyList<BodyLinkExpectation> actual = ProductionBodyLinks(markdown);

        BodyLinkExpectation link = Assert.Single(expected);
        Assert.Equal("other/doc.md", link.Path, StringComparer.Ordinal);
        Assert.Equal("install-section", link.Fragment, StringComparer.Ordinal);
        Assert.Equal("the guide", link.Text, StringComparer.Ordinal);
        Assert.Equal(1, link.Line);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ASameDocumentFragmentLinkIsRetainedWithTextAndLine()
    {
        const string markdown = "Jump to [the overview](#overview) here.\n";

        IReadOnlyList<BodyLinkExpectation> expected = ContractLinks(markdown);
        IReadOnlyList<BodyLinkExpectation> actual = ProductionBodyLinks(markdown);

        BodyLinkExpectation link = Assert.Single(expected);
        Assert.Equal(string.Empty, link.Path, StringComparer.Ordinal);
        Assert.Equal("overview", link.Fragment, StringComparer.Ordinal);
        Assert.Equal("the overview", link.Text, StringComparer.Ordinal);
        Assert.Equal(1, link.Line);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AFragmentedLinkResolvesTheTargetDocumentWithoutPassingFragmentToResolveLink()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/linker.md", """
                                       ---
                                       id: docs/source
                                       title: Source
                                       doc-type: reference
                                       status: current
                                       owner: Maintainers
                                       last-reviewed: 2026-10-31
                                       ---

                                       # Source

                                       ## Topic

                                       ### Detail

                                       See [other doc](other/target.md#install-section) for steps.
                                       """)
            .Write("6-Docs/other/target.md", """
                                             ---
                                             id: docs/target
                                             title: Target
                                             doc-type: reference
                                             status: current
                                             owner: Maintainers
                                             last-reviewed: 2026-10-31
                                             ---

                                             # Target

                                             ## Topic

                                             ### Install section

                                             Target body.
                                             """);

        DocumentSet set = fixture.Load();
        DocumentSectionGraph graph = DocumentSectionGraph.Build(set.Documents);
        DocumentModel target = set.Documents.Single(doc => doc.RelativePath == "6-Docs/other/target.md");
        DocumentLeaf fragmentLeaf = Assert.Single(
            target.Leaves,
            leaf => leaf.Id.Contains("install", StringComparison.OrdinalIgnoreCase));

        Assert.True(graph.InboundLabelsByLeafId.ContainsKey(fragmentLeaf.Id));
    }

    /// <summary>
    /// Stand-in for T8 link extraction via <see cref="DocumentLoader.ExtractBodyLinks"/>.
    /// </summary>
    private static IReadOnlyList<BodyLinkExpectation> ProductionBodyLinks(string markdown)
    {
        List<BodyLinkExpectation> links = new();
        foreach (BodyLink link in DocumentLoader.ExtractBodyLinks(markdown))
        {
            links.Add(new BodyLinkExpectation(link.Path, link.Fragment, link.Text, link.Line));
        }

        return links;
    }

    private static IReadOnlyList<BodyLinkExpectation> ContractLinks(string markdown)
    {
        MarkdownDocument document = Markdown.Parse(markdown, LinkPipeline);
        List<BodyLinkExpectation> links = new();
        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            if (link.Url is null) continue;

            string target = link.Url.ToString().Trim().Trim('<', '>');
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith('/'))
            {
                continue;
            }

            string path = target;
            string fragment = string.Empty;
            int hashIndex = target.IndexOf('#', StringComparison.Ordinal);
            if (hashIndex >= 0)
            {
                path = target[..hashIndex];
                fragment = target[(hashIndex + 1)..];
            }

            int line = link.Line + 1;
            links.Add(new BodyLinkExpectation(path, fragment, PlainText(link), line));
        }

        return links;
    }

    private static string PlainText(LinkInline link)
    {
        StringBuilder builder = new();
        foreach (Inline child in link)
        {
            if (child is LiteralInline literal)
            {
                builder.Append(literal.Content.ToString());
            }
        }

        return builder.ToString();
    }

    private sealed record BodyLinkExpectation(string Path, string Fragment, string Text, int Line);
}
