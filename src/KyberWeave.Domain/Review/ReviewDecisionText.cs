using System.Text;

namespace KyberWeave.Core.Review;

/// <summary>How a decision-model answer and a finding are written into a report line and a question.</summary>
public static class ReviewDecisionText
{
    /// <summary>Formats a score for a report line, as two decimals.</summary>
    public static string Format(double? score) =>
        score is double value ? value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "n/a";

    /// <summary>The text of a finding a question about its substance reads: the claim, then the failure scenario.</summary>
    public static string FindingText(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        StringBuilder text = new();
        text.Append(finding.File).Append(':').Append(finding.Line).Append(" — ").Append(ReviewSeatInput.Clip(finding.Claim) ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(finding.FailureScenario))
            text.Append("\nFailure scenario: ").Append(ReviewSeatInput.Clip(finding.FailureScenario));
        return text.ToString();
    }
}
