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
    /// Returns <c>HOME</c>, else <c>USERPROFILE</c>, trimmed. On Windows a Git Bash or MSYS value such as
    /// <c>/c/Users/name</c> is converted to <c>C:\Users\name</c>. A blank or relative value counts as unset,
    /// because a relative home would put the prompt file under whatever directory the command ran in.
    /// Null when neither names an absolute directory.
    /// </summary>
    /// <remarks>
    /// There is deliberately no fallback to the system profile folder: doctor resolves home through this
    /// method too, and the two must agree. The install commands refuse to run when this returns null.
    /// </remarks>
    /// <param name="environment">Reads one environment variable by name.</param>
    /// <returns>The home directory, or null when the environment names none.</returns>
    public static string? FromEnvironment(Func<string, string?> environment) =>
        FromEnvironment(environment, OperatingSystem.IsWindows());

    /// <summary>
    /// <see cref="FromEnvironment(Func{string, string?})"/> with the host's path rules chosen by <paramref name="windows"/>,
    /// so both rule sets can be tested on any host.
    /// </summary>
    /// <param name="environment">Reads one environment variable by name.</param>
    /// <param name="windows">True to apply Windows path rules.</param>
    /// <returns>The home directory, or null when the environment names none.</returns>
    internal static string? FromEnvironment(Func<string, string?> environment, bool windows)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return Absolute(environment("HOME"), windows) ?? Absolute(environment("USERPROFILE"), windows);
    }

    private static string? Absolute(string? value, bool windows)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (!windows)
            return trimmed.StartsWith('/') ? trimmed : null;

        if (IsWindowsFullyQualified(trimmed))
            return trimmed;

        // Git Bash and MSYS put "/c/Users/name" in HOME. VS Code cannot load that form, and .NET would read it as
        // drive-relative, so convert a drive-letter prefix and treat any other leading '/' as unset.
        if (trimmed.Length >= 3 && trimmed[0] == '/' && char.IsAsciiLetter(trimmed[1]) && trimmed[2] == '/')
            return char.ToUpperInvariant(trimmed[1]) + @":\" + trimmed[3..].Replace('/', '\\');
        if (trimmed.Length == 2 && trimmed[0] == '/' && char.IsAsciiLetter(trimmed[1]))
            return char.ToUpperInvariant(trimmed[1]) + @":\";

        return null;
    }

    private static bool IsWindowsFullyQualified(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal);
}
