using System.Runtime.InteropServices;

namespace KyberWeave.Core.Arbiter.Plans;

/// <summary>
/// Resolves a plan path that arrived in an event header to a file inside the repository,
/// or rejects it.
/// </summary>
/// <remarks>
/// <c>PLAN_FILE</c> comes from a dispatch's prompt header, which the caller controls. It
/// is resolved against the repository root and then read, so a rooted path or a
/// <c>..</c> segment would otherwise let a dispatch name any file on the machine and have
/// its content parsed and summarised into a decision. Containment is checked on the fully
/// resolved path, after <c>..</c> and symlinks are collapsed, because that is the only form
/// that describes the file actually opened.
/// </remarks>
public static class PlanPathResolver
{
    /// <summary>The platform's path comparison rule: case-insensitive on Windows and macOS.</summary>
    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Resolves <paramref name="planFile"/> inside <paramref name="repositoryRoot"/>.
    /// </summary>
    /// <returns>
    /// The full path of the file, or null when it names nothing inside the repository.
    /// A rejection is a normal outcome, not a failure: the caller reads it as
    /// <c>plan.exists: false</c> and lets the rules decide on the absence.
    /// </returns>
    public static string? Resolve(string? planFile, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(planFile))
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        string root;
        string candidate;
        try
        {
            root = Path.GetFullPath(repositoryRoot);
            candidate = Path.GetFullPath(Path.IsPathRooted(planFile)
                ? planFile
                : Path.Combine(repositoryRoot, planFile));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return IsInside(candidate, root) ? candidate : null;
    }

    private static bool IsInside(string candidate, string root)
    {
        if (candidate.Length == root.Length)
        {
            return string.Equals(candidate, root, PathComparison);
        }

        // The trailing separator is what distinguishes "/repo-evil" from "/repo".
        return candidate.Length > root.Length
            && candidate.StartsWith(root, PathComparison)
            && candidate[root.Length] == Path.DirectorySeparatorChar;
    }
}
