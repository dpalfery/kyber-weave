namespace KyberWeave.Core.RunStart;

/// <summary>What one git invocation wrote, and how it ended.</summary>
/// <param name="ExitCode">The exit code git returned.</param>
/// <param name="StandardOutput">Everything git wrote to standard output.</param>
/// <param name="StandardError">Everything git wrote to standard error.</param>
public readonly record struct GitOutcome(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Whether git exited zero.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>The first non-empty line of standard error, for a failure message.</summary>
    public string FirstErrorLine
    {
        get
        {
            string? line = (StandardError + "\n" + StandardOutput)
                .Split('\n')
                .Select(part => part.Trim())
                .FirstOrDefault(part => part.Length > 0);
            return line ?? "no output";
        }
    }
}
