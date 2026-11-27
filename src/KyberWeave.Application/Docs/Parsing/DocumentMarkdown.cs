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
    ///     Fenced code blocks are tracked so that a <c>##</c> comment inside a shell fence
    ///     does not open a section.
    /// </summary>
    public static IReadOnlyList<DocumentSection> SplitSections(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        List<DocumentSection> sections = new();
        string[] lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        string? heading = null;
        int headingLine = 1;
        List<string> buffer = new();
        bool inFence = false;

        void Flush()
        {
            // The leading run before the first '##' is usually nothing but the H1, which
            // the caller already has as the document title. Emitting it as a retrievable
            // section makes a title-only stub compete for relevance against real prose —
            // and win, because a two-word section scores high on cosine similarity.
            if (heading is null && !HasProse(buffer)) return;

            // Trim('\n') drops the empty lines at the body's start; count them so the body's
            // own first line is known and a nested heading inside it is numbered truly.
            int leadingBlank = buffer.TakeWhile(static l => l.Length == 0).Count();
            int bufferStart = heading is null ? 1 : headingLine + 1;
            sections.Add(new DocumentSection(
                heading ?? string.Empty,
                string.Join("\n", buffer).Trim('\n'),
                headingLine,
                bufferStart + leadingBlank));
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
                headingLine = i + 1;
                buffer = [];
                continue;
            }

            buffer.Add(line);
        }

        Flush();
        return sections;
    }

    /// <summary>True when the lines contain something other than headings and blanks.</summary>
    private static bool HasProse(IEnumerable<string> lines)
    {
        return lines.Any(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));
    }

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

    private static string LinkPlainText(LinkInline link)
    {
        StringBuilder builder = new();
        foreach (Inline child in link)
            if (child is LiteralInline literal)
                builder.Append(literal.Content.ToString());

        return builder.ToString();
    }

    private static string PlainText(ContainerInline? inline)
    {
        if (inline is null) return string.Empty;

        StringBuilder builder = new();
        foreach (Inline child in inline)
            if (child is LiteralInline literal)
                builder.Append(literal.Content.ToString());

        return builder.ToString();
    }

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
