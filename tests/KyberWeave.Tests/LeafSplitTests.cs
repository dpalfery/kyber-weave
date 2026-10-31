using System.Globalization;
using System.Text;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T7 RED — leaves come from Markdig with GitHub ids at <c>###</c> boundaries, not from
/// the legacy <c>##</c>-only section split. Replace <see cref="ProductionSplitLeaves"/>
/// with <c>DocumentLoader.SplitLeaves</c> in T8.
/// </summary>
public sealed class LeafSplitTests
{
    private const string DocId = "docs/leaf-fixture";

    private static readonly MarkdownPipeline IdentifierPipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    [Fact]
    public void ATripleHashHeadingIsItsOwnLeaf()
    {
        string body = """
            ## Topic

            Lead before the triple.

            ### Child

            Triple body.
            """;

        IReadOnlyList<LeafExpectation> contract = ContractLeaves(body, DocId);
        IReadOnlyList<LeafExpectation> actual = ProductionSplitLeaves(body, DocId);

        Assert.Equal(2, contract.Count);
        Assert.Contains($"{DocId}#child", contract.Select(leaf => leaf.Id));
        Assert.Equal(contract, actual);
    }

    [Fact]
    public void AFourHashHeadingStaysInsideItsParentTripleHashLeaf()
    {
        string body = """
            ## Topic

            ### Parent

            Before quad.

            #### Detail

            Quad body.

            After quad.
            """;

        IReadOnlyList<LeafExpectation> contract = ContractLeaves(body, DocId);
        IReadOnlyList<LeafExpectation> actual = ProductionSplitLeaves(body, DocId);

        LeafExpectation parentLeaf = Assert.Single(contract, leaf => leaf.Id == $"{DocId}#parent");
        Assert.Contains("Quad body.", parentLeaf.Body, StringComparison.Ordinal);
        Assert.Contains("After quad.", parentLeaf.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(contract, leaf => leaf.Id.Contains("detail", StringComparison.Ordinal));
        Assert.Equal(contract, actual);
    }

    [Fact]
    public void APreambleWithProseGetsTheDocIdHashIdentity()
    {
        string body = """
            # Title

            Preamble prose before any section.

            ## Overview

            Section body.
            """;

        IReadOnlyList<LeafExpectation> contract = ContractLeaves(body, DocId);
        IReadOnlyList<LeafExpectation> actual = ProductionSplitLeaves(body, DocId);

        LeafExpectation preamble = Assert.Single(contract, leaf => leaf.Id == $"{DocId}#");
        Assert.Contains("Preamble prose", preamble.Body, StringComparison.Ordinal);
        Assert.Equal(contract, actual);
    }

    [Fact]
    public void ADuplicateHeadingSlugGetsTheGitHubMinusOneSuffix()
    {
        string body = """
            ## Topic

            ### Overview

            First.

            ### Overview

            Second.
            """;

        IReadOnlyList<LeafExpectation> contract = ContractLeaves(body, DocId);
        IReadOnlyList<LeafExpectation> actual = ProductionSplitLeaves(body, DocId);

        Assert.Contains($"{DocId}#overview", contract.Select(leaf => leaf.Id));
        Assert.Contains($"{DocId}#overview-1", contract.Select(leaf => leaf.Id));
        Assert.Equal(contract, actual);
    }

    [Fact]
    public void AHashInsideAFencedBlockDoesNotSplitLeaves()
    {
        string body = """
            ## Commands

            ```bash
            ## not a heading
            echo hi
            ```

            ### Real

            End.
            """;

        IReadOnlyList<LeafExpectation> contract = ContractLeaves(body, DocId);
        IReadOnlyList<LeafExpectation> actual = ProductionSplitLeaves(body, DocId);

        Assert.DoesNotContain(contract, leaf => leaf.Id.Contains("not-a-heading", StringComparison.Ordinal));
        Assert.Equal(contract, actual);
    }

    [Fact]
    public void AHashInsideATildeFenceDoesNotSplitLeaves()
    {
        string body = """
            ## Commands

            ~~~bash
            ## not a heading
            echo hi
            ~~~

            ### Real

            End.
            """;

        IReadOnlyList<LeafExpectation> contract = ContractLeaves(body, DocId);
        IReadOnlyList<LeafExpectation> actual = ProductionSplitLeaves(body, DocId);

        Assert.DoesNotContain(contract, leaf => leaf.Id.Contains("not-a-heading", StringComparison.Ordinal));
        Assert.Equal(contract, actual);
    }

    /// <summary>
    /// Stand-in for T8 <c>DocumentLoader.SplitLeaves</c>.
    /// </summary>
    private static IReadOnlyList<LeafExpectation> ProductionSplitLeaves(string body, string docId)
    {
        List<LeafExpectation> leaves = new();
        foreach (DocumentLeaf leaf in DocumentLoader.SplitLeaves(body, docId))
        {
            leaves.Add(new LeafExpectation(leaf.Id, leaf.Body));
        }

        return leaves;
    }

    private static IReadOnlyList<LeafExpectation> ContractLeaves(string body, string docId)
    {
        MarkdownDocument document = Markdown.Parse(body, IdentifierPipeline);
        Dictionary<HeadingBlock, string> headingIds = HeadingIds(document, docId);
        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');

        List<LeafExpectation> leaves = new();
        bool inFence = false;
        string? topicSlug = null;
        StringBuilder? leadBuffer = null;
        StringBuilder? leafBuffer = null;
        string? leafId = null;

        void FlushLeaf()
        {
            if (leafId is null || leafBuffer is null) return;
            string text = leafBuffer.ToString().Trim('\n');
            if (text.Length == 0) return;
            leaves.Add(new LeafExpectation(leafId, text));
            leafId = null;
            leafBuffer = null;
        }

        void StartLeaf(string id)
        {
            FlushLeaf();
            leafId = id;
            leafBuffer = new StringBuilder();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("```", StringComparison.Ordinal)
                || trimmedStart.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                leafBuffer?.AppendLine(line);
                leadBuffer?.AppendLine(line);
                continue;
            }

            if (inFence)
            {
                leafBuffer?.AppendLine(line);
                leadBuffer?.AppendLine(line);
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushLeaf();
                topicSlug = Slugify(line[3..].Trim());
                leadBuffer = new StringBuilder();
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                if (leadBuffer is not null)
                {
                    string lead = leadBuffer.ToString().Trim('\n');
                    if (HasProseLines(lead) && topicSlug is not null)
                    {
                        leaves.Add(new LeafExpectation($"{docId}#{topicSlug}", lead));
                    }

                    leadBuffer = null;
                }

                HeadingBlock? heading = FindHeading(document, 3, line[4..].Trim(), i);
                string id = heading is not null && headingIds.TryGetValue(heading, out string? headingId)
                    ? headingId
                    : $"{docId}#{Slugify(line[4..].Trim())}";
                StartLeaf(id);
                continue;
            }

            if (line.StartsWith("#### ", StringComparison.Ordinal))
            {
                leafBuffer?.AppendLine(line);
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                continue;
            }

            if (topicSlug is null)
            {
                if (leafId is null)
                {
                    StartLeaf($"{docId}#");
                }

                leafBuffer?.AppendLine(line);
                continue;
            }

            if (leafId is null)
            {
                leadBuffer?.AppendLine(line);
            }
            else
            {
                leafBuffer?.AppendLine(line);
            }
        }

        if (leadBuffer is not null)
        {
            string lead = leadBuffer.ToString().Trim('\n');
            if (HasProseLines(lead) && topicSlug is not null)
            {
                leaves.Add(new LeafExpectation($"{docId}#{topicSlug}", lead));
            }
        }

        FlushLeaf();
        return leaves;
    }

    private static Dictionary<HeadingBlock, string> HeadingIds(MarkdownDocument document, string docId)
    {
        Dictionary<HeadingBlock, string> ids = new();
        foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
        {
            string? attributeId = heading.GetAttributes().Id;
            string slug = attributeId ?? Slugify(PlainText(heading.Inline));
            ids[heading] = $"{docId}#{slug}";
        }

        return ids;
    }

    private static HeadingBlock? FindHeading(MarkdownDocument document, int level, string text, int lineIndex)
    {
        foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
        {
            if (heading.Level != level)
            {
                continue;
            }

            if (heading.Line != lineIndex)
            {
                continue;
            }

            if (string.Equals(PlainText(heading.Inline), text, StringComparison.Ordinal))
            {
                return heading;
            }
        }

        return null;
    }

    private static string PlainText(ContainerInline? inline)
    {
        if (inline is null) return string.Empty;
        StringBuilder builder = new();
        foreach (Inline child in inline)
        {
            if (child is LiteralInline literal)
            {
                builder.Append(literal.Content.ToString());
            }
        }

        return builder.ToString();
    }

    private static bool HasProseLines(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith('#')) continue;
            return true;
        }

        return false;
    }

    private static string Slugify(string heading)
    {
        StringBuilder builder = new();
        bool lastWasDash = false;
        foreach (char ch in heading.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private sealed record LeafExpectation(string Id, string Body);
}
