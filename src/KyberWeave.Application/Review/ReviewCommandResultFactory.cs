using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Review;

/// <summary>
/// Builds the <see cref="ReviewCommandResult"/> a review use case hands back, so the record itself stays data.
/// </summary>
public static class ReviewCommandResultFactory
{
    /// <summary>A refused input.</summary>
    /// <param name="message">What is wrong with it.</param>
    public static ReviewCommandResult Refused(string message) =>
        new(ReviewCommandResult.ClientInputExitCode, ClientError: message);

    /// <summary>A run that ends by rendering <paramref name="report"/>.</summary>
    /// <param name="exitCode">The exit code.</param>
    /// <param name="report">The report.</param>
    /// <param name="epilogue">Lines printed after the report is rendered.</param>
    public static ReviewCommandResult Reported(int exitCode, DiagnosticReport report, IReadOnlyList<string>? epilogue = null) =>
        new(exitCode, report, epilogue ?? []);
}
