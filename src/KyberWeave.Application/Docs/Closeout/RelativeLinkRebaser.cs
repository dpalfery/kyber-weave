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
/// unless the targets are re-expressed. Inline links and images, and reference definitions, are rewritten; absolute
/// URLs, root-absolute paths, fragment-only links and anything inside a code fence or an inline code span are left
/// exactly as written.
/// </para>
/// <para>
/// Folders are repository-relative and forward-slashed. A target that climbs above the repository root cannot be
/// re-expressed and is left as written.
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
        int index = 0;
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

            if (markdown[index] == ']' && !IsEscaped(markdown, index)
                && index + 1 < markdown.Length && markdown[index + 1] == '(')
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
        int index = start;
        int spaces = 0;
        while (index < text.Length && text[index] == ' ' && spaces < 4)
        {
            index++;
            spaces++;
        }

        if (spaces > 3 || index >= text.Length || (text[index] != '`' && text[index] != '~'))
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
        int index = start;
        int spaces = 0;
        while (index < text.Length && text[index] == ' ' && spaces < 4)
        {
            index++;
            spaces++;
        }

        if (spaces > 3 || index >= text.Length || text[index] != fence || RunLength(text, index, fence) < length)
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
        int index = start;
        int spaces = 0;
        while (index < text.Length && text[index] == ' ' && spaces < 4)
        {
            index++;
            spaces++;
        }

        if (spaces > 3 || index >= text.Length || text[index] != '[' || IsEscaped(text, index))
            return false;

        int labelEnd = index + 1;
        while (labelEnd < text.Length && text[labelEnd] is not (']' or '\n'))
            labelEnd++;
        if (labelEnd >= text.Length || text[labelEnd] != ']' || IsEscaped(text, labelEnd))
            return false;
        if (labelEnd + 1 >= text.Length || text[labelEnd + 1] != ':')
            return false;

        int destination = labelEnd + 2;
        while (destination < text.Length && text[destination] is ' ' or '\t')
            destination++;
        int lineEnd = LineEnd(text, destination);
        if (!TryRebaseDestination(text[destination..lineEnd], from, to, ref count, out string line))
            return false;

        rewritten = string.Concat(text[start..destination], line);
        end = lineEnd;
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
        for (int index = open; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '\\')
            {
                index++;
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

    private static int FindBacktickRun(string text, int start, int length)
    {
        int index = start;
        while (index < text.Length)
        {
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

    private static int RunLength(string text, int start, char character)
    {
        int end = start;
        while (end < text.Length && text[end] == character)
            end++;
        return end - start;
    }

    private static bool AtLineStart(string text, int index) => index == 0 || text[index - 1] == '\n';

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
