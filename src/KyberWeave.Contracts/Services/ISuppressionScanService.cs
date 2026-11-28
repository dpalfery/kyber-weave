using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>Finds the warning suppressions a change adds without the reason the review requires.</summary>
public interface ISuppressionScanService
{
    /// <summary>Finds every suppression the change under <paramref name="workingDirectory"/> adds without a reason.</summary>
    /// <param name="workingDirectory">The repository root the diff is taken in.</param>
    /// <param name="baseRef">The ref the working tree is diffed against.</param>
    /// <returns>The findings, and why the scan could not run when it could not.</returns>
    SuppressionScanResult Scan(string workingDirectory, string baseRef);

    /// <summary>Finds every suppression the change under <paramref name="workingDirectory"/> adds without a reason, without blocking a thread.</summary>
    /// <param name="workingDirectory">The repository root the diff is taken in.</param>
    /// <param name="baseRef">The ref the working tree is diffed against.</param>
    /// <param name="cancellationToken">Cancels the git reads.</param>
    /// <returns>The findings, and why the scan could not run when it could not.</returns>
    Task<SuppressionScanResult> ScanAsync(
        string workingDirectory,
        string baseRef,
        CancellationToken cancellationToken = default);
}
