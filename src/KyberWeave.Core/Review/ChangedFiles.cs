using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Review;

/// <summary>The kind of change a path in the working tree carries.</summary>
public enum ChangeKind
{
    Unknown = 0,
    Added,
    Copied,
    Modified,
    Renamed,
    Deleted,
    Untracked
}

/// <summary>One changed path with the kind of change it carries.</summary>
/// <param name="Path">Repository-relative path, forward-slashed.</param>
/// <param name="Kind">How the path changed since the merge base.</param>
public sealed record ChangedPath(string Path, ChangeKind Kind);

/// <summary>Lists the files a working tree changed, by running git with argv and never a shell.</summary>
public static class ChangedFiles
{
    /// <summary>The ref the working tree is compared with when none is named.</summary>
    public const string DefaultBase = "HEAD";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Refuses a <c>--base</c> value git would read as an option, or that is empty.</summary>
    /// <returns>The reason it is refused, or <see langword="null"/> when the value is acceptable.</returns>
    public static string? ValidateBase(string baseRef)
    {
        if (string.IsNullOrWhiteSpace(baseRef))
            return "--base is empty. Name a git ref such as HEAD or origin/main.";

        if (baseRef.StartsWith('-'))
            return $"--base '{baseRef}' starts with '-' and would be read as a git option. " +
                   "Name a git ref such as HEAD or origin/main.";

        return null;
    }

    /// <summary>
    /// Lists the paths changed since <paramref name="baseRef"/> that still exist, together with untracked files.
    /// </summary>
    /// <remarks>
    /// Deleted paths are left out on purpose: a gate cannot read a file that is gone. The output is
    /// NUL-separated so a name git would otherwise quote comes through exactly as written.
    /// </remarks>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="baseRef">The ref to diff the working tree against.</param>
    /// <exception cref="InvalidOperationException">git could not list the files.</exception>
    public static IReadOnlyList<string> Resolve(string workingDirectory, string baseRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string[] changed = RunGitPaths(
            workingDirectory,
            "diff", "-z", "--name-only", "--relative", "--diff-filter=ACMRT", baseRef, "--");
        string[] untracked = RunGitPaths(
            workingDirectory,
            "ls-files", "-z", "--others", "--exclude-standard", "--");

        return changed
            .Concat(untracked)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Lists the paths changed since the merge base of <paramref name="baseRef"/> and
    /// <c>HEAD</c> with the kind of each change, together with untracked files.
    /// </summary>
    /// <remarks>
    /// The change under review is the working tree, not the last commit: the diff runs
    /// from the merge base to the files on disk, so staged renames, uncommitted edits,
    /// and untracked files are all in scope. A deleted path is reported as
    /// <see cref="ChangeKind.Deleted"/> rather than dropped — a deleted archive carries
    /// no document to check, and the caller tells that apart from a path that was never
    /// touched. Output is NUL-separated so a name git would otherwise quote comes
    /// through exactly as written.
    /// </remarks>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="baseRef">The ref the merge base is resolved against.</param>
    /// <exception cref="InvalidOperationException">
    /// No merge base resolves, so the scope is unknown. The message says how to fetch
    /// the history the scope needs.
    /// </exception>
    public static IReadOnlyList<ChangedPath> ResolveSinceMergeBase(string workingDirectory, string baseRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string mergeBase = ResolveMergeBase(workingDirectory, baseRef);

        string[] status = RunGitPaths(
            workingDirectory,
            "diff", "-z", "--name-status", "-M", "--relative", mergeBase, "--");
        string[] untracked = RunGitPaths(
            workingDirectory,
            "ls-files", "-z", "--others", "--exclude-standard", "--");

        List<ChangedPath> changes = new();
        for (int i = 0; i < status.Length; i++)
        {
            string entry = status[i];
            if (entry.Length == 0)
                continue;

            // A rename or copy carries two paths: the similarity score and the old path,
            // then the new path. The entry is the file as archived, so the new path wins.
            if (entry[0] is 'R' or 'C')
            {
                if (i + 2 >= status.Length)
                    break;

                changes.Add(new ChangedPath(
                    status[i + 2],
                    entry[0] == 'R' ? ChangeKind.Renamed : ChangeKind.Copied));
                i += 2;
                continue;
            }

            if (i + 1 >= status.Length)
                break;

            changes.Add(new ChangedPath(status[i + 1], MapStatus(entry[0])));
            i += 1;
        }

        foreach (string path in untracked)
            changes.Add(new ChangedPath(path, ChangeKind.Untracked));

        return changes
            .DistinctBy(change => change.Path, StringComparer.Ordinal)
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Resolves the merge base of <paramref name="baseRef"/> and <c>HEAD</c>, failing
    /// closed with a fetch hint when the history on disk cannot answer.
    /// </summary>
    private static string ResolveMergeBase(string workingDirectory, string baseRef)
    {
        string[] output;
        try
        {
            output = RunGitPaths(workingDirectory, "merge-base", baseRef, "HEAD");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Archive closeout scope is unknown: cannot determine what this change archives " +
                $"from base '{baseRef}' ({ex.Message.Trim()}). " +
                $"Fetch the full history ('git fetch --unshallow' or 'git fetch origin') and re-run.",
                ex);
        }

        if (output.Length == 0)
        {
            throw new InvalidOperationException(
                $"Archive closeout scope is unknown: cannot determine what this change archives " +
                $"from base '{baseRef}' (git merge-base reported no common commit). " +
                $"Fetch the full history ('git fetch --unshallow' or 'git fetch origin') and re-run.");
        }

        return output[0].Trim();
    }

    private static ChangeKind MapStatus(char status)
    {
        return status switch
        {
            'A' => ChangeKind.Added,
            'C' => ChangeKind.Copied,
            'M' => ChangeKind.Modified,
            'R' => ChangeKind.Renamed,
            'D' => ChangeKind.Deleted,
            'T' => ChangeKind.Modified,
            _ => ChangeKind.Unknown
        };
    }

    private static string[] RunGitPaths(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited with code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
