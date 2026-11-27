namespace KyberWeave.Domain.StatusLine;

/// <summary>
/// The status-line root rules: where each harness's root, staging root, auto-load directories and
/// settings paths are. They read only the injected override lookup and home directory on
/// <see cref="StatusLineTargetRoots"/>, so they hold no process state.
/// </summary>
/// <remarks>
/// Kept in <c>KyberWeave.Application</c> with the data type's namespace so existing callers read the
/// same method names. The rules are split from the data type, which stays in <c>KyberWeave.Domain</c>
/// so that <c>KyberWeave.Contracts</c> can name it without referencing Application.
/// </remarks>
public static class StatusLineTargetRootsExtensions
{
    /// <summary>
    /// The Kyber-owned staging segment every harness stages beneath: <c>kyber/statusline</c>. Two
    /// segments rather than one dot-prefixed directory name, so the location reads as a namespace
    /// Kyber owns inside the harness root — <c>&lt;harness root&gt;/kyber/statusline</c> — and the
    /// contract suite pins it as a literal rather than reading it back from this type.
    /// </summary>
    private const string StagingNamespaceSegment = "kyber";

    /// <summary>The second half of the staging segment, after <see cref="StagingNamespaceSegment"/>.</summary>
    private const string StagingDirectorySegment = "statusline";

    /// <summary>
    /// The harness's own per-user root, which every other path this type reports is derived from.
    /// </summary>
    /// <remarks>
    /// The root a status line is <em>activated</em> from, not the root a deployment lands in. It is
    /// public because the activation guidance the CLI prints has to name absolute paths inside it
    /// (<c>agy</c> does not expand <c>~</c>), and because a test or an operator asking "where does
    /// Kyber think this harness lives?" deserves an answer that is the same one every plan uses.
    /// </remarks>
    public static string ResolveHarnessRoot(this StatusLineTargetRoots roots, StatusLineTarget target)
    {
        ArgumentNullException.ThrowIfNull(roots);
        RequireDefinedTarget(target);
        return target switch
        {
            StatusLineTarget.Claude => ResolveWithOverride(roots, "CLAUDE_CONFIG_DIR", ".claude"),
            StatusLineTarget.Pi => ResolveWithOverride(
                roots,
                "PI_CODING_AGENT_DIR",
                Path.Combine(".pi", "agent")),
            StatusLineTarget.Agy => ResolveWithOverride(
                roots,
                StatusLineTargetRoots.AgyConfigDirectoryVariable,
                Path.Combine(".gemini", "antigravity-cli")),
            StatusLineTarget.CommandCode => ResolveWithOverride(roots, null, ".commandcode"),
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
    public static string ResolveStagingRoot(this StatusLineTargetRoots roots, StatusLineTarget target)
    {
        return Path.Combine(
            roots.ResolveHarnessRoot(target),
            StagingNamespaceSegment,
            StagingDirectorySegment);
    }

    /// <summary>
    /// Directories <paramref name="target"/> loads automatically, so a file placed there is active
    /// without the user's consent. Empty for a harness with no auto-load hazard.
    /// </summary>
    /// <remarks>
    /// Pi discovers extensions in <c>~/.pi/agent/extensions/</c>. Command Code auto-loads mods from
    /// <c>~/.commandcode/mods/</c>. Claude Code and the Antigravity CLI load no status-line file
    /// from a directory at all.
    /// </remarks>
    public static IReadOnlyList<string> ResolveAutoLoadDirectories(this StatusLineTargetRoots roots, StatusLineTarget target)
    {
        RequireDefinedTarget(target);
        return target switch
        {
            StatusLineTarget.Pi => [Path.Combine(roots.ResolveHarnessRoot(target), "extensions")],
            StatusLineTarget.CommandCode => [Path.Combine(roots.ResolveHarnessRoot(target), "mods")],
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
    public static IReadOnlyList<string> ResolveSettingsPaths(this StatusLineTargetRoots roots, StatusLineTarget target)
    {
        RequireDefinedTarget(target);
        string root = roots.ResolveHarnessRoot(target);
        return target switch
        {
            StatusLineTarget.Claude =>
            [
                Path.Combine(root, "settings.json"),
                Path.Combine(root, "settings.local.json")
            ],
            StatusLineTarget.Agy => [Path.Combine(root, "settings.json")],
            StatusLineTarget.Pi => [Path.Combine(root, "settings.json")],
            StatusLineTarget.CommandCode => [Path.Combine(root, "config.json")],
            _ => throw UnknownTarget(target)
        };
    }

    private static string ResolveWithOverride(
        StatusLineTargetRoots roots,
        string? overrideVariableName,
        string defaultRelativePath)
    {
        if (overrideVariableName is null)
            return Path.Combine(roots.HomeDirectory, defaultRelativePath);

        string? overrideValue = roots.GetEnvironmentVariable(overrideVariableName);
        return string.IsNullOrEmpty(overrideValue)
            ? Path.Combine(roots.HomeDirectory, defaultRelativePath)
            : RequireFullyQualified(overrideValue, overrideVariableName);
    }

    /// <summary>
    /// The same guard <see cref="StatusLineTargetRoots"/> applies to its home directory, applied to an
    /// override value read from the environment.
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
