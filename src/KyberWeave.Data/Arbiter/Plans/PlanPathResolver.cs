using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using System.Runtime.InteropServices;

namespace KyberWeave.Core.Arbiter.Plans;

/// <summary>
/// Resolves a plan path that arrived in an event header to a file inside the repository,
/// or rejects it.
/// </summary>
/// <remarks>
/// <para>
/// <c>PLAN_FILE</c> comes from a dispatch's prompt header, which the caller controls. It
/// is resolved against the repository root and then read, so a rooted path or a
/// <c>..</c> segment would otherwise let a dispatch name any file on the machine and have
/// its content parsed and summarised into a decision.
/// </para>
/// <para>
/// Containment is decided twice, because collapsing <c>..</c> and collapsing symlinks are
/// different operations and only the first one is what <see cref="Path.GetFullPath(string)"/>
/// does. The lexical pass resolves the candidate and requires it to sit inside the
/// repository root; the link pass asks the path seam whether the candidate is within the root after both
/// sides' links are resolved. A header naming a repository-internal symlink therefore fails
/// the second pass even though it passed the first.
/// </para>
/// <para>
/// The root's own links are resolved on both sides, and that is not incidental: on macOS
/// <c>/var</c> is a symlink to <c>/private/var</c>, so every repository under the temporary
/// directory is reached through one. Comparing a resolved candidate against an unresolved
/// root would reject every plan on the platform.
/// </para>
/// </remarks>
public sealed class PlanPathResolver(IPathResolver paths, IProcessEnvironment environment) : IPlanFileResolver
{
    /// <summary>The platform's path comparison rule: case-insensitive on Windows and macOS.</summary>
    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <inheritdoc />
    public string? Resolve(string? planFile, string repositoryRoot)
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
            root = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
            candidate = paths.GetFullPath(planFile, root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!IsInside(candidate, root) || !SurvivesLinks(candidate, root))
        {
            return null;
        }

        return candidate;
    }

    private bool SurvivesLinks(string candidate, string root)
    {
        try
        {
            return paths.AreSame(candidate, root) || paths.IsWithin(root, candidate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A path whose links cannot be followed — a loop, or a link into a directory
            // this process may not read — has no real form, so containment cannot be
            // established. Rejecting is the only answer that does not read it.
            return false;
        }
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
