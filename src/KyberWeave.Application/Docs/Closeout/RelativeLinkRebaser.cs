using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>
/// Rewrites the relative link targets in a Markdown document so they resolve to the same files after the document
/// moves to another folder, such as a plan moving from the plans folder into the archive.
/// </summary>
/// <remarks>
/// <para>
/// A link is written for the folder the document is in, so a move one folder deeper breaks every relative link in it
/// unless the targets are re-expressed. Inline links and images, reference definitions (including ones written inside
/// a blockquote or a list item), <c>href</c> and <c>src</c> values in inline HTML tags, and targets written with
/// backslashes are rewritten; absolute URLs, root-absolute paths, fragment-only links, the YAML frontmatter and
/// anything inside a code fence or an inline code span are left exactly as written.
/// </para>
/// <para>
/// Folders are repository-relative and forward-slashed. A target that climbs above the repository root cannot be
/// re-expressed and is left as written.
/// </para>
/// <para>
/// Known limit: this is a text scan rather than a full Markdown parser, so a link inside a four-space indented code
/// block, an HTML comment, or a <c>&lt;code&gt;</c> element is still rewritten even though a renderer shows it
/// literally.
/// </para>
/// </remarks>
public static partial class RelativeLinkRebaser
{
    /// <summary>Returns <paramref name="markdown"/> with every relative link target re-expressed for <paramref name="toFolder"/>.</summary>
    /// <param name="markdown">The document text.</param>
    /// <param name="fromFolder">The repository-relative folder the text was written for.</param>
    /// <param name="toFolder">The repository-relative folder the document is moving to.</param>
    public static string Rebase(string markdown, string fromFolder, string toFolder) =>
        Rebase(markdown, fromFolder, toFolder, out _);

    /// <summary>Returns <paramref name="markdown"/> re-expressed for <paramref name="toFolder"/>, and reports how many targets changed.</summary>
    /// <param name="markdown">The document text.</param>
    /// <param name="fromFolder">The repository-relative folder the text was written for.</param>
    /// <param name="toFolder">The repository-relative folder the document is moving to.</param>
    /// <param name="rewritten">The number of link targets the move rewrote.</param>
    public static string Rebase(string markdown, string fromFolder, string toFolder, out int rewritten)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(fromFolder);
        ArgumentNullException.ThrowIfNull(toFolder);

        string from = NormalizeFolder(fromFolder);
        string to = NormalizeFolder(toFolder);
        StringBuilder output = new(markdown.Length);
        int count = 0;
        int index = SkipFrontmatter(markdown, output);
        while (index < markdown.Length)
        {
            if (AtLineStart(markdown, index) && TryFence(markdown, index, out char fence, out int fenceLength))
            {
                index = CopyFence(markdown, index, fence, fenceLength, output);
                continue;
            }

            if (markdown[index] == '`')
            {
                index = CopyCodeSpan(markdown, index, output);
                continue;
            }

            if (AtLineStart(markdown, index)
                && TryReferenceDefinition(markdown, index, from, to, ref count, out string definition, out int definitionEnd))
            {
                output.Append(definition);
                index = definitionEnd;
                continue;
            }

            if (markdown[index] == '<' && TryHtmlTag(markdown, index, from, to, ref count, out string tag, out int tagEnd))
            {
                output.Append(tag);
                index = tagEnd;
                continue;
            }

            if (markdown[index] == ']' && !IsEscaped(markdown, index)
                && index + 1 < markdown.Length && markdown[index + 1] == '('
                && HasOpeningBracket(markdown, index))
            {
                int close = MatchingParenthesis(markdown, index + 1);
                if (close > index + 1
                    && TryRebaseDestination(markdown[(index + 2)..close], from, to, ref count, out string inner))
                {
                    output.Append("](").Append(inner).Append(')');
                    index = close + 1;
                    continue;
                }
            }

            output.Append(markdown[index]);
            index++;
        }

        rewritten = count;
        return output.ToString();
    }

    /// <summary>Copies a leading YAML frontmatter block verbatim, returning the index after it, or 0 when there is none.</summary>
    private static int SkipFrontmatter(string text, StringBuilder output)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal))
            return 0;

        int firstLineEnd = LineEnd(text, 0);
        if (text[3..firstLineEnd].TrimEnd('\r').Trim().Length != 0)
            return 0;

        for (int index = IndexAfterLine(text, 0); index < text.Length; index = IndexAfterLine(text, index))
        {
            int end = LineEnd(text, index);
            if (text[index..end].TrimEnd('\r').Trim().Equals("---", StringComparison.Ordinal))
            {
                int after = IndexAfterLine(text, index);
                output.Append(text, 0, after);
                return after;
            }
        }

        // An opening fence with no closing one is not frontmatter, so the document is scanned as ordinary Markdown.
        return 0;
    }

    private static int CopyFence(string text, int start, char fence, int fenceLength, StringBuilder output)
    {
        int index = IndexAfterLine(text, start);
        output.Append(text, start, index - start);
        while (index < text.Length)
        {
            int end = IndexAfterLine(text, index);
            output.Append(text, index, end - index);
            bool closing = IsClosingFence(text, index, fence, fenceLength);
            index = end;
            if (closing)
                break;
        }

        return index;
    }

    private static int CopyCodeSpan(string text, int start, StringBuilder output)
    {
        int run = RunLength(text, start, '`');
        int close = FindBacktickRun(text, start + run, run);
        int end = close < 0 ? start + run : close + run;
        output.Append(text, start, end - start);
        return end;
    }

    private static bool TryFence(string text, int start, out char fence, out int length)
    {
        fence = '\0';
        length = 0;
        int index = SkipBlockPrefix(text, start, allowListMarker: true);
        if (index < 0 || index >= text.Length || (text[index] != '`' && text[index] != '~'))
            return false;

        char marker = text[index];
        int run = RunLength(text, index, marker);
        if (run < 3)
            return false;

        // A backtick fence's info string may not contain a backtick, or the line is inline code rather than a fence.
        if (marker == '`' && text[(index + run)..LineEnd(text, index)].Contains('`', StringComparison.Ordinal))
            return false;

        fence = marker;
        length = run;
        return true;
    }

    private static bool IsClosingFence(string text, int start, char fence, int length)
    {
        int index = SkipBlockPrefix(text, start, allowListMarker: false);
        if (index < 0 || index >= text.Length || text[index] != fence || RunLength(text, index, fence) < length)
            return false;

        for (int trailing = index + RunLength(text, index, fence); trailing < text.Length && text[trailing] != '\n'; trailing++)
            if (text[trailing] is not (' ' or '\t' or '\r'))
                return false;

        return true;
    }

    private static bool TryReferenceDefinition(
        string text, int start, string from, string to, ref int count, out string rewritten, out int end)
    {
        rewritten = string.Empty;
        end = start;
        int labelStart = SkipBlockPrefix(text, start, allowListMarker: true);
        if (labelStart < 0 || labelStart >= text.Length || text[labelStart] != '[' || IsEscaped(text, labelStart))
            return false;

        int labelEnd = labelStart + 1;
        while (labelEnd < text.Length && text[labelEnd] is not (']' or '\n'))
            labelEnd++;
        if (labelEnd >= text.Length || text[labelEnd] != ']' || IsEscaped(text, labelEnd))
            return false;
        if (labelEnd + 1 >= text.Length || text[labelEnd + 1] != ':')
            return false;

        // A footnote reference and a definition's label share the '[^...]' shape, and only the definition names a file.
        string label = text[(labelStart + 1)..labelEnd];
        if (label.Length == 0 || label[0] == '^')
            return false;

        int destination = DestinationStart(text, labelEnd + 2);
        if (destination < 0)
            return false;

        int lineEnd = LineEnd(text, destination);
        if (!TryRebaseDefinitionRest(text[destination..lineEnd], from, to, ref count, out string line))
            return false;

        rewritten = string.Concat(text[start..destination], line);
        end = lineEnd;
        return true;
    }

    /// <summary>
    /// Where a definition's destination starts: after the colon's whitespace, and after a single line break when the
    /// destination is written on the next line. Returns -1 when the definition has no destination.
    /// </summary>
    private static int DestinationStart(string text, int afterColon)
    {
        int index = afterColon;
        while (index < text.Length && text[index] is ' ' or '\t')
            index++;
        if (index < text.Length && text[index] == '\r')
            index++;
        if (index >= text.Length)
            return -1;
        if (text[index] != '\n')
            return index;

        int next = SkipBlockPrefix(text, index + 1, allowListMarker: true);
        if (next < 0 || next >= text.Length || text[next] == '\n' || text[next] == '\r')
            return -1;
        return next;
    }

    /// <summary>
    /// Re-expresses the destination of a definition and accepts the line only when what follows the destination is
    /// nothing or a quoted or parenthesised title: a line shaped <c>[label]: prose</c> is prose, not a definition.
    /// </summary>
    private static bool TryRebaseDefinitionRest(string rest, string from, string to, ref int count, out string rebuilt)
    {
        rebuilt = rest;
        int start = 0;
        while (start < rest.Length && rest[start] is ' ' or '\t')
            start++;
        if (start >= rest.Length)
            return false;

        bool angled = rest[start] == '<';
        int destinationEnd;
        string destination;
        if (angled)
        {
            int close = rest.IndexOf('>', start + 1);
            if (close < 0)
                return false;
            destination = rest[(start + 1)..close];
            destinationEnd = close + 1;
        }
        else
        {
            destinationEnd = start;
            while (destinationEnd < rest.Length && !char.IsWhiteSpace(rest[destinationEnd]))
                destinationEnd++;
            destination = rest[start..destinationEnd];
        }

        if (!IsDefinitionTail(rest[destinationEnd..]))
            return false;

        string rebased = RebaseTarget(destination, from, to);
        if (string.Equals(destination, rebased, StringComparison.Ordinal))
            return true;

        count++;
        rebuilt = string.Concat(rest[..start], angled ? $"<{rebased}>" : rebased, rest[destinationEnd..]);
        return true;
    }

    /// <summary>True when the text after a definition's destination is empty or is one quoted or parenthesised title.</summary>
    private static bool IsDefinitionTail(string tail)
    {
        string trimmed = tail.TrimEnd('\r');
        if (trimmed.Length == 0)
            return true;
        if (trimmed[0] is not (' ' or '\t'))
            return false;

        trimmed = trimmed.Trim();
        if (trimmed.Length == 0)
            return true;
        if (trimmed.Length < 2)
            return false;

        char close = trimmed[0] switch { '"' => '"', '\'' => '\'', '(' => ')', _ => '\0' };
        return close != '\0'
            && trimmed[^1] == close
            && !trimmed[1..^1].Contains(close, StringComparison.Ordinal);
    }

    /// <summary>Rewrites <c>href</c> and <c>src</c> values in one inline HTML tag, or returns false when it is not a tag.</summary>
    private static bool TryHtmlTag(string text, int start, string from, string to, ref int count, out string rewritten, out int end)
    {
        rewritten = string.Empty;
        end = start;
        if (!IsTagStart(text, start))
            return false;

        int close = -1;
        for (int cursor = start + 1; cursor < LineEnd(text, start); cursor++)
        {
            if (text[cursor] == '>')
            {
                close = cursor;
                break;
            }
        }

        if (close < 0)
            return false;

        rewritten = string.Concat("<", RewriteAttributes(text[(start + 1)..close], from, to, ref count), ">");
        end = close + 1;
        return true;
    }

    private static bool IsTagStart(string text, int start)
    {
        int index = start + 1;
        if (index < text.Length && text[index] == '/')
            index++;
        return index < text.Length && char.IsAsciiLetter(text[index]);
    }

    private static string RewriteAttributes(string inner, string from, string to, ref int count)
    {
        StringBuilder result = new(inner.Length);
        int index = 0;
        while (index < inner.Length)
        {
            char character = inner[index];
            if (character is '"' or '\'')
            {
                int close = inner.IndexOf(character, index + 1);
                if (close < 0)
                {
                    result.Append(inner, index, inner.Length - index);
                    break;
                }

                result.Append(inner, index, close - index + 1);
                index = close + 1;
                continue;
            }

            if (!char.IsAsciiLetter(character))
            {
                result.Append(character);
                index++;
                continue;
            }

            int nameEnd = index;
            while (nameEnd < inner.Length && (char.IsAsciiLetterOrDigit(inner[nameEnd]) || inner[nameEnd] is '-' or '_'))
                nameEnd++;

            if (!TryAttributeValue(inner, index, nameEnd, out int valueStart, out int valueEnd))
            {
                result.Append(inner, index, nameEnd - index);
                index = nameEnd;
                continue;
            }

            string value = inner[(valueStart + 1)..valueEnd];
            string rebased = RebaseTarget(value, from, to);
            if (!string.Equals(value, rebased, StringComparison.Ordinal))
                count++;

            result.Append(inner, index, valueStart - index + 1).Append(rebased).Append(inner[valueEnd]);
            index = valueEnd + 1;
        }

        return result.ToString();
    }

    /// <summary>The quoted value of a <c>href</c> or <c>src</c> attribute whose name ends at <paramref name="nameEnd"/>, when it is one.</summary>
    private static bool TryAttributeValue(string inner, int start, int nameEnd, out int valueStart, out int valueEnd)
    {
        valueStart = 0;
        valueEnd = 0;
        string name = inner[start..nameEnd];
        if (!name.Equals("href", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("src", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int cursor = nameEnd;
        while (cursor < inner.Length && inner[cursor] == ' ')
            cursor++;
        if (cursor >= inner.Length || inner[cursor] != '=')
            return false;

        cursor++;
        while (cursor < inner.Length && inner[cursor] == ' ')
            cursor++;
        if (cursor >= inner.Length || inner[cursor] is not ('"' or '\''))
            return false;

        int close = inner.IndexOf(inner[cursor], cursor + 1);
        if (close < 0)
            return false;

        valueStart = cursor;
        valueEnd = close;
        return true;
    }

    /// <summary>Rebuilds a link's parenthesised text with its destination re-expressed, or returns false when it has none.</summary>
    private static bool TryRebaseDestination(string inner, string from, string to, ref int count, out string rebuilt)
    {
        rebuilt = inner;
        int start = 0;
        while (start < inner.Length && char.IsWhiteSpace(inner[start]))
            start++;
        if (start >= inner.Length)
            return false;

        bool angled = inner[start] == '<';
        int destinationEnd;
        string destination;
        if (angled)
        {
            int close = inner.IndexOf('>', start + 1);
            if (close < 0)
                return false;
            destination = inner[(start + 1)..close];
            destinationEnd = close + 1;
        }
        else
        {
            destinationEnd = start;
            while (destinationEnd < inner.Length && !char.IsWhiteSpace(inner[destinationEnd]))
                destinationEnd++;
            destination = inner[start..destinationEnd];
        }

        string rebased = RebaseTarget(destination, from, to);
        if (string.Equals(destination, rebased, StringComparison.Ordinal))
            return true;

        count++;
        rebuilt = string.Concat(inner[..start], angled ? $"<{rebased}>" : rebased, inner[destinationEnd..]);
        return true;
    }

    private static string RebaseTarget(string target, string from, string to)
    {
        if (target.Length == 0 || target[0] is '#' or '/')
            return target;
        if (Scheme().IsMatch(target))
            return target;

        target = target.Replace('\\', '/');

        int suffixAt = target.IndexOfAny(['#', '?']);
        string path = suffixAt < 0 ? target : target[..suffixAt];
        string suffix = suffixAt < 0 ? string.Empty : target[suffixAt..];
        if (path.Length == 0)
            return target;

        string? resolved = Resolve(from, path);
        if (resolved is null)
            return target;

        string relative = Relative(to, resolved);
        if (path.EndsWith('/') && relative != ".")
            relative += "/";
        return relative + suffix;
    }

    /// <summary>The repository-relative path <paramref name="target"/> names from <paramref name="folder"/>, or null when it climbs above the root.</summary>
    private static string? Resolve(string folder, string target)
    {
        List<string> segments = [.. folder.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (string segment in target.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private static string Relative(string folder, string resolved)
    {
        string[] from = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] to = resolved.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int common = 0;
        while (common < from.Length && common < to.Length && string.Equals(from[common], to[common], StringComparison.Ordinal))
            common++;

        List<string> parts = [];
        for (int index = common; index < from.Length; index++)
            parts.Add("..");
        for (int index = common; index < to.Length; index++)
            parts.Add(to[index]);
        return parts.Count == 0 ? "." : string.Join('/', parts);
    }

    private static int MatchingParenthesis(string text, int open)
    {
        int depth = 0;
        char quote = '\0';
        bool angled = false;
        int lineEndings = 0;
        for (int index = open; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '\\')
            {
                index++;
                continue;
            }

            if (character == '\n' && quote == '\0' && !angled)
            {
                // A link destination cannot span paragraphs, and only the whitespace around a destination and its
                // title may hold a line ending: this is what stops an unbalanced '](' scanning to the end of a file.
                if (lineEndings >= 2 || IsBlankLineAfter(text, index))
                    return -1;
                lineEndings++;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                continue;
            }

            if (angled)
            {
                if (character == '>')
                    angled = false;
                continue;
            }

            if (character is '"' or '\'')
            {
                // A quote opens a title only where a title may start, after whitespace; elsewhere it is part of a path.
                if (index > open + 1 && char.IsWhiteSpace(text[index - 1]))
                    quote = character;
                continue;
            }

            if (character == '<' && depth == 1 && (index == open + 1 || char.IsWhiteSpace(text[index - 1])))
            {
                angled = true;
                continue;
            }

            if (character == '(')
            {
                depth++;
                continue;
            }

            if (character == ')' && --depth == 0)
                return index;
        }

        return -1;
    }

    /// <summary>True when the bracketed text before <paramref name="closeBracket"/> opens with a real link bracket.</summary>
    private static bool HasOpeningBracket(string text, int closeBracket)
    {
        for (int index = closeBracket - 1; index >= 0; index--)
        {
            char character = text[index];
            if (character == '\n')
                return false;
            if (character != '[')
                continue;
            if (IsEscaped(text, index))
                return false;

            // '[0](1)' after a name is an index into an array, not a link.
            return index == 0 || !IsIdentifierCharacter(text[index - 1]);
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    private static int FindBacktickRun(string text, int start, int length)
    {
        int index = start;
        while (index < text.Length)
        {
            if (text[index] == '\n' && IsBlankLineAfter(text, index))
                return -1;
            if (text[index] != '`')
            {
                index++;
                continue;
            }

            int run = RunLength(text, index, '`');
            if (run == length)
                return index;
            index += run;
        }

        return -1;
    }

    /// <summary>
    /// Where a line's content starts once its blockquote markers and one list marker are skipped, or -1 when the line
    /// is indented far enough to be an indented code block.
    /// </summary>
    private static int SkipBlockPrefix(string text, int start, bool allowListMarker)
    {
        int index = start;
        for (int guard = 0; guard < 8; guard++)
        {
            int lineEnd = LineEnd(text, index);
            int cursor = index;
            int spaces = 0;
            while (cursor < lineEnd && text[cursor] == ' ' && spaces < 4)
            {
                cursor++;
                spaces++;
            }

            if (spaces > 3)
                return -1;

            if (cursor < lineEnd && text[cursor] == '>')
            {
                index = cursor + 1;
                continue;
            }

            if (allowListMarker && TryListMarkerEnd(text, cursor, lineEnd, out int markerEnd))
            {
                index = markerEnd;
                continue;
            }

            return cursor;
        }

        return -1;
    }

    /// <summary>The index just past a list marker at <paramref name="start"/>, when there is one.</summary>
    private static bool TryListMarkerEnd(string text, int start, int lineEnd, out int end)
    {
        end = start;
        if (text[start] is '-' or '*' or '+')
        {
            if (start + 1 < lineEnd && text[start + 1] is not (' ' or '\t'))
                return false;
            end = start + 1;
            return true;
        }

        int digits = start;
        while (digits < lineEnd && char.IsAsciiDigit(text[digits]))
            digits++;
        if (digits == start || digits >= lineEnd || text[digits] is not ('.' or ')'))
            return false;
        if (digits + 1 >= lineEnd || text[digits + 1] is not (' ' or '\t'))
            return false;
        end = digits + 1;
        return true;
    }

    private static int RunLength(string text, int start, char character)
    {
        int end = start;
        while (end < text.Length && text[end] == character)
            end++;
        return end - start;
    }

    private static bool AtLineStart(string text, int index) => index == 0 || text[index - 1] == '\n';

    /// <summary>True when the line beginning after <paramref name="newline"/> holds no content.</summary>
    private static bool IsBlankLineAfter(string text, int newline)
    {
        for (int index = newline + 1; index < text.Length; index++)
        {
            if (text[index] is '\r' or ' ' or '\t')
                continue;
            return text[index] == '\n';
        }

        return true;
    }

    private static bool IsEscaped(string text, int index)
    {
        int backslashes = 0;
        for (int current = index - 1; current >= 0 && text[current] == '\\'; current--)
            backslashes++;
        return backslashes % 2 == 1;
    }

    private static int LineEnd(string text, int from)
    {
        int end = text.IndexOf('\n', from);
        return end < 0 ? text.Length : end;
    }

    private static int IndexAfterLine(string text, int from)
    {
        int end = text.IndexOf('\n', from);
        return end < 0 ? text.Length : end + 1;
    }

    private static string NormalizeFolder(string folder) => folder.Replace('\\', '/').Trim('/');

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*:")]
    private static partial Regex Scheme();
}
