using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

/// <summary>Checks that the line a finding cites exists in its file, and repairs it from the excerpt when it does not.</summary>
/// <remarks>
/// <para>
/// Seats cite lines from a diff or from memory, and the line is sometimes beyond the end of the file — the
/// recorded transcripts cite line 6492 of a file a few hundred lines long. A finding anchored nowhere cannot be
/// checked by the person fixing it, so it is moved to the one line that holds its excerpt, or dropped when no
/// single line does. Guessing between several matching lines would anchor the finding to code it may not be about.
/// </para>
/// <para>
/// A line inside the file is never moved, even when the excerpt does not match it: whether the seat read the
/// right code is the reviewer's judgement, and this check repairs only an anchor that cannot exist.
/// </para>
/// </remarks>
public static class CitedLineCheck
{
    /// <summary>Checks one finding against the working tree.</summary>
    /// <param name="repositoryRoot">The absolute repository root the finding's path is relative to.</param>
    /// <param name="finding">The finding to check.</param>
    public static CitedLineResult Check(string repositoryRoot, ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return Check(repositoryRoot, [finding])[0];
    }

    /// <summary>Checks every finding against the working tree, reading each cited file once.</summary>
    /// <param name="repositoryRoot">The absolute repository root the findings' paths are relative to.</param>
    /// <param name="findings">The findings to check.</param>
    /// <returns>One result per finding, in the order given.</returns>
    public static IReadOnlyList<CitedLineResult> Check(string repositoryRoot, IReadOnlyList<ReviewFinding> findings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(findings);

        Dictionary<string, FileLines> files = new(StringComparer.Ordinal);
        List<CitedLineResult> results = new(findings.Count);
        foreach (ReviewFinding finding in findings)
        {
            if (!files.TryGetValue(finding.File, out FileLines? file))
            {
                file = ReadFile(repositoryRoot, finding.File);
                files[finding.File] = file;
            }

            results.Add(file.Lines is { } lines
                ? CheckLine(finding, lines)
                : CitedLineResult.Unverified(finding, file.Problem ?? "the cited file could not be read"));
        }

        return results;
    }

    /// <summary>
    /// The cited file's lines, or why they were not read. The path comes from a seat's text, so only a file inside
    /// the repository, reached without a symbolic link, is read.
    /// </summary>
    private static FileLines ReadFile(string repositoryRoot, string path)
    {
        if (!IsRepositoryRelative(path))
            return new FileLines(null, $"{path} is not a path inside the repository, so it was not read");

        try
        {
            if (!DocsRootPath.IsContainedFile(repositoryRoot, path, []))
                return new FileLines(null, $"{path} is not a file in the repository, or is reached through a link");

            return new FileLines(File.ReadAllLines(Path.Combine(repositoryRoot, path)), null);
        }
        // ArgumentException and NotSupportedException: a seat can write a path holding a character no file
        // system accepts, such as a NUL from a quoted "\0".
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            // The exception message can carry the absolute path, which does not belong in a review report.
            return new FileLines(null, $"{path} could not be read ({ex.GetType().Name})");
        }
    }

    private static CitedLineResult CheckLine(ReviewFinding finding, string[] lines)
    {
        // Line 0 or a negative line can no more exist than one past the end, so it is repaired the same way.
        if (finding.Line >= 1 && finding.Line <= lines.Length)
            return CitedLineResult.Unchanged(finding);

        string? anchor = finding.Excerpt?
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
        if (anchor is null)
        {
            return CitedLineResult.Unresolved(
                finding,
                $"line {finding.Line} is not a line of {finding.File} ({lines.Length} lines) and the finding " +
                "has no excerpt to find it by");
        }

        int[] matches = [.. Enumerable.Range(0, lines.Length)
            .Where(index => string.Equals(lines[index].Trim(), anchor, StringComparison.Ordinal))
            .Select(index => index + 1)];

        return matches.Length == 1
            ? CitedLineResult.Relocated(finding with { Line = matches[0] }, finding.Line)
            : CitedLineResult.Unresolved(
                finding,
                $"line {finding.Line} is not a line of {finding.File} ({lines.Length} lines), and " +
                (matches.Length == 0
                    ? "no line holds the excerpt"
                    : $"{matches.Length} lines hold the excerpt, so which one it means cannot be told"));
    }

    private sealed record FileLines(string[]? Lines, string? Problem);

    private static bool IsRepositoryRelative(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Split(['/', '\\']).Contains("..", StringComparer.Ordinal);
}

/// <summary>What the cited-line check did to one finding.</summary>
/// <param name="Finding">The finding, with its line moved when the check relocated it.</param>
/// <param name="Kind">Whether the line was left, moved, or could not be repaired.</param>
/// <param name="OriginalLine">The line the seat cited.</param>
/// <param name="Reason">Why the line could not be repaired or checked, when it could not.</param>
public sealed record CitedLineResult(ReviewFinding Finding, CitedLineKind Kind, int OriginalLine, string? Reason)
{
    /// <summary>The finding's line exists in its file.</summary>
    public static CitedLineResult Unchanged(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new CitedLineResult(finding, CitedLineKind.Unchanged, finding.Line, null);
    }

    /// <summary>The finding's file could not be read, so its line was not checked.</summary>
    public static CitedLineResult Unverified(ReviewFinding finding, string reason)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new CitedLineResult(finding, CitedLineKind.Unverified, finding.Line, reason);
    }

    /// <summary>The finding was moved to the one line holding its excerpt.</summary>
    public static CitedLineResult Relocated(ReviewFinding finding, int originalLine)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new CitedLineResult(finding, CitedLineKind.Relocated, originalLine, null);
    }

    /// <summary>The finding's line does not exist and no single line holds its excerpt.</summary>
    public static CitedLineResult Unresolved(ReviewFinding finding, string reason)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new CitedLineResult(finding, CitedLineKind.Unresolved, finding.Line, reason);
    }
}

/// <summary>How the cited-line check ended for one finding.</summary>
public enum CitedLineKind
{
    /// <summary>The line was left where the seat put it.</summary>
    Unchanged,

    /// <summary>The line was moved to the one line holding the excerpt.</summary>
    Relocated,

    /// <summary>The line does not exist and could not be repaired, so the finding is dropped.</summary>
    Unresolved,

    /// <summary>
    /// The cited file is missing, outside the repository, or unreadable, so the line was not checked and the
    /// finding is left as the seat wrote it.
    /// </summary>
    Unverified
}
