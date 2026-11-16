using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>Checks that the line a finding cites exists in its file, and repairs it from the excerpt when it does not.</summary>
public interface ICitedLineChecker
{
    /// <summary>Checks one finding against the working tree.</summary>
    /// <param name="repositoryRoot">The absolute repository root the finding's path is relative to.</param>
    /// <param name="finding">The finding to check.</param>
    CitedLineResult Check(string repositoryRoot, ReviewFinding finding);

    /// <summary>Checks every finding against the working tree, reading each cited file once.</summary>
    /// <param name="repositoryRoot">The absolute repository root the findings' paths are relative to.</param>
    /// <param name="findings">The findings to check.</param>
    /// <returns>One result per finding, in the order given.</returns>
    IReadOnlyList<CitedLineResult> Check(string repositoryRoot, IReadOnlyList<ReviewFinding> findings);
}
