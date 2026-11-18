using KyberWeave.Core.Review;

namespace KyberWeave.Core.Configuration;

/// <summary>The decisions <see cref="ReviewPolicy"/> itself no longer makes, so the record stays data.</summary>
public static class ReviewPolicyRules
{
    /// <summary>Whether a fix round takes <paramref name="finding"/>: its severity is at or above the policy's fix level.</summary>
    /// <param name="policy">The policy whose <see cref="ReviewPolicy.FixLevel"/> decides.</param>
    /// <param name="finding">The finding the round would fix.</param>
    public static bool FixRoundTakes(this ReviewPolicy policy, ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Severity >= policy.FixLevel switch
        {
            ReviewFixLevel.Critical => ReviewSeverity.Critical,
            ReviewFixLevel.Major => ReviewSeverity.Major,
            ReviewFixLevel.Minor => ReviewSeverity.Minor,
            _ => ReviewSeverity.Nitpick
        };
    }
}
