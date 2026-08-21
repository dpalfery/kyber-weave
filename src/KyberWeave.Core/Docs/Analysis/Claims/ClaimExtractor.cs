using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace KyberWeave.Core.Docs.Analysis.Claims;

/// <summary>Extracts graph-analysis claims from supported Markdown block structures.</summary>
public sealed partial class ClaimExtractor
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UsePreciseSourceLocation()
        .Build();

    private const char InlineLiteralStart = '\uE000';
    private const char InlineLiteralEnd = '\uE001';

    /// <summary>Extracts claims without changing the document body used by retrieval.</summary>
    public ClaimExtractionResult Extract(DocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        DiagnosticReport diagnostics = new DiagnosticReport();
        IgnoreMarkupReadResult ignoreRead = IgnoreMarkupReader.Read(document);
        if (ignoreRead.Diagnostic is not null)
        {
            diagnostics.Add(ignoreRead.Diagnostic);
            return new ClaimExtractionResult([], diagnostics);
        }

        string markdown = ignoreRead.SanitizedBody;
        MarkdownDocument syntax = Markdown.Parse(markdown, Pipeline);
        IReadOnlyList<SectionAtLine> sections = ReadSections(syntax, markdown);
        List<Claim> claims = new List<Claim>();

        foreach (Block block in syntax.Descendants<Block>())
        {
            switch (block)
            {
                case FencedCodeBlock code:
                    AddCodeClaim(claims, code, document, markdown, sections, ignoreRead.Intervals);
                    break;
                case TableRow { IsHeader: false } row:
                    AddTableClaim(claims, row, document, markdown, sections, ignoreRead.Intervals);
                    break;
                case ListItemBlock item:
                    AddListClaim(claims, item, document, markdown, sections, ignoreRead.Intervals);
                    break;
                case ParagraphBlock paragraph when !HasAncestor<ListItemBlock>(paragraph) &&
                                                   !HasAncestor<TableCell>(paragraph):
                    AddProseClaim(claims, paragraph, ClaimKind.Paragraph, document, markdown, sections, ignoreRead.Intervals);
                    break;
            }
        }

        claims.Sort(static (left, right) =>
        {
            int line = left.StartLine.CompareTo(right.StartLine);
            return line != 0 ? line : left.Kind.CompareTo(right.Kind);
        });
        return new ClaimExtractionResult(claims, diagnostics);
    }

    private static void AddProseClaim(
        ICollection<Claim> claims,
        Block block,
        ClaimKind kind,
        DocumentModel document,
        string markdown,
        IReadOnlyList<SectionAtLine> sections,
        IReadOnlyList<IgnoreInterval> ignores)
    {
        string section = SectionFor(block.Line, sections);
        if (section.Length == 0) return;

        string source = Slice(markdown, block);
        string text = DisplayProse(PlainText(source));
        if (text.Length == 0) return;

        AddClaim(claims, kind, text, section, text, Source(block), document, markdown, ignores, false);
    }

    private static void AddListClaim(
        ICollection<Claim> claims,
        ListItemBlock item,
        DocumentModel document,
        string markdown,
        IReadOnlyList<SectionAtLine> sections,
        IReadOnlyList<IgnoreInterval> ignores)
    {
        List<ParagraphBlock> directParagraphs = item.OfType<ParagraphBlock>().ToList();
        if (directParagraphs.Count == 0) return;

        string text = DisplayProse(string.Join(
            " ",
            directParagraphs.Select(paragraph => PlainText(Slice(markdown, paragraph)))));
        if (text.Length == 0) return;

        ParagraphBlock first = directParagraphs[0];
        ParagraphBlock last = directParagraphs[^1];
        SourceBlock span = new SourceBlock(first.Line, first.Span.Start, last.Span.End);
        string section = SectionFor(first.Line, sections);
        if (section.Length == 0) return;

        AddClaim(claims, ClaimKind.ListItem, text, section, text, span, document, markdown, ignores, false);
    }

    private static void AddTableClaim(
        ICollection<Claim> claims,
        TableRow row,
        DocumentModel document,
        string markdown,
        IReadOnlyList<SectionAtLine> sections,
        IReadOnlyList<IgnoreInterval> ignores)
    {
        if (row.Parent is not Table table) return;
        TableRow? header = table.OfType<TableRow>().FirstOrDefault(candidate => candidate.IsHeader);
        if (header is null) return;

        IReadOnlyList<string> headers = ReadCells(header, markdown);
        IReadOnlyList<string> values = ReadCells(row, markdown);
        if (values.Count == 0 || values.All(string.IsNullOrWhiteSpace)) return;

        string text = string.Join(" | ", values);
        IEnumerable<string> pairs = values.Select((value, index) =>
            $"{(index < headers.Count && headers[index].Length > 0 ? headers[index] : $"Column {index + 1}")}: {value}");
        string context = string.Join("\n", pairs);
        string section = SectionFor(row.Line, sections);
        if (section.Length == 0) return;

        AddClaim(claims, ClaimKind.TableRow, text, section, context, Source(row), document, markdown, ignores, false);
    }

    private static IReadOnlyList<string> ReadCells(TableRow row, string markdown) =>
        row.OfType<TableCell>()
            .Select(cell => DisplayProse(PlainText(Slice(markdown, cell))))
            .ToList();

    private static void AddCodeClaim(
        ICollection<Claim> claims,
        FencedCodeBlock code,
        DocumentModel document,
        string markdown,
        IReadOnlyList<SectionAtLine> sections,
        IReadOnlyList<IgnoreInterval> ignores)
    {
        string section = SectionFor(code.Line, sections);
        if (section.Length == 0) return;

        string source = Slice(markdown, code);
        string[] lines = source.Split('\n');
        int contentEnd = code.ClosingFencedCharCount > 0 ? lines.Length - 1 : lines.Length;
        string text = string.Join("\n", lines.Skip(1).Take(Math.Max(0, contentEnd - 1))).Trim('\n');
        if (text.Length == 0) return;

        string opening = lines[0].TrimStart();
        int markerLength = opening.TakeWhile(character => character is '`' or '~').Count();
        string info = markerLength < opening.Length ? opening[markerLength..].Trim() : string.Empty;
        AddClaim(claims, ClaimKind.CodeBlock, text, section, info, Source(code), document, markdown, ignores, true);
    }

    private static void AddClaim(
        ICollection<Claim> claims,
        ClaimKind kind,
        string text,
        string section,
        string contextDetail,
        ISourceBlock block,
        DocumentModel document,
        string markdown,
        IReadOnlyList<IgnoreInterval> ignores,
        bool code)
    {
        int startBodyLine = block.Line + 1;
        int endBodyLine = EndLine(markdown, block);
        string contextualText = contextDetail.Length == 0
            ? section + "\n" + text
            : section + "\n" + contextDetail + (contextDetail == text ? string.Empty : "\n" + text);
        string normalizedContent = code ? NormalizeCode(text) : NormalizeProse(text);
        string normalizedContext = code ? NormalizeCode(contextualText) : NormalizeProse(contextualText);

        claims.Add(new Claim(
            kind,
            text,
            contextualText,
            Hash(normalizedContent),
            Hash(normalizedContext),
            document.Subject,
            document.Frontmatter.Component ?? string.Empty,
            section,
            document.FilePath,
            document.BodyStartLine + startBodyLine - 1,
            document.BodyStartLine + endBodyLine - 1,
            IgnoreFor(startBodyLine, endBodyLine, ignores),
            code ? contextDetail : null,
            document.CodeRefs));
    }

    private static IReadOnlyList<SectionAtLine> ReadSections(MarkdownDocument syntax, string markdown) =>
        syntax.Descendants<HeadingBlock>()
            .Where(heading => heading.Level == 2)
            .Select(heading => new SectionAtLine(
                heading.Line,
                DisplayProse(PlainText(Slice(markdown, heading)))))
            .ToList();

    // Markdig's plain-text renderer deliberately omits inline code. Analysis retains the
    // literal because commands, paths, and enum values are important conflict evidence.
    // Private-use sentinels avoid colliding with source text that happens to contain a
    // printable placeholder such as KYBERINLINELITERAL0END.
    private static string PlainText(string markdown)
    {
        List<string> literals = new List<string>();
        string withPlaceholders = InlineCodePattern().Replace(markdown, match =>
        {
            literals.Add(match.Groups[1].Value);
            return $"{InlineLiteralStart}{literals.Count - 1}{InlineLiteralEnd}";
        });
        string plain = Markdown.ToPlainText(withPlaceholders, Pipeline);
        for (int index = 0; index < literals.Count; index++)
        {
            plain = plain.Replace(
                $"{InlineLiteralStart}{index}{InlineLiteralEnd}",
                $"`{literals[index]}`",
                StringComparison.Ordinal);
        }

        return plain;
    }

    private static string SectionFor(int line, IReadOnlyList<SectionAtLine> sections) =>
        sections.LastOrDefault(section => section.Line < line)?.Heading ?? string.Empty;

    private static bool HasAncestor<T>(Block block) where T : Block
    {
        for (ContainerBlock? parent = block.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is T) return true;
        }

        return false;
    }

    private static string Slice(string markdown, ISourceBlock block)
    {
        if (block.SpanStart < 0 || block.SpanEnd < block.SpanStart || block.SpanStart >= markdown.Length)
        {
            return string.Empty;
        }

        int length = Math.Min(markdown.Length - block.SpanStart, block.SpanEnd - block.SpanStart + 1);
        return markdown.Substring(block.SpanStart, length);
    }

    private static string Slice(string markdown, Block block) =>
        Slice(markdown, Source(block));

    private static SourceBlock Source(Block block) =>
        new(block.Line, block.Span.Start, block.Span.End);

    private static int EndLine(string markdown, ISourceBlock block)
    {
        int end = Math.Min(markdown.Length, block.SpanEnd + 1);
        int line = block.Line + 1;
        for (int index = Math.Max(0, block.SpanStart); index < end; index++)
        {
            if (markdown[index] == '\n') line++;
        }

        return line;
    }

    private static IgnoreRule IgnoreFor(
        int startLine,
        int endLine,
        IReadOnlyList<IgnoreInterval> intervals)
    {
        IgnoreRule result = IgnoreRule.None;
        foreach (IgnoreInterval interval in intervals)
        {
            if (startLine <= interval.EndLine && endLine >= interval.StartLine)
            {
                result |= interval.Rule;
            }
        }

        return result;
    }

    private static string DisplayProse(string text) => WhitespacePattern().Replace(text, " ").Trim();

    private static string NormalizeProse(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormKD).ToLowerInvariant();
        string withoutPunctuation = ProseSeparatorPattern().Replace(decomposed, " ");
        return WhitespacePattern().Replace(withoutPunctuation, " ").Trim();
    }

    private static string NormalizeCode(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd()))
            .Trim('\n');

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex("[\\p{P}\\p{S}]+", RegexOptions.CultureInvariant)]
    private static partial Regex ProseSeparatorPattern();

    [GeneratedRegex("`([^`\\r\\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCodePattern();

    private sealed record SectionAtLine(int Line, string Heading);

    private interface ISourceBlock
    {
        int Line { get; }
        int SpanStart { get; }
        int SpanEnd { get; }
    }

    private sealed record SourceBlock(int Line, int SpanStart, int SpanEnd) : ISourceBlock;
}
