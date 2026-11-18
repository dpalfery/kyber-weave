namespace KyberWeave.Contracts.Repositories;

/// <summary>How a child process ended.</summary>
/// <param name="ExitCode">The exit code; meaningless when <paramref name="TimedOut"/> is true.</param>
/// <param name="Output">Standard output followed by standard error.</param>
/// <param name="TimedOut">Whether the process was killed for running past its timeout.</param>
public sealed record ProcessRunResult(int ExitCode, string Output, bool TimedOut)
{
    /// <summary>What the program wrote to standard output, kept apart from its errors.</summary>
    /// <remarks>Defaults to <see cref="Output"/> so a result built with the three positional values still carries readable text.</remarks>
    public string StandardOutput { get; init; } = Output;

    /// <summary>What the program wrote to standard error, kept apart from its output.</summary>
    public string StandardError { get; init; } = string.Empty;
}
