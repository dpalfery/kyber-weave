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
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(finding);

        // The path comes from a seat's text, so only a file inside the repository, reached without a symbolic
        // link, is read. Anything else is left as the seat wrote it.
        if (!IsRepositoryRelative(finding.File)
            || !DocsRootPath.IsContainedFile(repositoryRoot, finding.File, []))
        {
            return CitedLineResult.Unchanged(finding);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(repositoryRoot, finding.File));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CitedLineResult.Unchanged(finding);
        }

        if (finding.Line <= lines.Length)
            return CitedLineResult.Unchanged(finding);

        string? anchor = finding.Excerpt?
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
        if (anchor is null)
        {
            return CitedLineResult.Unresolved(
                finding,
                $"line {finding.Line} is beyond the end of {finding.File} ({lines.Length} lines) and the finding " +
                "has no excerpt to find it by");
        }

        int[] matches = [.. Enumerable.Range(0, lines.Length)
            .Where(index => string.Equals(lines[index].Trim(), anchor, StringComparison.Ordinal))
            .Select(index => index + 1)];

        return matches.Length == 1
            ? CitedLineResult.Relocated(finding with { Line = matches[0] }, finding.Line)
            : CitedLineResult.Unresolved(
                finding,
                $"line {finding.Line} is beyond the end of {finding.File} ({lines.Length} lines), and " +
                (matches.Length == 0
                    ? "no line holds the excerpt"
                    : $"{matches.Length} lines hold the excerpt, so which one it means cannot be told"));
    }

    private static bool IsRepositoryRelative(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Split(['/', '\\']).Contains("..", StringComparer.Ordinal);
}

/// <summary>What the cited-line check did to one finding.</summary>
/// <param name="Finding">The finding, with its line moved when the check relocated it.</param>
/// <param name="Kind">Whether the line was left, moved, or could not be repaired.</param>
/// <param name="OriginalLine">The line the seat cited.</param>
/// <param name="Reason">Why the line could not be repaired, when it could not.</param>
public sealed record CitedLineResult(ReviewFinding Finding, CitedLineKind Kind, int OriginalLine, string? Reason)
{
    /// <summary>The finding's line exists, or its file could not be checked.</summary>
    public static CitedLineResult Unchanged(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new CitedLineResult(finding, CitedLineKind.Unchanged, finding.Line, null);
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
    Unresolved
}
