using System.Text.RegularExpressions;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Analysis.Claims;

/// <summary>
/// Validates the deliberately small ignore language and replaces only its tags with
/// spaces. Keeping character and newline positions stable lets Markdig remain the source
/// of structural claim boundaries without changing the retrieval body.
/// </summary>
internal static partial class IgnoreMarkupReader
{
    internal const string DiagnosticCode = "KW-DOC-ANALYSIS-004";

    internal static IgnoreMarkupReadResult Read(DocumentModel document)
    {
        string body = NormalizeLineEndings(document.Body);
        string prefix = FrontmatterPrefix(document);
        if (ContainsTagLikeText(prefix))
        {
            return Error(document, "Ignore markup is not allowed in YAML frontmatter.");
        }

        char[] characters = body.ToCharArray();
        List<IgnoreInterval> intervals = new List<IgnoreInterval>();
        IgnoreRule activeRule = IgnoreRule.None;
        int activeLine = 0;
        FenceState fence = new FenceState();
        int lineStart = 0;
        int lineNumber = 1;

        while (lineStart <= body.Length)
        {
            int newline = body.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? body.Length : newline;
            string line = body[lineStart..lineEnd];

            if (UpdateFence(line, fence))
            {
                goto NextLine;
            }

            if (fence.IsOpen)
            {
                goto NextLine;
            }

            if (activeRule != IgnoreRule.None && LevelTwoHeadingPattern().IsMatch(line))
            {
                return Error(document, "Ignore markup cannot cross a level-two section boundary.");
            }

            MatchCollection matches = ExactTagPattern().Matches(line);
            string residue = line;
            foreach (Match match in matches.Reverse())
            {
                residue = residue.Remove(match.Index, match.Length);
            }

            if (TagLikePattern().IsMatch(residue))
            {
                return Error(document, "Ignore markup is malformed or uses an unknown, case-changed rule.");
            }

            foreach (Match match in matches)
            {
                Array.Fill(characters, ' ', lineStart + match.Index, match.Length);
                if (match.Value == "</kyber-ignore>")
                {
                    if (activeRule == IgnoreRule.None)
                    {
                        return Error(document, "Ignore markup has a closing tag without an opening tag.");
                    }

                    intervals.Add(new IgnoreInterval(activeLine, lineNumber, activeRule));
                    activeRule = IgnoreRule.None;
                    activeLine = 0;
                    continue;
                }

                if (activeRule != IgnoreRule.None)
                {
                    return Error(document, "Ignore markup cannot be nested.");
                }

                activeRule = ParseRule(match.Groups["rule"].Value);
                activeLine = lineNumber;
            }

        NextLine:
            if (newline < 0) break;
            lineStart = newline + 1;
            lineNumber++;
        }

        if (activeRule != IgnoreRule.None)
        {
            return Error(document, "Ignore markup has an opening tag without a closing tag.");
        }

        return new IgnoreMarkupReadResult(new string(characters), intervals, null);
    }

    private static string FrontmatterPrefix(DocumentModel document)
    {
        if (!document.HasFrontmatter || document.BodyStartLine <= 1 || document.RawMarkdown.Length == 0)
        {
            return string.Empty;
        }

        string raw = NormalizeLineEndings(document.RawMarkdown);
        int line = 1;
        int index = 0;
        while (index < raw.Length && line < document.BodyStartLine)
        {
            if (raw[index++] == '\n') line++;
        }

        return raw[..index];
    }

    private static bool ContainsTagLikeText(string text) => TagLikePattern().IsMatch(text);

    private static bool UpdateFence(string line, FenceState fence)
    {
        Match match = FencePattern().Match(line);
        if (!match.Success) return false;

        string marker = match.Groups["marker"].Value;
        if (!fence.IsOpen)
        {
            fence.Character = marker[0];
            fence.Length = marker.Length;
            return true;
        }

        if (marker[0] == fence.Character && marker.Length >= fence.Length &&
            line[(match.Index + match.Length)..].Trim().Length == 0)
        {
            fence.Character = '\0';
            fence.Length = 0;
        }

        return true;
    }

    private static IgnoreRule ParseRule(string rule) => rule switch
    {
        "duplicate" => IgnoreRule.Duplicate,
        "conflict" => IgnoreRule.Conflict,
        "terminology" => IgnoreRule.Terminology,
        "all" => IgnoreRule.All,
        _ => IgnoreRule.None
    };

    private static IgnoreMarkupReadResult Error(DocumentModel document, string message) =>
        new(
            string.Empty,
            [],
            new Diagnostic(
                DiagnosticCode,
                Severity.Error,
                message,
                document.Subject,
                document.FilePath,
                "Use balanced, non-nested <kyber-ignore rule=\"duplicate|conflict|terminology|all\"> tags inside one ## section, outside frontmatter and code fences."));

    private static string NormalizeLineEndings(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    [GeneratedRegex("^ {0,3}(?<marker>`{3,}|~{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex FencePattern();

    [GeneratedRegex("^ {0,3}##(?:[ \\t]+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LevelTwoHeadingPattern();

    [GeneratedRegex("<kyber-ignore rule=\"(?<rule>duplicate|conflict|terminology|all)\">|</kyber-ignore>", RegexOptions.CultureInvariant)]
    private static partial Regex ExactTagPattern();

    [GeneratedRegex("<[^\\r\\n>]*kyber-ignore[^\\r\\n]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagLikePattern();

    private sealed class FenceState
    {
        internal char Character { get; set; }
        internal int Length { get; set; }
        internal bool IsOpen => Length > 0;
    }
}

internal sealed record IgnoreMarkupReadResult(
    string SanitizedBody,
    IReadOnlyList<IgnoreInterval> Intervals,
    Diagnostic? Diagnostic);

internal sealed record IgnoreInterval(int StartLine, int EndLine, IgnoreRule Rule);
