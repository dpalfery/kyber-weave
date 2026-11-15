namespace KyberDash.Domain;

/// <summary>
/// Resolves the filesystem path to the canonical KyberDash SQLite store (<c>canon.db</c>),
/// matching <c>resolveCanonDbPath</c> in <c>dash/src/canon/paths.ts</c>.
/// </summary>
public static class CanonDbPath
{
    /// <summary>Environment variable overriding the canonical store location.</summary>
    public const string EnvironmentVariable = "KYBER_CANON_DB";

    /// <summary>Default directory name inside the user's home directory.</summary>
    public const string DefaultDirectoryName = ".kyberdash";

    /// <summary>Default database file name.</summary>
    public const string DefaultFileName = "canon.db";

    /// <summary>
    /// Resolves the canonical database path using explicit precedence: explicit argument,
    /// then <see cref="EnvironmentVariable"/>, then <c>~/.kyberdash/canon.db</c>.
    /// </summary>
    /// <param name="explicitPath">Caller-supplied override path.</param>
    /// <param name="environmentPath">Optional override for <see cref="EnvironmentVariable"/> (defaults to process environment).</param>
    /// <param name="homeDirectory">Optional override for the user profile directory (defaults to user profile).</param>
    /// <returns>The resolved database file path.</returns>
    public static string Resolve(
        string? explicitPath = null,
        string? environmentPath = null,
        string? homeDirectory = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return explicitPath;

        string? fromEnv = environmentPath ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnv))
            return fromEnv;

        string home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, DefaultDirectoryName, DefaultFileName);
    }
}
