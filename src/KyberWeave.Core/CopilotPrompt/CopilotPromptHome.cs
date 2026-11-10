namespace KyberWeave.Core.CopilotPrompt;

/// <summary>
/// Resolves the home directory the Copilot prompt override lives under, the same way for the install
/// commands and for <c>kyber-weave doctor</c>.
/// </summary>
/// <remarks>
/// The prompt file path is written into VS Code's settings and later compared by doctor, so both must
/// resolve the same home. On Windows <c>HOME</c> can be set (Git Bash, MSYS) and differ from the profile
/// folder; reading the variables in one order everywhere keeps the two from disagreeing.
/// </remarks>
public static class CopilotPromptHome
{
    /// <summary>
    /// Returns <c>HOME</c>, else <c>USERPROFILE</c>, trimmed. A blank or relative value counts as unset,
    /// because a relative home would put the prompt file under whatever directory the command ran in.
    /// Null when neither names an absolute directory.
    /// </summary>
    /// <remarks>
    /// There is deliberately no fallback to the system profile folder: doctor resolves home through this
    /// method too, and the two must agree. The install commands refuse to run when this returns null.
    /// </remarks>
    /// <param name="environment">Reads one environment variable by name.</param>
    /// <returns>The home directory, or null when the environment names none.</returns>
    public static string? FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return Absolute(environment("HOME")) ?? Absolute(environment("USERPROFILE"));
    }

    private static string? Absolute(string? value)
    {
        string? trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || !Path.IsPathFullyQualified(trimmed) ? null : trimmed;
    }
}
