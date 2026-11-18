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
    /// <summary>Judges one finding against the lines of its cited file.</summary>
    /// <param name="finding">The finding.</param>
    /// <param name="lines">The cited file's lines.</param>
    public static CitedLineResult Judge(ReviewFinding finding, string[] lines)
    {
        // Line 0 or a negative line can no more exist than one past the end, so it is repaired the same way.
        if (finding.Line >= 1 && finding.Line <= lines.Length)
            return CitedLineResult.Unchanged(finding);

        string? anchor = finding.Excerpt is { } excerpt && ReviewSeatInput.ExcerptWithinLimits(excerpt)
            ? excerpt.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0)
            : null;
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

    /// <summary>Whether a seat-written path names a place inside the repository.</summary>
    /// <param name="path">The path.</param>
    public static bool IsRepositoryRelative(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Split(['/', '\\']).Contains("..", StringComparer.Ordinal);
}
