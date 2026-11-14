namespace KyberWeave.Core.Doctor;

/// <summary>
///     Advisory doctor check: whether GitHub Copilot CLI is set to keep large tool results inline, so the code index
///     stays usable.
/// </summary>
/// <remarks>
///     Copilot CLI saves any tool result over 20 KiB to a temporary file and hands the model only a path and a preview.
///     CodeGraph answers run up to 25,000 characters, so by default many of them spill and agents then stop using the
///     index. The finding is a warning only: it passes, so it never changes the exit code of any doctor mode. Nothing
///     is written; the person sets the variable themselves.
/// </remarks>
public static class CopilotLargeOutputProbe
{
    /// <summary>The check id, in the dotted style of the other doctor checks.</summary>
    public const string CheckId = "copilot.large-output-limit";

    /// <summary>The environment variable Copilot CLI reads for its large-output limit.</summary>
    public const string VariableName = "COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES";

    /// <summary>
    ///     The smallest limit recommended, in bytes. CodeGraph answers reach 25,000 characters; 32,000 bytes keeps every
    ///     one inline, including answers with multi-byte characters.
    /// </summary>
    public const long RecommendedThresholdBytes = 32000;

    /// <summary>The bash and zsh line that sets the recommended limit.</summary>
    public const string PosixFix = "export " + VariableName + "=32000";

    /// <summary>The PowerShell line that sets the recommended limit.</summary>
    public const string PowerShellFix = "$env:" + VariableName + " = \"32000\"";

    private const string WarningPrefix = "warning: ";

    /// <summary>Runs the check.</summary>
    /// <param name="repositoryRoot">The repository root, whose Copilot files show the Copilot target is installed.</param>
    /// <param name="home">The user's home directory, or null when the environment names none.</param>
    /// <param name="environment">Reads one environment variable.</param>
    /// <returns>One check, or none when Copilot CLI is not in use.</returns>
    public static IReadOnlyList<DoctorCheck> Run(string repositoryRoot, string? home, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(environment);

        if (!CopilotInUse(repositoryRoot, home, environment))
            return [];

        string? raw = environment(VariableName);
        if (long.TryParse(raw?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value)
            && value >= RecommendedThresholdBytes)
            return [new DoctorCheck(CheckId, true, $"{VariableName} is {value}; large tool results stay inline", null)];

        string problem = string.IsNullOrWhiteSpace(raw)
            ? $"{VariableName} is not set, so Copilot CLI saves tool results over 20 KiB to a file instead of showing them to the agent"
            : long.TryParse(raw.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long low)
                ? $"{VariableName} is {low}, below {RecommendedThresholdBytes}, so Copilot CLI saves large tool results to a file instead of showing them to the agent"
                : $"{VariableName} is not a whole number of bytes, so Copilot CLI saves tool results over 20 KiB to a file instead of showing them to the agent";

        string reason = $"{WarningPrefix}{problem}; that makes agents stop using the code index. "
            + $"Set it in the environment Copilot CLI starts from. bash/zsh: {PosixFix} PowerShell: {PowerShellFix}";
        return [new DoctorCheck(CheckId, true, reason, PosixFix)];
    }

    /// <summary>
    ///     Whether the Copilot target is in use: the user has a Copilot CLI home directory, or this repository holds
    ///     the files the Squad Copilot target installs.
    /// </summary>
    private static bool CopilotInUse(string repositoryRoot, string? home, Func<string, string?> environment)
    {
        string? copilotHome = environment("COPILOT_HOME");
        if (!string.IsNullOrWhiteSpace(copilotHome) && Directory.Exists(copilotHome))
            return true;

        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(Path.Combine(home, ".copilot")))
            return true;

        return Directory.Exists(Path.Combine(repositoryRoot, ".github", "agents"))
            || File.Exists(Path.Combine(repositoryRoot, ".github", "copilot-instructions.md"));
    }
}
