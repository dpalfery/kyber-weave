using System.Text.RegularExpressions;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// Decides whether one hunk's changed lines hold only comments, using the comment syntax of the
/// file's language.
/// </summary>
/// <remarks>
/// Comment syntax is per language, so the table is keyed by file extension: a line that is a
/// comment in one language stays code in another (<c>*p = x</c> is a dereference in C, Go and Rust,
/// and <c>#include</c> is a directive in C). An unknown extension is never comment-only, so a review
/// lens cannot skip a change in a language the table does not describe. A block comment counts only
/// when it opens and closes inside the one hunk, because the hunk is all the measurement sees; an
/// unclosed opener might close outside it and is therefore code.
/// </remarks>
public static class CommentSyntax
{
    // A directive line reads as a comment to the table but is an instruction to a tool, so it is code.
    private static readonly Regex ShebangDirective = new(@"^#!", RegexOptions.CultureInvariant);
    private static readonly Regex PythonDirective = new(@"^(#!|#\s*(-\*-\s*)?coding[:=])", RegexOptions.CultureInvariant);
    private static readonly Regex GoDirective = new(@"^(//go:|// \+build)", RegexOptions.CultureInvariant);

    private static readonly CommentStyle CStyle = new(["//"], "/*", "*/");
    private static readonly CommentStyle GoStyle = new(["//"], "/*", "*/", GoDirective);
    private static readonly CommentStyle HashStyle = new(["#"], null, null, ShebangDirective);
    private static readonly CommentStyle PythonStyle = new(["#"], null, null, PythonDirective);
    private static readonly CommentStyle PowerShellStyle = new(["#"], "<#", "#>", ShebangDirective);
    private static readonly CommentStyle DashStyle = new(["--"], null, null);
    private static readonly CommentStyle MarkupStyle = new([], "<!--", "-->");

    private static readonly IReadOnlyDictionary<string, CommentStyle> StyleByExtension =
        new Dictionary<string, CommentStyle>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = CStyle,
            [".java"] = CStyle,
            [".kt"] = CStyle,
            [".kts"] = CStyle,
            [".swift"] = CStyle,
            [".go"] = GoStyle,
            [".rs"] = CStyle,
            [".ts"] = CStyle,
            [".tsx"] = CStyle,
            [".js"] = CStyle,
            [".jsx"] = CStyle,
            [".mjs"] = CStyle,
            [".cjs"] = CStyle,
            [".c"] = CStyle,
            [".h"] = CStyle,
            [".cc"] = CStyle,
            [".cpp"] = CStyle,
            [".cxx"] = CStyle,
            [".hpp"] = CStyle,
            [".scala"] = CStyle,
            [".dart"] = CStyle,
            [".py"] = PythonStyle,
            [".sh"] = HashStyle,
            [".bash"] = HashStyle,
            [".yml"] = HashStyle,
            [".yaml"] = HashStyle,
            [".toml"] = HashStyle,
            [".rb"] = HashStyle,
            [".ps1"] = PowerShellStyle,
            [".psm1"] = PowerShellStyle,
            [".psd1"] = PowerShellStyle,
            [".sql"] = DashStyle,
            [".lua"] = DashStyle,
            [".html"] = MarkupStyle,
            [".htm"] = MarkupStyle,
            [".xml"] = MarkupStyle,
            [".xaml"] = MarkupStyle,
            [".csproj"] = MarkupStyle,
            [".props"] = MarkupStyle,
            [".targets"] = MarkupStyle,
        };

    /// <summary>Whether the changed lines of one hunk hold only comments and blank lines in the file's language.</summary>
    /// <param name="path">The file the hunk belongs to; its extension selects the comment syntax.</param>
    /// <param name="changedLines">The hunk's added and removed line contents, without the diff's <c>+</c> and <c>-</c> prefix.</param>
    /// <returns>
    /// True only when every line is a comment or blank in a language with known syntax. False for an
    /// unknown extension, an empty hunk, or any code line. The caller decides per file: a file is
    /// comment-only only when every one of its hunks is.
    /// </returns>
    public static bool IsCommentOnly(string path, IReadOnlyList<string> changedLines)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(changedLines);

        if (changedLines.Count == 0
            || !StyleByExtension.TryGetValue(Path.GetExtension(path), out CommentStyle? style))
        {
            return false;
        }

        return IsCommentOnlyIn(style, changedLines);
    }

    private static bool IsCommentOnlyIn(CommentStyle style, IReadOnlyList<string> lines)
    {
        bool inBlock = false;
        foreach (string raw in lines)
        {
            // A byte-order mark is not whitespace to Trim, yet it opens a file that starts with a
            // comment; it is stripped first so that comment is still read as one.
            // A line can close a block and then hold more comment or code, so the remainder is
            // read again from the start rather than assumed to be blank.
            string rest = raw.TrimStart('﻿').Trim();
            while (rest.Length > 0)
            {
                if (inBlock)
                {
                    // A block is only ever opened by a style that also has a closer.
                    string? closer = style.BlockClose;
                    if (closer is null)
                    {
                        return false;
                    }

                    int close = rest.IndexOf(closer, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        break;
                    }

                    inBlock = false;
                    rest = rest[(close + closer.Length)..].TrimStart();
                    continue;
                }

                if (style.DirectivePattern is not null && style.DirectivePattern.IsMatch(rest))
                {
                    return false;
                }

                if (style.LinePrefixes.Any(prefix => rest.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    break;
                }

                if (style.BlockOpen is not null && rest.StartsWith(style.BlockOpen, StringComparison.Ordinal))
                {
                    inBlock = true;
                    rest = rest[style.BlockOpen.Length..].TrimStart();
                    continue;
                }

                // Anything else is code, including a star continuation outside a block:
                // "*p = x" is a dereference, not a comment.
                return false;
            }
        }

        return !inBlock;
    }

    /// <summary>The comment syntax of one language family.</summary>
    /// <param name="LinePrefixes">Tokens that open a comment running to the end of the line.</param>
    /// <param name="BlockOpen">The token that opens a block comment, when the language has one.</param>
    /// <param name="BlockClose">The token that closes a block comment, when the language has one.</param>
    /// <param name="DirectivePattern">
    /// Lines the compiler, interpreter or tool reads as instructions (a shebang, a Go <c>//go:</c>
    /// directive, a Python coding cookie). A match is code even though the syntax calls it a comment.
    /// </param>
    private sealed record CommentStyle(
        IReadOnlyList<string> LinePrefixes,
        string? BlockOpen,
        string? BlockClose,
        Regex? DirectivePattern = null);
}
