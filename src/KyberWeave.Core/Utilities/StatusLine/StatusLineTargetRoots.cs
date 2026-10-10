namespace KyberWeave.Core.Utilities.StatusLine;

/// <summary>Resolves each harness's per-user root, staging root, settings paths, and auto-load hazards.</summary>
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
/// (A4, C2), but this type names them so a plan can prove it never touches one.
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

    /// <summary>
    /// The Kyber-owned staging segment every harness stages beneath: <c>kyber/statusline</c>. Two
    /// segments rather than one dot-prefixed directory name, so the location reads as a namespace
    /// Kyber owns inside the harness root — <c>&lt;harness root&gt;/kyber/statusline</c> — and the
    /// contract suite pins it as a literal rather than reading it back from this type.
    /// </summary>
    private const string StagingNamespaceSegment = "kyber";

    /// <summary>The second half of the staging segment, after <see cref="StagingNamespaceSegment"/>.</summary>
    private const string StagingDirectorySegment = "statusline";

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
    /// The harness's own per-user root, which every other path this type reports is derived from.
    /// </summary>
    /// <remarks>
    /// The root a status line is <em>activated</em> from, not the root a deployment lands in. It is
    /// public because the activation guidance the CLI prints has to name absolute paths inside it
    /// (<c>agy</c> does not expand <c>~</c>), and because a test or an operator asking "where does
    /// Kyber think this harness lives?" deserves an answer that is the same one every plan uses.
    /// </remarks>
    public string ResolveHarnessRoot(StatusLineTarget target)
    {
        RequireDefinedTarget(target);
        return target switch
        {
            StatusLineTarget.Claude => ResolveWithOverride("CLAUDE_CONFIG_DIR", ".claude"),
            StatusLineTarget.Pi => ResolveWithOverride(
                "PI_CODING_AGENT_DIR",
                Path.Combine(".pi", "agent")),
            StatusLineTarget.Agy => ResolveWithOverride(
                AgyConfigDirectoryVariable,
                Path.Combine(".gemini", "antigravity-cli")),
            _ => throw UnknownTarget(target)
        };
    }

    /// <summary>The Kyber-owned staging root for <paramref name="target"/>, which is never auto-loaded.</summary>
    /// <remarks>
    /// Always a child of <see cref="ResolveHarnessRoot"/>, so an override moves the staging root with
    /// the harness instead of splitting the two. For Pi, whose harness root contains a directory its
    /// own host auto-loads, this sits beside that directory rather than inside it — a file in an
    /// auto-load directory would be active without the user applying anything, which A3 forbids and
    /// C1 records.
    /// </remarks>
    public string ResolveStagingRoot(StatusLineTarget target)
    {
        return Path.Combine(
            ResolveHarnessRoot(target),
            StagingNamespaceSegment,
            StagingDirectorySegment);
    }

    /// <summary>
    /// Directories <paramref name="target"/> loads automatically, so a file placed there is active
    /// without the user's consent. Empty for a harness with no auto-load hazard.
    /// </summary>
    /// <remarks>
    /// Pi discovers extensions in <c>~/.pi/agent/extensions/</c>. Claude Code and the Antigravity CLI
    /// load no status-line file from a directory at all.
    /// </remarks>
    public IReadOnlyList<string> ResolveAutoLoadDirectories(StatusLineTarget target)
    {
        RequireDefinedTarget(target);
        return target switch
        {
            StatusLineTarget.Pi => [Path.Combine(ResolveHarnessRoot(target), "extensions")],
            _ => []
        };
    }

    /// <summary>
    /// The harness settings paths Kyber Utilities never creates, opens for write, or modifies.
    /// </summary>
    /// <remarks>
    /// Listed so a plan can prove no planned path is one of them, not so anything can read them:
    /// <c>doctor</c> checks only Kyber-owned files and prerequisites (D5). Activation is manual, so
    /// the user applies the entry these files would carry (A3). The <c>agy</c> file name is the one
    /// entry here that the plan records as unverified — the macOS settings path is documented
    /// inconsistently and T14 confirms it live — and a wrong name costs nothing, because the list
    /// only ever guards against writing.
    /// </remarks>
    public IReadOnlyList<string> ResolveSettingsPaths(StatusLineTarget target)
    {
        RequireDefinedTarget(target);
        string root = ResolveHarnessRoot(target);
        return target switch
        {
            StatusLineTarget.Claude =>
            [
                Path.Combine(root, "settings.json"),
                Path.Combine(root, "settings.local.json")
            ],
            StatusLineTarget.Agy => [Path.Combine(root, "settings.json")],
            StatusLineTarget.Pi => [Path.Combine(root, "settings.json")],
            _ => throw UnknownTarget(target)
        };
    }

    private string ResolveWithOverride(string overrideVariableName, string defaultRelativePath)
    {
        string? overrideValue = _getEnvironmentVariable(overrideVariableName);
        return string.IsNullOrEmpty(overrideValue)
            ? Path.Combine(_homeDirectory, defaultRelativePath)
            : RequireFullyQualified(overrideValue, overrideVariableName);
    }

    /// <summary>
    /// A relative root or override would be completed against the process working directory by
    /// <see cref="KyberWeave.Core.Squad.Deployment.SquadPathPolicy.ResolveFile"/>, so a deployment
    /// could write outside the home tree it claims to own. Reject it rather than resolve it.
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

    private static void RequireDefinedTarget(StatusLineTarget target)
    {
        if (!Enum.IsDefined(target))
            throw UnknownTarget(target);
    }

    private static ArgumentOutOfRangeException UnknownTarget(StatusLineTarget target)
    {
        return new ArgumentOutOfRangeException(nameof(target), target,
            "No status-line root rule exists for this target.");
    }
}
