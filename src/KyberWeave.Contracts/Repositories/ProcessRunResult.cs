namespace KyberWeave.Contracts.Repositories;

/// <summary>How a child process ended.</summary>
/// <param name="ExitCode">The exit code; meaningless when <paramref name="TimedOut"/> is true.</param>
/// <param name="Output">Standard output followed by standard error.</param>
/// <param name="TimedOut">Whether the process was killed for running past its timeout.</param>
public sealed record ProcessRunResult(int ExitCode, string Output, bool TimedOut);
