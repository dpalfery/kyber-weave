using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

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
