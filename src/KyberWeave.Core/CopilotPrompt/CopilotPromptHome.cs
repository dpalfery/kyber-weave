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
    /// Returns <c>HOME</c>, else <c>USERPROFILE</c>, ignoring a blank value. Null when neither is set.
    /// </summary>
    /// <param name="environment">Reads one environment variable by name.</param>
    /// <returns>The home directory, or null when the environment names none.</returns>
    public static string? FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string? home = environment("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return home;

        string? profile = environment("USERPROFILE");
        return string.IsNullOrWhiteSpace(profile) ? null : profile;
    }
}
