using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

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
