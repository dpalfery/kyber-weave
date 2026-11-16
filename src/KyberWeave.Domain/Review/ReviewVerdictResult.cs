using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Review;

/// <summary>What one <c>review verdict</c> run produced.</summary>
/// <param name="Report">Everything the run found, for the caller to render.</param>
/// <param name="ExitCode">The exit code the command returns.</param>
/// <param name="NextStep">The line a candidate run prints after the report; null when there is none.</param>
public sealed record ReviewVerdictResult(DiagnosticReport Report, int ExitCode, string? NextStep);
