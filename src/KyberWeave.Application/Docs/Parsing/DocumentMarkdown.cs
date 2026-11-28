using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Docs.Model;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KyberWeave.Core.Docs.Parsing;

/// <summary>The Markdown half of the corpus loader: text in, parsed shapes out.</summary>
/// <remarks>
/// Split from <c>DocumentLoader</c> because the validators need these while holding no
/// repository root: a heading slug, a document id, the links a body declares. Reading a file
/// stays with the loader; nothing here touches the file system. The loader forwards to these,
/// so the corpus and the validators can never parse a document two different ways.
/// </remarks>
public sealed partial class DocumentMarkdown
{
    private static readonly MarkdownPipeline LeafPipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    private static readonly MarkdownPipeline SectionPipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .Build();

    private static readonly MarkdownPipeline LinkDetailPipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .Build();

    public static string ResolveDocId(DocumentFrontmatter frontmatter, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        string? id = frontmatter.Id;
        return string.IsNullOrWhiteSpace(id) ? relativePath : id;
    }

    /// <summary>
    ///     GitHub slug fragments declared by headings in <paramref name="body"/>, using the
    ///     same AutoIdentifiers pipeline as retrieval leaves.
    /// </summary>
    public static IReadOnlySet<string> CollectHeadingFragmentSlugs(string body, string docId)
    {
        MarkdownDocument document = Markdown.Parse(body, LeafPipeline);
        Dictionary<HeadingBlock, string> headingIds = BuildHeadingIds(document, docId);
        HashSet<string> slugs = new(StringComparer.Ordinal);
        foreach (string id in headingIds.Values)
        {
            int hash = id.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0 && hash < id.Length - 1)
                slugs.Add(id[(hash + 1)..]);
        }

        return slugs;
    }

    /// <summary>
    ///     Splits a body on <c>##</c> headings. Deeper headings stay inside their parent
    ///     section: a retrieval unit is a topic, and <c>###</c> is a subdivision of one.
    /// </summary>
    public static IReadOnlyList<DocumentSection> SplitSections(string body) =>
        Split(body, expandNestedHeadings: false);

    /// <summary>
    ///     Splits a body into the units a topic is scored against. A <c>##</c> section that
    ///     contains <c>###</c> headings is expanded to one unit per <c>###</c>, plus its own
    ///     lead-in when that has prose; a section with no <c>###</c> stays whole. Scoring only
    ///     the <c>##</c> heading lets a sibling such as "Security and data handling" win the
    ///     topic "error handling" on one shared word and spend the budget before "Errors and
    ///     diagnostics" is reached.
    /// </summary>
    public static IReadOnlyList<DocumentSection> SplitTopicSections(string body) =>
        Split(body, expandNestedHeadings: true);

    /// <summary>
    ///     The one reading of a heading both splits share: Markdig decides what is a heading, so
    ///     an indented heading, a closing run of hashes, a setext underline and a <c>#</c> line
    ///     inside a <c>~~~</c> fence are read the same way here as everywhere else in the corpus.
    /// </summary>
    private static IReadOnlyList<DocumentSection> Split(string body, bool expandNestedHeadings)
    {
        ArgumentNullException.ThrowIfNull(body);

        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');
        IReadOnlyList<Heading> headings = HeadingsOf(normalized);

        List<DocumentSection> sections = new();

        // The leading run before the first '##' is usually nothing but the H1, which the
        // caller already has as the document title. Emitting it as a retrievable section
        // makes a title-only stub compete for relevance against real prose — and win,
        // because a two-word section scores high on cosine similarity.
        Heading? firstTopic = headings.FirstOrDefault(static h => h.Level == TopicLevel);
        int preambleEnd = firstTopic is null ? lines.Length : firstTopic.Line;
        if (HasProse(lines, 0, preambleEnd)) sections.Add(SectionOf(lines, string.Empty, -1, preambleEnd));

        for (int index = 0; index < headings.Count; index++)
        {
            Heading topic = headings[index];
            if (topic.Level != TopicLevel) continue;

            int end = NextTopicLine(headings, index, lines.Length);
            if (!expandNestedHeadings)
            {
                sections.Add(SectionOf(lines, topic.Text, topic.Line, end));
                continue;
            }

            List<Heading> nested = NestedHeadings(headings, index, end);
            if (nested.Count == 0)
            {
                sections.Add(SectionOf(lines, topic.Text, topic.Line, end));
                continue;
            }

            // The parent's own prose is a unit only when it has any, and it keeps the parent's
            // heading: prose written under "## Coding standards" answers a topic about coding.
            if (HasProse(lines, topic.Line + 1, nested[0].Line))
                sections.Add(SectionOf(lines, topic.Text, topic.Line, nested[0].Line));

            for (int child = 0; child < nested.Count; child++)
            {
                int childEnd = child + 1 < nested.Count ? nested[child + 1].Line : end;
                sections.Add(SectionOf(lines, nested[child].Text, nested[child].Line, childEnd));
            }
        }

        return sections;
    }

    private const int TopicLevel = 2;

    private const int NestedLevel = 3;

    private static IReadOnlyList<Heading> HeadingsOf(string normalized)
    {
        List<Heading> headings = new();
        foreach (HeadingBlock heading in Markdown.Parse(normalized, SectionPipeline).Descendants<HeadingBlock>())
        {
            if (heading.Level is not (TopicLevel or NestedLevel)) continue;

            headings.Add(new Heading(heading.Line, heading.Level, PlainText(heading.Inline).Trim()));
        }

        headings.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        return headings;
    }

    /// <summary>The line of the next <c>##</c> heading after <paramref name="index"/>, or the body's end.</summary>
    private static int NextTopicLine(IReadOnlyList<Heading> headings, int index, int lines)
    {
        for (int next = index + 1; next < headings.Count; next++)
            if (headings[next].Level == TopicLevel) return headings[next].Line;

        return lines;
    }

    /// <summary>The <c>###</c> headings inside the section that starts at <paramref name="index"/>.</summary>
    private static List<Heading> NestedHeadings(IReadOnlyList<Heading> headings, int index, int end)
    {
        List<Heading> nested = new();
        for (int next = index + 1; next < headings.Count && headings[next].Line < end; next++)
            if (headings[next].Level == NestedLevel) nested.Add(headings[next]);

        return nested;
    }

    /// <summary>
    ///     One section from its heading line to <paramref name="endLine"/>, exclusive. The body's
    ///     blank edges are trimmed, and <see cref="DocumentSection.BodyLine"/> records where the
    ///     first surviving line really is, so a nested heading keeps its line.
    /// </summary>
    /// <param name="lines">The body's lines, split on a bare newline.</param>
    /// <param name="heading">The heading text, or empty for the run before the first heading.</param>
    /// <param name="headingLine">The heading's 0-based line, or -1 for the run before the first heading.</param>
    /// <param name="endLine">The 0-based line the section stops before.</param>
    private static DocumentSection SectionOf(string[] lines, string heading, int headingLine, int endLine)
    {
        int start = Math.Max(0, headingLine + 1);
        int end = Math.Clamp(endLine, start, lines.Length);

        int bodyStart = start;
        while (bodyStart < end && string.IsNullOrWhiteSpace(lines[bodyStart])) bodyStart++;

        int bodyEnd = end;
        while (bodyEnd > bodyStart && string.IsNullOrWhiteSpace(lines[bodyEnd - 1])) bodyEnd--;

        return new DocumentSection(
            heading,
            string.Join("\n", lines[bodyStart..bodyEnd]),
            headingLine < 0 ? 1 : headingLine + 1,
            bodyStart + 1);
    }

    private static bool HasProse(string[] lines, int start, int end)
    {
        int from = Math.Max(0, start);
        int to = Math.Clamp(end, from, lines.Length);

        for (int line = from; line < to; line++)
        {
            if (!string.IsNullOrWhiteSpace(lines[line]) && !lines[line].TrimStart().StartsWith('#'))
                return true;
        }

        return false;
    }

    private sealed record Heading(int Line, int Level, string Text);

    /// <summary>
    ///     Splits a body into <c>###</c> retrieval leaves with GitHub slug ids. Deeper headings
    ///     stay inside their parent <c>###</c> leaf. Fenced blocks are ordinary content — a
    ///     <c>##</c> line inside a fence is not a section boundary.
    /// </summary>
    public static IReadOnlyList<DocumentLeaf> SplitLeaves(string body, string docId)
    {
        ArgumentNullException.ThrowIfNull(body);
        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        MarkdownDocument document = Markdown.Parse(normalized, LeafPipeline);
        Dictionary<HeadingBlock, string> headingIds = BuildHeadingIds(document, docId);
        LeafAccumulator accumulator = new(normalized, docId, headingIds);
        return accumulator.Build(document);
    }

    /// <summary>
    ///     Gathers the retrieval leaves while the block tree is walked: the current <c>##</c> topic, the
    ///     text accumulated before a topic's first <c>###</c> leaf, and the current leaf's buffer.
    /// </summary>
    private sealed class LeafAccumulator
    {
        private readonly string _normalized;
        private readonly string _docId;
        private readonly Dictionary<HeadingBlock, string> _headingIds;
        private readonly List<DocumentLeaf> _leaves = new();

        private string? _topicId;
        private StringBuilder? _leadBuffer;
        private string? _leafId;
        private StringBuilder? _leafBuffer;

        public LeafAccumulator(string normalized, string docId, Dictionary<HeadingBlock, string> headingIds)
        {
            _normalized = normalized;
            _docId = docId;
            _headingIds = headingIds;
        }

        public IReadOnlyList<DocumentLeaf> Build(MarkdownDocument document)
        {
            foreach (Block block in document) VisitBlock(block);

            if (_leadBuffer is not null)
            {
                string lead = _leadBuffer.ToString().Trim('\n');
                if (HasProseLines(lead) && _topicId is not null) _leaves.Add(new DocumentLeaf(_topicId, lead));
            }

            FlushLeaf();
            return _leaves;
        }

        private void FlushLeaf()
        {
            if (_leafId is null || _leafBuffer is null) return;

            string text = _leafBuffer.ToString().Trim('\n');
            if (text.Length == 0) return;

            _leaves.Add(new DocumentLeaf(_leafId, text));
            _leafId = null;
            _leafBuffer = null;
        }

        private void StartLeaf(string id)
        {
            FlushLeaf();
            _leafId = id;
            _leafBuffer = new StringBuilder();
        }

        private void AppendContent(string text)
        {
            if (text.Length == 0) return;

            if (_topicId is null)
            {
                if (_leafId is null) StartLeaf($"{_docId}#");

                AppendWithNewline(_leafBuffer, text);
                return;
            }

            if (_leafId is null)
            {
                _leadBuffer ??= new StringBuilder();
                AppendWithNewline(_leadBuffer, text);
            }
            else
            {
                AppendWithNewline(_leafBuffer, text);
            }
        }

        private void HandleHeading(HeadingBlock heading)
        {
            if (heading.Level == 1) return;

            if (heading.Level == 2)
            {
                FlushLeaf();
                _topicId = _headingIds[heading];
                _leadBuffer = new StringBuilder();
                return;
            }

            if (heading.Level == 3)
            {
                if (_leadBuffer is not null)
                {
                    string lead = _leadBuffer.ToString().Trim('\n');
                    if (HasProseLines(lead) && _topicId is not null) _leaves.Add(new DocumentLeaf(_topicId, lead));

                    _leadBuffer = null;
                }

                StartLeaf(_headingIds[heading]);
                return;
            }

            if (_leafId is not null)
            {
                AppendWithNewline(_leafBuffer, Slice(_normalized, heading.Span));
            }
            else if (_topicId is not null)
            {
                _leadBuffer ??= new StringBuilder();
                AppendWithNewline(_leadBuffer, Slice(_normalized, heading.Span));
            }
        }

        private void VisitBlock(Block block)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    HandleHeading(heading);
                    return;
                case FencedCodeBlock:
                case ParagraphBlock:
                case HtmlBlock:
                case ThematicBreakBlock:
                case CodeBlock:
                    AppendContent(Slice(_normalized, block.Span));
                    return;
                case ContainerBlock container:
                    foreach (Block child in container) VisitBlock(child);

                    return;
            }
        }
    }

    /// <summary>
    ///     Reads relative Markdown links with fragments, anchor text, and 1-based source lines.
    /// </summary>
    public static IReadOnlyList<BodyLink> ExtractBodyLinks(string markdown)
    {
        MarkdownDocument document = Markdown.Parse(markdown, LinkDetailPipeline);
        List<BodyLink> links = new();
        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            if (link.Url is null) continue;

            string target = link.Url.Trim().Trim('<', '>');
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith('/'))
                continue;

            string path = target;
            string fragment = string.Empty;
            int hashIndex = target.IndexOf('#', StringComparison.Ordinal);
            if (hashIndex >= 0)
            {
                path = target[..hashIndex];
                fragment = target[(hashIndex + 1)..];
            }

            int line = link.Line + 1;
            links.Add(new BodyLink(path, fragment, LinkPlainText(link), line));
        }

        return links;
    }

    private static Dictionary<HeadingBlock, string> BuildHeadingIds(MarkdownDocument document, string docId)
    {
        Dictionary<HeadingBlock, string> ids = new();
        foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
        {
            string? attributeId = heading.GetAttributes().Id;
            string slug = attributeId ?? SlugifyHeading(PlainText(heading.Inline));
            ids[heading] = $"{docId}#{slug}";
        }

        return ids;
    }

    private static void AppendWithNewline(StringBuilder? buffer, string text)
    {
        if (buffer is null || text.Length == 0) return;

        if (buffer.Length > 0)
        {
            if (buffer[buffer.Length - 1] != '\n') buffer.Append('\n');

            buffer.Append('\n');
        }

        buffer.Append(text);
    }

    private static string Slice(string source, SourceSpan span)
    {
        return source.Substring(span.Start, span.Length);
    }

    /// <summary>True when the text holds a line that is neither blank nor a heading.</summary>
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

    /// <summary>
    /// The text a heading or link displays. Walks the whole inline tree rather than the
    /// container's direct children, because a heading such as <c>### `unsafe`</c> holds its
    /// word in a <see cref="CodeInline"/> and one written with emphasis holds it in a nested
    /// container; reading only literals drops those words from the heading text.
    /// </summary>
    private static string PlainText(ContainerInline? inline)
    {
        if (inline is null) return string.Empty;

        StringBuilder builder = new();
        AppendPlainText(inline, builder);
        return builder.ToString();
    }

    private static void AppendPlainText(ContainerInline container, StringBuilder builder)
    {
        foreach (Inline child in container)
        {
            switch (child)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline nested:
                    AppendPlainText(nested, builder);
                    break;
            }
        }
    }

    private static string LinkPlainText(LinkInline link) => PlainText(link);

    private static string SlugifyHeading(string heading)
    {
        StringBuilder builder = new();
        bool lastWasDash = false;
        foreach (char ch in heading.Normalize(NormalizationForm.FormKD))
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

        return builder.ToString().Trim('-');
    }


    public static IReadOnlyList<string> ExtractRelativeLinks(string markdown)
    {
        List<string> links = new();
        foreach (Match match in LinkPattern().Matches(markdown))
        {
            string target = match.Groups[1].Value.Trim().Trim('<', '>');
            int hashIndex = target.IndexOf('#', StringComparison.Ordinal);
            if (hashIndex >= 0) target = target[..hashIndex];
            if (target.Length == 0) continue;
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
            if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            if (target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            if (target.StartsWith('/')) continue;
            links.Add(target);
        }

        return links;
    }

    [GeneratedRegex(@"\]\(([^)]*)\)")]
    private static partial Regex LinkPattern();
}
