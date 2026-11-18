using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Review;

/// <summary>
/// Requires every warning suppression a change adds to state a reason, judging added lines and never history.
/// </summary>
/// <remarks>
/// <para>
/// Only the lines a change adds are in scope. This repository already carries suppressions it decided long ago,
/// each under a rule that asks for a reason rather than forbidding the suppression, and re-litigating them on
/// every run would fail every change that touches those files. The scan therefore reads a diff — plus the files
/// the change leaves untracked, whose every line is added — and nothing else.
/// </para>
/// <para>
/// The three forms recognised are the ones decision D28 names: the C# pragma that disables a compiler warning, the
/// SuppressMessage attribute, and the NoWarn element of a project file. No recogniser existed in the tree, so this
/// is the smallest one the rule needs, and each form is matched where it is written rather than by phrase, so
/// ordinary prose about a suppression is not mistaken for one.
/// </para>
/// <para>
/// A reason is the token <c>reason:</c> on the suppression's own line or on the added line immediately above it,
/// which is where a source comment has room to say why. Both orders are accepted because a trailing comment runs
/// out of width on a long attribute, while a leading comment has no syntax of its own in a project file.
/// </para>
/// <para>
/// The scan is bounded and defensive on purpose: it reads a diff, never the corpus, and untrusted input reaches
/// it. A malformed hunk, a binary file, a path that vanished between listing and reading, or a change that adds
/// nothing at all yields no finding and no exception.
/// </para>
/// </remarks>
public static partial class SuppressionScan
{
    /// <summary>The token a suppression states its reason with, in either accepted position.</summary>
    public const string ReasonToken = "reason:";

    /// <summary>The ref the working tree is compared with when the caller names none.</summary>
    public const string DefaultBase = "HEAD";

    /// <summary>The most bytes of a diff or of an untracked file the scan reads.</summary>
    public const int MaxDiffBytes = 10 * 1024 * 1024;

    /// <summary>Turns a unified diff into the added lines of each file.</summary>
    /// <param name="diff">The diff text.</param>
    public static SuppressionCandidates FromDiff(string diff) => new(ParseAddedLines(diff));

    /// <summary>
    /// Turns a unified diff into the added lines of each file, keyed by path. Only a line inside a hunk counts:
    /// a header, a binary patch, and a trailing marker carry no source line to judge.
    /// </summary>
    internal static SortedDictionary<string, List<AddedLine>> ParseAddedLines(string diff)
    {
        SortedDictionary<string, List<AddedLine>> added = new(StringComparer.Ordinal);
        string? path = null;
        bool inHunk = false;
        int lineNumber = 0;

        foreach (ReadOnlySpan<char> raw in diff.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> line = raw.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunk = false;
                path = null;
                continue;
            }

            if (inHunk)
            {
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    inHunk = TryParseFirstAddedLine(line.ToString(), out lineNumber);
                    continue;
                }

                if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    if (path is { } file)
                        AddLine(added, file, new AddedLine(lineNumber, line[1..].Trim().ToString()));

                    lineNumber++;
                }
                else if (line.StartsWith(" ", StringComparison.Ordinal))
                {
                    lineNumber++;
                }

                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                path = DiffPath(line[4..].ToString());
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
                inHunk = path is not null && TryParseFirstAddedLine(line.ToString(), out lineNumber);
        }

        return added;
    }

    /// <summary>
    /// Reads the first line number of the added side of a hunk header such as <c>@@ -1,2 +3,4 @@</c>. A hunk that
    /// adds no line (<c>+0,0</c>) or a header that does not parse is not a hunk as far as this scan is concerned,
    /// which is what keeps a malformed diff from being read as a run of findings.
    /// </summary>
    private static bool TryParseFirstAddedLine(string header, out int firstLine)
    {
        firstLine = 0;

        int plus = header.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
            return false;

        int end = header.IndexOf(' ', plus + 1);
        ReadOnlySpan<char> range = end < 0 ? header.AsSpan(plus + 1) : header.AsSpan(plus + 1, end - plus - 1);
        int comma = range.IndexOf(',');
        ReadOnlySpan<char> start = comma < 0 ? range : range[..comma];

        return int.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out firstLine) && firstLine > 0;
    }

    /// <summary>
    /// The repository-relative path of a <c>+++</c> header. A quoted path is unquoted but not unescaped: the path
    /// is reported, never opened, so an escape left in place is cosmetic while unquoting wrongly is not.
    /// </summary>
    private static string? DiffPath(string header)
    {
        string path = header.TrimEnd();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path[1..^1];

        if (string.Equals(path, "/dev/null", StringComparison.Ordinal))
            return null;

        return path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
    }

    /// <summary>The added lines of an untracked file's bytes, whose every line is added, or null when they are not text.</summary>
    internal static List<AddedLine>? LinesOfUntracked(byte[] content)
    {
        if (Array.IndexOf(content, (byte)0, 0, content.Length) >= 0)
            return null;

        List<AddedLine> lines = [];
        int number = 0;
        string text = Encoding.UTF8.GetString(content, 0, content.Length);
        foreach (ReadOnlySpan<char> raw in text.AsSpan().EnumerateLines())
        {
            number++;
            lines.Add(new AddedLine(number, raw.TrimEnd('\r').Trim().ToString()));
        }

        return lines;
    }

    internal static void AddLine(SortedDictionary<string, List<AddedLine>> added, string path, AddedLine line)
    {
        if (!added.TryGetValue(path, out List<AddedLine>? lines))
        {
            lines = [];
            added[path] = lines;
        }

        lines.Add(line);
    }

    /// <summary>Reports each added suppression that states no reason, in the order the files and lines arrived.</summary>
    internal static IReadOnlyList<SuppressionFinding> Collect(SortedDictionary<string, List<AddedLine>> added)
    {
        List<SuppressionFinding> findings = [];
        foreach (KeyValuePair<string, List<AddedLine>> file in added)
        {
            List<AddedLine> lines = file.Value;
            for (int i = 0; i < lines.Count; i++)
            {
                AddedLine line = lines[i];
                if (Recognise(line.Text) is not { } form || CarriesReason(line.Text))
                    continue;

                bool reasonAbove = i > 0
                                   && lines[i - 1].Number == line.Number - 1
                                   && CarriesReason(lines[i - 1].Text);
                if (reasonAbove)
                    continue;

                findings.Add(new SuppressionFinding(file.Key, line.Number, form, line.Text));
            }
        }

        return findings;
    }

    /// <summary>
    /// The suppression form an added line writes, in words, or null when the line suppresses nothing. Each form is
    /// matched where it is written, so a comment or a document that mentions one of them is not a suppression.
    /// </summary>
    private static string? Recognise(string text)
    {
        if (DisablesWarning().IsMatch(text))
            return "warning-disable pragma";

        if (SuppressesMessage().IsMatch(text))
            return "SuppressMessage attribute";

        if (AddsNoWarn().IsMatch(text))
            return "NoWarn element";

        return null;
    }

    private static bool CarriesReason(string text) =>
        ReasonPattern().IsMatch(text);

    [GeneratedRegex(@"\breason\s*:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReasonPattern();

    /// <summary>The C# pragma that disables a compiler warning.</summary>
    [GeneratedRegex(@"#\s*pragma\s+warning\s+disable\b", RegexOptions.CultureInvariant)]
    private static partial Regex DisablesWarning();

    /// <summary>The SuppressMessage attribute, optionally under its fully qualified name.</summary>
    [GeneratedRegex(@"\[\s*(?:global\s*::\s*)?(?:System\s*\.\s*Diagnostics\s*\.\s*CodeAnalysis\s*\.\s*)?SuppressMessage\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex SuppressesMessage();

    /// <summary>The MSBuild element that adds a warning id to a project's NoWarn list.</summary>
    [GeneratedRegex(@"<\s*NoWarn\b", RegexOptions.CultureInvariant)]
    private static partial Regex AddsNoWarn();

    /// <summary>One added line, with the number it carries in the file as the change leaves it.</summary>
    internal sealed record AddedLine(int Number, string Text);
}

/// <summary>The lines a change adds, by path, waiting to be judged.</summary>
public sealed class SuppressionCandidates
{
    private readonly SortedDictionary<string, List<SuppressionScan.AddedLine>> _added;

    internal SuppressionCandidates(SortedDictionary<string, List<SuppressionScan.AddedLine>> added) => _added = added;

    /// <summary>Whether the diff already added lines to <paramref name="path"/>.</summary>
    public bool Has(string path) => _added.ContainsKey(path);

    /// <summary>Adds the lines of an untracked file; false when the content is binary, so it adds none.</summary>
    /// <param name="path">The repository-relative path.</param>
    /// <param name="content">The file's bytes.</param>
    public bool AddUntracked(string path, byte[] content)
    {
        if (SuppressionScan.LinesOfUntracked(content) is not { } lines)
            return false;

        _added[path] = lines;
        return true;
    }

    /// <summary>Reports each added suppression that states no reason, in the order the files and lines arrived.</summary>
    public IReadOnlyList<SuppressionFinding> Findings() => SuppressionScan.Collect(_added);
}
