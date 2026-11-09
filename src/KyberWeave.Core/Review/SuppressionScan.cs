using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Review;

/// <summary>A warning suppression a change adds without the reason the review requires.</summary>
/// <param name="Path">Repository-relative path of the file, forward-slashed.</param>
/// <param name="Line">One-based line number in the file as the change leaves it.</param>
/// <param name="Form">The suppression form recognised, in words, so the finding says what it found.</param>
/// <param name="Excerpt">The added line, trimmed, so the finding quotes the line it is about.</param>
public sealed record SuppressionFinding(string Path, int Line, string Form, string Excerpt);

/// <summary>What one scan found, and why it could not run when it could not.</summary>
/// <param name="Findings">Suppressions added without a reason, ordered by path and then by line.</param>
/// <param name="Failure">
/// Why the change could not be read, or <see langword="null"/> when it was read in full. A failure is not the
/// same fact as a change that adds no suppression: the caller reports the first and passes the second.
/// </param>
public sealed record SuppressionScanResult(IReadOnlyList<SuppressionFinding> Findings, string? Failure)
{
    /// <summary>Whether the scan read the whole change.</summary>
    public bool Succeeded => Failure is null;
}

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

    private const int MaxDiffBytes = 10 * 1024 * 1024;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Finds every suppression the change under <paramref name="workingDirectory"/> adds without a reason.</summary>
    /// <remarks>
    /// A path the scan cannot read — a binary file, or a file that vanished between listing and reading — adds no
    /// finding: no source suppression can live in a binary, and the alternative is failing the gate on a race
    /// rather than on the change. A git invocation that fails is different, and is returned as
    /// <see cref="SuppressionScanResult.Failure"/>, because then the scan did not see the change at all.
    /// </remarks>
    /// <param name="workingDirectory">The repository root the diff is taken in.</param>
    /// <param name="baseRef">The ref the working tree is diffed against.</param>
    /// <returns>The findings, ordered by path and then by line, and why the scan could not run when it could not.</returns>
    public static SuppressionScanResult Scan(string workingDirectory, string baseRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string root;
        try
        {
            root = Path.GetFullPath(workingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            return new SuppressionScanResult([], $"'{workingDirectory}' is not a usable path: {ex.Message}");
        }

        if (!Directory.Exists(root))
            return new SuppressionScanResult([], $"'{root}' is not a directory");

        if (ChangedFiles.ValidateBase(baseRef) is { } baseProblem)
            return new SuppressionScanResult([], baseProblem);

        (bool diffRead, string diff) = RunGit(
            root,
            MaxDiffBytes,
            "diff", "--no-color", "--no-ext-diff", "-U0", "-M", "--relative", "--diff-filter=ACMRT", baseRef, "--");
        if (!diffRead)
        {
            if (diff.Contains("exceeded the limit", StringComparison.OrdinalIgnoreCase))
                return new SuppressionScanResult([], $"the diff against '{baseRef}' exceeded the 10 MB scan budget");

            return new SuppressionScanResult([], $"the change against '{baseRef}' could not be read: {diff}");
        }

        SortedDictionary<string, List<AddedLine>> added = ParseAddedLines(diff);

        (bool untrackedRead, string untracked) = RunGit(
            root, "ls-files", "-z", "--others", "--exclude-standard", "--");
        if (!untrackedRead)
            return new SuppressionScanResult([], $"the untracked files could not be listed: {untracked}");

        string[] untrackedPaths = untracked
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (string path in untrackedPaths)
        {
            if (added.ContainsKey(path))
                continue;

            if (ReadAddedFile(Path.Combine(root, path)) is { } lines)
                added[path] = lines;
        }

        return new SuppressionScanResult(Collect(added), null);
    }

    /// <summary>
    /// Turns a unified diff into the added lines of each file, keyed by path. Only a line inside a hunk counts:
    /// a header, a binary patch, and a trailing marker carry no source line to judge.
    /// </summary>
    private static SortedDictionary<string, List<AddedLine>> ParseAddedLines(string diff)
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

    /// <summary>The added lines of a file the change leaves untracked, whose every line is added, or null when it
    /// cannot be read as text.</summary>
    private static List<AddedLine>? ReadAddedFile(string fullPath)
    {
        const int maxBytes = 10 * 1024 * 1024;
        byte[] buffer = new byte[maxBytes + 1];
        int totalRead = 0;
        try
        {
            if (!File.Exists(fullPath))
                return null;

            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maxBytes)
                return null;

            while (totalRead <= maxBytes)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                    break;
                totalRead += read;
            }

            if (totalRead > maxBytes)
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }

        if (Array.IndexOf(buffer, (byte)0, 0, totalRead) >= 0)
            return null;

        List<AddedLine> lines = [];
        int number = 0;
        string text = Encoding.UTF8.GetString(buffer, 0, totalRead);
        foreach (ReadOnlySpan<char> raw in text.AsSpan().EnumerateLines())
        {
            number++;
            lines.Add(new AddedLine(number, raw.TrimEnd('\r').Trim().ToString()));
        }

        return lines;
    }

    private static void AddLine(SortedDictionary<string, List<AddedLine>> added, string path, AddedLine line)
    {
        if (!added.TryGetValue(path, out List<AddedLine>? lines))
        {
            lines = [];
            added[path] = lines;
        }

        lines.Add(line);
    }

    /// <summary>Reports each added suppression that states no reason, in the order the files and lines arrived.</summary>
    private static IReadOnlyList<SuppressionFinding> Collect(SortedDictionary<string, List<AddedLine>> added)
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

    /// <summary>
    /// Runs git with argv, never a shell, and returns its output or the reason it produced none. A missing git, a
    /// timeout, and a non-zero exit are all the same fact to the caller: the change was not read.
    /// </summary>
    private static (bool Succeeded, string Output) RunGit(string workingDirectory, params string[] arguments) =>
        RunGit(workingDirectory, null, arguments);

    private static (bool Succeeded, string Output) RunGit(
        string workingDirectory,
        int? maxOutputCharacters,
        params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout, maxOutputCharacters);
            return result.ExitCode == 0
                ? (true, result.StandardOutput)
                : (false, $"git {string.Join(' ', arguments)} exited with code {result.ExitCode}: " +
                          result.StandardError.Trim());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException or IOException)
        {
            return (false, ex.Message.Trim());
        }
    }

    /// <summary>One added line, with the number it carries in the file as the change leaves it.</summary>
    private sealed record AddedLine(int Number, string Text);
}
