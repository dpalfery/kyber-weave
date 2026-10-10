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
/// repository root; the link pass resolves both sides again through
/// <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> and requires the real candidate to
/// sit inside the real root. A header naming a repository-internal symlink therefore fails
/// the second pass even though it passed the first.
/// </para>
/// <para>
/// The root's own links are resolved on both sides, and that is not incidental: on macOS
/// <c>/var</c> is a symlink to <c>/private/var</c>, so every repository under the temporary
/// directory is reached through one. Comparing a resolved candidate against an unresolved
/// root would reject every plan on the platform.
/// </para>
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
    /// The lexical full path of the file, or null when it names nothing inside the
    /// repository. The returned path is the one the caller opens; it is deliberately not
    /// the link-resolved one, so that a path accepted through an in-repository symlink
    /// still reads the file the repository names. A rejection is a normal outcome, not a
    /// failure: the caller reads it as <c>plan.exists: false</c> and lets the rules decide
    /// on the absence.
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

        if (!IsInside(candidate, root) || !SurvivesLinks(candidate, root))
        {
            return null;
        }

        return candidate;
    }

    /// <summary>
    /// Whether the candidate is still inside the repository once every symbolic link in
    /// both paths has been followed.
    /// </summary>
    /// <remarks>
    /// The walk is component by component rather than a single
    /// <c>ResolveLinkTarget</c> on the whole path, because that call resolves only the leaf:
    /// a link in the middle of the path is invisible to it. Both sides are resolved through
    /// the same walk so the two forms of the same directory still compare equal.
    /// </remarks>
    private static bool SurvivesLinks(string candidate, string root)
    {
        try
        {
            string? realCandidate = ResolveLinks(candidate);
            string? realRoot = ResolveLinks(root);
            return realCandidate is not null
                && realRoot is not null
                && IsInside(realCandidate, realRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A path whose links cannot be followed — a loop, or a link into a directory
            // this process may not read — has no real form, so containment cannot be
            // established. Rejecting is the only answer that does not read it.
            return false;
        }
    }

    /// <summary>How many components one path resolution may visit before it is treated as a loop.</summary>
    private const int MaxLinkHops = 4096;

    /// <summary>
    /// The real path of <paramref name="path"/> with every component's links followed, or
    /// null when a component cannot be resolved.
    /// </summary>
    /// <remarks>
    /// A resolved target replaces the remaining work rather than extending it, and the walk
    /// restarts from the target's own path root. That is what a link's target needs: the
    /// target is an ordinary path that may itself sit behind links — on macOS a plan
    /// symlinked to <c>/var/folders/...</c> names <c>/private/var/folders/...</c> once
    /// followed — and a walk that resumed where it stood would compare an unresolved
    /// candidate against a resolved root and reject every one.
    /// </remarks>
    private static string? ResolveLinks(string path)
    {
        string current = Path.GetPathRoot(path) ?? string.Empty;
        Queue<string> pending = new(Segments(path, current));
        int visited = 0;

        while (pending.Count > 0)
        {
            if (++visited > MaxLinkHops)
            {
                // Two links pointing at each other never drain the queue. The caller's
                // rule is that a path which cannot be resolved is rejected, so a loop
                // ends the walk rather than the process.
                return null;
            }

            current = Path.Combine(current, pending.Dequeue());
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            FileSystemInfo? target = entry.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                continue;
            }

            current = Path.GetPathRoot(target.FullName) ?? current;
            Queue<string> restarted = new(Segments(target.FullName, current));
            while (pending.Count > 0)
            {
                restarted.Enqueue(pending.Dequeue());
            }

            pending = restarted;
        }

        return current.Length == 0 ? null : current;
    }

    /// <summary>The path components below <paramref name="pathRoot"/>.</summary>
    private static IEnumerable<string> Segments(string path, string pathRoot) =>
        path[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

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
