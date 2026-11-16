using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>Reads the facts the Arbiter derives itself from git.</summary>
/// <remarks>
/// Every call goes through <see cref="ProcessRunner.Run"/> with argv only: arguments
/// travel in <see cref="ProcessStartInfo.ArgumentList"/>, never a shell string, so a
/// hostile path or base cannot re-enable the shell. Outside a repository, or without
/// git on <c>PATH</c>, every fact is absent and nothing throws — the Arbiter rules on
/// fewer facts rather than failing the dispatch.
/// </remarks>
public sealed class GitFacts : IGitRepositoryFacts
{

    /// <summary>Resolves the repository root for a working directory.</summary>
    /// <returns>The root, or <c>null</c> when it cannot be determined.</returns>
    public string? GetRoot(string workingDirectory, string gitExecutable = "git")
    {
        string? output = RunGit(
            workingDirectory, gitExecutable, string.Empty, "rev-parse", "--show-toplevel");
        string root = output?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(root) ? null : root;
    }


    /// <summary>
    /// The working trees of the repository at <paramref name="workingDirectory"/>, main first, from
    /// <c>git worktree list --porcelain</c>. Empty outside a repository or when git cannot run.
    /// </summary>
    public IReadOnlyList<string> ListWorktrees(string workingDirectory, string gitExecutable = "git")
    {
        string? output = RunGit(workingDirectory, gitExecutable, string.Empty, "worktree", "list", "--porcelain");
        if (output is null)
            return [];

        const string Prefix = "worktree ";
        return [.. output.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(line => line[Prefix.Length..])
            .Where(path => path.Length > 0)];
    }


    /// <summary>
    /// Reads the repository's common git directory as an absolute path: the one directory every worktree of
    /// a repository shares, so it names the repository whichever worktree asks.
    /// </summary>
    /// <returns>The directory, or <c>null</c> outside a repository.</returns>
    public string? GetCommonDirectory(string workingDirectory, string gitExecutable = "git")
    {
        string? output = RunGit(
            workingDirectory, gitExecutable, string.Empty, "rev-parse", "--path-format=absolute", "--git-common-dir");
        string directory = output?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }


    /// <summary>Reads the current branch name.</summary>
    /// <returns>The branch, or <c>null</c> on a detached head or outside a repository.</returns>
    public string? GetCurrentBranch(string workingDirectory, string gitExecutable = "git")
    {
        string? output = RunGit(
            workingDirectory, gitExecutable, string.Empty, "branch", "--show-current");
        string branch = output?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(branch) ? null : branch;
    }


    /// <summary>Whether <paramref name="revision" /> resolves in the checkout.</summary>
    /// <returns>False for a bogus ref or outside a repository; never throws.</returns>
    public bool RefExists(string workingDirectory, string revision, string gitExecutable = "git")
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return false;
        }

        return RunGit(workingDirectory, gitExecutable, string.Empty, "rev-parse", "--verify", "--quiet", revision) is not null;
    }


    /// <summary>Reads the current commit.</summary>
    /// <returns>The full <c>HEAD</c> sha, or <c>null</c> when it cannot be determined.</returns>
    public string? GetHead(string workingDirectory, string gitExecutable = "git")
    {
        string? output = RunGit(
            workingDirectory, gitExecutable, string.Empty, "rev-parse", "HEAD");
        string head = output?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(head) ? null : head;
    }


    /// <summary>Reads the dirty set: staged, unstaged, and untracked paths.</summary>
    /// <returns>Porcelain status per repository-relative path; empty when absent.</returns>
    public IReadOnlyDictionary<string, string> GetDirtyStatuses(
        string workingDirectory,
        string gitExecutable = "git")
    {
        string? status = RunGit(
            workingDirectory,
            gitExecutable,
            string.Empty,
            "status",
            "--porcelain=v1",
            "-z",
            "--untracked-files=all");
        return status is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : GitDiffParser.ParsePorcelainZ(status);
    }


    /// <summary>Hashes working-tree files the way the index sees them.</summary>
    /// <returns>Blob id per path that could be hashed; empty when absent.</returns>
    public IReadOnlyDictionary<string, string> GetBlobIds(
        string workingDirectory,
        IEnumerable<string> paths,
        string gitExecutable = "git")
    {
        ArgumentNullException.ThrowIfNull(paths);

        // hash-object aborts the whole batch on the first unreadable path, so only
        // files that still exist go on stdin; the rest have no blob to record.
        List<string> existing = paths
            .Where(p => GitDiffParser.IsRepoRelative(p) && ExistsInRepo(workingDirectory, p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Dictionary<string, string> blobs = new(StringComparer.Ordinal);
        if (existing.Count == 0)
            return blobs;

        string? batch = RunGit(
            workingDirectory,
            gitExecutable,
            string.Join('\n', existing) + "\n",
            "hash-object",
            "--stdin-paths");
        if (batch is not null)
        {
            string[] hashes = batch.Split(
                ['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < Math.Min(existing.Count, hashes.Length); i++)
                blobs[existing[i]] = hashes[i];
            if (blobs.Count == existing.Count)
                return blobs;

            // A partial batch cannot be trusted positionally; fall through and hash
            // each file on its own so one vanishing path cannot mislabel the rest.
            blobs.Clear();
        }

        foreach (string path in existing)
        {
            string? single = RunGit(
                workingDirectory, gitExecutable, string.Empty, "hash-object", "--", path);
            string hash = single?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(hash))
                blobs[path] = hash;
        }

        return blobs;
    }


    /// <summary>Lists what changed since a base: committed diff plus the dirty set.</summary>
    /// <remarks>A rename reports its new path, matching the dirty set's convention.</remarks>
    /// <returns>Repository-relative paths; empty when absent.</returns>
    public IReadOnlySet<string> GetChangedPathsSinceBase(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git")
    {
        HashSet<string> changed = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(baseRef))
        {
            string? diff = RunGit(
                workingDirectory,
                gitExecutable,
                string.Empty,
                "diff",
                "--name-only",
                "-z",
                $"{baseRef}...HEAD");
            if (diff is not null)
                changed.UnionWith(GitDiffParser.SplitNul(diff));
        }

        changed.UnionWith(GetDirtyStatuses(workingDirectory, gitExecutable).Keys);
        return changed;
    }


    /// <summary>Reads the new-side hunk ranges of the diff against a base.</summary>
    /// <returns>One range per hunk; empty when absent.</returns>
    public IReadOnlyList<HunkRange> GetHunkRanges(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git")
    {
        if (string.IsNullOrWhiteSpace(baseRef))
            return [];
        string? diff = RunGit(
            workingDirectory, gitExecutable, string.Empty, "diff", "--unified=0", baseRef);
        return diff is null ? [] : GitDiffParser.ParseHunkRanges(diff);
    }


    /// <summary>Reads the changed hunks of the change against a base, committed work and the working tree together.</summary>
    /// <remarks>
    /// Untracked files are part of the change too: each is one whole-file hunk. The list is never
    /// cut short, because a lens that sees part of a change judges a partial change as the whole.
    /// </remarks>
    /// <returns>Every hunk ordered by path; <c>null</c> when the base is unreadable or nothing changed.</returns>
    public IReadOnlyList<ChangedHunk>? GetChangedHunks(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git")
    {
        ChangeSet? change = GetChangeSet(workingDirectory, baseRef, gitExecutable);
        return change?.Hunks.Select(measured => measured.Hunk).ToList();
    }


    /// <summary>
    /// Measures the change against a base once: its hunks with their line contents, and the paths
    /// renamed at full similarity. Everything the review facts need comes from this one diff.
    /// </summary>
    /// <remarks>
    /// The base is compared with the working tree, not with <c>HEAD</c>, so a change a lens has not
    /// yet committed is measured. Rename detection is on, so a rename shows as one entry; a rename
    /// that also edits content has hunks under its new path.
    /// </remarks>
    /// <returns>
    /// The change set, or <c>null</c> when the base cannot be read, an untracked file cannot be read,
    /// or nothing changed. A clean checkout is absence, not an empty measurement: a rule that reads
    /// an empty change as measured would answer for a change it never saw.
    /// </returns>
    public ChangeSet? GetChangeSet(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git")
    {
        if (string.IsNullOrWhiteSpace(baseRef))
        {
            return null;
        }

        string? diff = RunGit(
            workingDirectory,
            gitExecutable,
            string.Empty,
            "-c",
            "core.quotePath=false",
            "diff",
            "--unified=0",
            "--no-color",
            "--no-ext-diff",
            "--src-prefix=a/",
            "--dst-prefix=b/",
            "-M",
            baseRef);
        if (diff is null)
        {
            return null;
        }

        (List<MeasuredHunk> hunks, HashSet<string> renameOnly) = GitDiffParser.ParseChangeSet(diff);

        // Untracked files sit outside every diff, so each is read whole. ls-files prints paths
        // relative to the directory it runs in; --full-name keeps them repository-relative.
        string? untracked = RunGit(
            workingDirectory,
            gitExecutable,
            string.Empty,
            "ls-files",
            "--others",
            "--exclude-standard",
            "--full-name",
            "-z");
        if (untracked is null)
        {
            return null;
        }

        string? root = GetRoot(workingDirectory, gitExecutable);
        if (root is null)
        {
            return null;
        }

        foreach (string path in GitDiffParser.SplitNul(untracked))
        {
            if (!GitDiffParser.IsRepoRelative(path))
            {
                return null;
            }

            // An untracked file that is too large, binary, or unreadable makes the whole measurement absent:
            // a partial measurement would judge a partial change as the whole.
            List<string>? lines = ReadUntrackedLines(Path.Combine(root, path));
            if (lines is null)
            {
                return null;
            }

            hunks.Add(new MeasuredHunk(new ChangedHunk(path, 0, 0, 1, lines.Count), lines));
        }

        if (hunks.Count == 0 && renameOnly.Count == 0)
        {
            return null;
        }

        return new ChangeSet(
            hunks.OrderBy(measured => measured.Hunk.Path, StringComparer.Ordinal).ToList(),
            renameOnly);
    }


    /// <summary>
    /// Summarises a change as each changed path followed by its hunk headers, committed work
    /// and the working tree together, so a model can judge it without the full diff.
    /// </summary>
    /// <returns>The summary, or null when nothing changed or the diff cannot be read.</returns>
    public string? GetDiffSummary(
        string workingDirectory,
        string baseRef,
        IReadOnlyCollection<string> changedPaths,
        string gitExecutable = "git")
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        if (string.IsNullOrWhiteSpace(baseRef) || changedPaths.Count == 0)
            return null;

        string? mergeBase = RunGit(workingDirectory, gitExecutable, string.Empty, "merge-base", baseRef, "HEAD")?.Trim();
        if (string.IsNullOrEmpty(mergeBase))
            return null;

        string? diff = RunGit(
            workingDirectory,
            gitExecutable,
            string.Empty,
            "-c",
            "core.quotePath=false",
            "diff",
            "--unified=0",
            "--no-color",
            "--no-ext-diff",
            "--no-renames",
            "--src-prefix=a/",
            "--dst-prefix=b/",
            mergeBase);
        if (diff is null)
            return null;

        Dictionary<string, List<string>> headers = GitDiffParser.ParseHunkHeaders(diff);
        List<string> lines = [];
        foreach (string path in changedPaths.Order(StringComparer.Ordinal))
        {
            lines.Add(path);
            if (headers.TryGetValue(path, out List<string>? pathHeaders))
                lines.AddRange(pathHeaders);
        }

        return string.Join('\n', lines);
    }


    /// <summary>Captures the snapshot the ledger stores at pre-dispatch.</summary>
    /// <returns><c>HEAD</c> plus the blob id of every dirty or untracked path.</returns>
    public GitSnapshot? CaptureSnapshot(
        string workingDirectory,
        string gitExecutable = "git")
    {
        string? head = GetHead(workingDirectory, gitExecutable);
        if (head is null)
            return null;
        IReadOnlyDictionary<string, string> statuses = GetDirtyStatuses(workingDirectory, gitExecutable);
        IReadOnlyDictionary<string, string> blobs = GetBlobIds(workingDirectory, statuses.Keys, gitExecutable);
        return new GitSnapshot(head, blobs, statuses);
    }


    /// <summary>Diffs the working tree against a snapshot taken earlier.</summary>
    /// <remarks>
    /// A path counts when its status or blob id differs, or when <c>HEAD</c> has moved
    /// and the committed diff names it. <c>artifacts/**</c> never counts: gate output
    /// is evidence, not a dispatch change.
    /// </remarks>
    /// <returns>Repository-relative paths; empty when absent.</returns>
    public IReadOnlySet<string> GetChangedPathsSinceSnapshot(
        string workingDirectory,
        GitSnapshot snapshot,
        string gitExecutable = "git")
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        HashSet<string> changed = new(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> statuses = GetDirtyStatuses(workingDirectory, gitExecutable);
        string? head = GetHead(workingDirectory, gitExecutable);
        if (head is null && statuses.Count == 0)
            return changed;

        HashSet<string> candidates = new(snapshot.Statuses.Keys, StringComparer.Ordinal);
        candidates.UnionWith(statuses.Keys);
        IReadOnlyDictionary<string, string> blobs = GetBlobIds(workingDirectory, candidates, gitExecutable);
        foreach (string path in candidates)
        {
            snapshot.Statuses.TryGetValue(path, out string? oldStatus);
            statuses.TryGetValue(path, out string? newStatus);
            snapshot.BlobIds.TryGetValue(path, out string? oldBlob);
            blobs.TryGetValue(path, out string? newBlob);
            if (!StringComparer.Ordinal.Equals(oldStatus, newStatus)
                || !StringComparer.Ordinal.Equals(oldBlob, newBlob))
                changed.Add(path);
        }

        if (head is not null
            && !string.IsNullOrWhiteSpace(snapshot.Head)
            && !head.Equals(snapshot.Head, StringComparison.Ordinal))
        {
            string? diff = RunGit(
                workingDirectory,
                gitExecutable,
                string.Empty,
                "diff",
                "--name-only",
                "-z",
                $"{snapshot.Head}...HEAD");
            if (diff is not null)
                changed.UnionWith(GitDiffParser.SplitNul(diff));
        }

        changed.RemoveWhere(GitDiffParser.IsArtifactsPath);
        return changed;
    }


    private static bool ExistsInRepo(string workingDirectory, string path)
    {
        try
        {
            return File.Exists(Path.Combine(workingDirectory, path));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }


    private static string? RunGit(
        string workingDirectory,
        string gitExecutable,
        string standardInput,
        params string[] args)
    {
        try
        {
            ProcessStartInfo startInfo = new(gitExecutable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);
            ProcessResult result = ProcessRunner.Run(startInfo, standardInput);
            return result.ExitCode == 0 ? result.StandardOutput : null;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is Win32Exception or InvalidOperationException)
        {
            // Outside a repository git exits non-zero (handled above); a missing
            // binary, a dead working directory, or an unreadable pipe lands here.
            // Either way the fact is absent, never a failure.
            return null;
        }
    }


    /// <summary>The largest untracked file read for its lines; a bigger one is a generated or vendored blob.</summary>
    private const long UntrackedFileByteLimit = 1024 * 1024;


    /// <summary>
    /// Reads an untracked file's lines, or returns null when it cannot be measured: over the byte limit,
    /// binary (contains a NUL byte), or unreadable. Never throws.
    /// </summary>
    /// <remarks>
    /// A symbolic link is never followed: its target's content is not the change's content, so a link
    /// reads as unmeasurable, which keeps a lens from skipping it as comment-only. The link check runs
    /// before the size check and the open, because both of those follow a link. The size is checked
    /// before any read so a huge file is never loaded. The read itself is bounded too, because a file
    /// can grow between the size check and the read.
    /// </remarks>
    private static List<string>? ReadUntrackedLines(string fullPath)
    {
        try
        {
            FileInfo info = new(fullPath);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            if (info.Length > UntrackedFileByteLimit)
            {
                return null;
            }

            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using MemoryStream bytes = new();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (bytes.Length + read > UntrackedFileByteLimit)
                {
                    return null;
                }

                bytes.Write(chunk, 0, read);
            }

            if (Array.IndexOf(bytes.GetBuffer(), (byte)0, 0, (int)bytes.Length) >= 0)
            {
                return null;
            }

            bytes.Position = 0;
            List<string> lines = [];
            using StreamReader reader = new(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is OutOfMemoryException)
        {
            return null;
        }
    }
}
