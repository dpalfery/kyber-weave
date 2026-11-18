namespace KyberWeave.Domain.StatusLine;

/// <summary>
/// The injected per-user inputs the status-line root rules resolve against: the override environment
/// lookup and the home directory. The rules themselves (<c>ResolveHarnessRoot</c>, <c>ResolveStagingRoot</c>,
/// <c>ResolveAutoLoadDirectories</c>, <c>ResolveSettingsPaths</c>) are extension methods in
/// <c>KyberWeave.Application</c>.
/// </summary>
/// <remarks>
/// Follows <c>SquadGlobalRoots</c> for the harness roots — an override environment variable first,
/// then the verified default beneath the home directory — and reuses the same variable names, so
/// Squad and Kyber Utilities agree about where a harness lives. It reads no process state: the
/// override lookup and the home directory are both injected, which is what lets a test drive the
/// whole deployment surface against a temporary home instead of the operator's configuration.
///
/// <para>
/// Three things are deliberately <em>not</em> reused from Squad. First, the Antigravity root:
/// Squad's <c>antigravity</c> target resolves to <c>~/.gemini/config</c>, which is the Gemini agent
/// configuration directory and not the <c>agy</c> CLI's settings directory this slice needs, so
/// <see cref="StatusLineTarget.Agy"/> resolves its own root and honours its own
/// <see cref="AgyConfigDirectoryVariable"/> override. Second, the staging location: Squad deploys
/// files a harness loads, while Kyber Utilities stages files a harness loads only after the user
/// wires them up themselves (A3), so every staging root is a Kyber-owned
/// <c>kyber/statusline</c> segment beneath the harness root and never an auto-load directory
/// (C1). Third, the settings paths: Squad owns no settings file and neither does Kyber Utilities
/// (A4, C2), but the resolver names them so a plan can prove it never touches one.
/// </para>
/// </remarks>
public sealed class StatusLineTargetRoots
{
    /// <summary>
    /// The override the Antigravity CLI root honours. The plan leaves <c>agy</c>'s own root rule to
    /// be confirmed live (D4, T10) and names no variable for it, so this is the documented name this
    /// slice chooses; <c>agy</c> itself publishes none.
    /// </summary>
    public const string AgyConfigDirectoryVariable = "AGY_CONFIG_DIR";

    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly string _homeDirectory;

    /// <param name="getEnvironmentVariable">Reads an override environment variable's value.</param>
    /// <param name="homeDirectory">The fully qualified per-user home directory.</param>
    public StatusLineTargetRoots(Func<string, string?> getEnvironmentVariable, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        _getEnvironmentVariable = getEnvironmentVariable;
        _homeDirectory = RequireFullyQualified(homeDirectory, nameof(homeDirectory));
    }

    /// <summary>The override lookup the resolver reads before any harness default.</summary>
    public Func<string, string?> GetEnvironmentVariable => _getEnvironmentVariable;

    /// <summary>The fully qualified per-user home directory the resolver completes defaults against.</summary>
    public string HomeDirectory => _homeDirectory;

    /// <summary>
    /// A relative root or override would be completed against the process working directory by
    /// <c>SquadPathPolicy.ResolveFile</c>, so a deployment could write outside the home tree it
    /// claims to own. Reject it rather than resolve it.
    /// </summary>
    private static string RequireFullyQualified(string path, string paramName)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "A status-line root must be fully qualified so it cannot resolve against the " +
                "process working directory.",
                paramName);
        }

        return path;
    }
}
