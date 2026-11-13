using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>A new-side hunk range from a unified diff.</summary>
/// <param name="Path">Repository-relative path of the file.</param>
/// <param name="StartLine">One-based first new-side line.</param>
/// <param name="LineCount">Number of new-side lines in the hunk.</param>
public sealed record HunkRange(string Path, int StartLine, int LineCount);

/// <summary>One hunk of a change, with the old and new ranges git reports for it.</summary>
/// <remarks>
/// Ranges are the zero-context ones git prints. A zero count marks the side that has no lines:
/// a pure insertion has <c>OldCount</c> 0, a pure deletion has <c>Count</c> 0. A whole new file
/// is one hunk with <c>OldStart</c> and <c>OldCount</c> 0.
/// </remarks>
/// <param name="Path">Repository-relative path of the file, under its new name.</param>
/// <param name="OldStart">One-based first old-side line.</param>
/// <param name="OldCount">Number of old-side lines in the hunk.</param>
/// <param name="Start">One-based first new-side line.</param>
/// <param name="Count">Number of new-side lines in the hunk.</param>
public sealed record ChangedHunk(string Path, int OldStart, int OldCount, int Start, int Count);

/// <summary>A changed hunk together with the content of the lines it adds and removes.</summary>
/// <param name="Hunk">The hunk's ranges.</param>
/// <param name="ChangedLines">The line contents the hunk really changes, without the diff's <c>+</c> or <c>-</c> prefix:
/// added and removed lines, net of any removed line git printed back unchanged.</param>
public sealed record MeasuredHunk(ChangedHunk Hunk, IReadOnlyList<string> ChangedLines);

/// <summary>One measurement of a change against a base: its hunks and the paths renamed without a content change.</summary>
/// <param name="Hunks">Every hunk, ordered by path; the order within one path is git's.</param>
/// <param name="RenameOnlyPaths">New paths of renames at full similarity. They have no hunk.</param>
public sealed record ChangeSet(IReadOnlyList<MeasuredHunk> Hunks, IReadOnlySet<string> RenameOnlyPaths);

/// <summary>The dirty-state snapshot the ledger stores at pre-dispatch.</summary>
/// <param name="Head">The commit the snapshot was taken at.</param>
/// <param name="BlobIds">Blob id per dirty or untracked path.</param>
/// <param name="Statuses">Porcelain status per dirty or untracked path.</param>
public sealed record GitSnapshot(
    string Head,
    IReadOnlyDictionary<string, string> BlobIds,
    IReadOnlyDictionary<string, string> Statuses);

/// <summary>Reads the facts the Arbiter derives itself from git.</summary>
/// <remarks>
/// Every call goes through <see cref="ProcessRunner.Run"/> with argv only: arguments
/// travel in <see cref="ProcessStartInfo.ArgumentList"/>, never a shell string, so a
/// hostile path or base cannot re-enable the shell. Outside a repository, or without
/// git on <c>PATH</c>, every fact is absent and nothing throws — the Arbiter rules on
/// fewer facts rather than failing the dispatch.
/// </remarks>
public static class GitFacts
{
    /// <summary>Resolves the repository root for a working directory.</summary>
    /// <returns>The root, or <c>null</c> when it cannot be determined.</returns>
    public static string? GetRoot(string workingDirectory, string gitExecutable = "git")
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
    public static IReadOnlyList<string> ListWorktrees(string workingDirectory, string gitExecutable = "git")
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

    /// <summary>Reads the current branch name.</summary>
    /// <returns>The branch, or <c>null</c> on a detached head or outside a repository.</returns>
    public static string? GetCurrentBranch(string workingDirectory, string gitExecutable = "git")
    {
        string? output = RunGit(
            workingDirectory, gitExecutable, string.Empty, "branch", "--show-current");
        string branch = output?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(branch) ? null : branch;
    }

    /// <summary>Whether <paramref name="revision" /> resolves in the checkout.</summary>
    /// <returns>False for a bogus ref or outside a repository; never throws.</returns>
    public static bool RefExists(string workingDirectory, string revision, string gitExecutable = "git")
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return false;
        }

        return RunGit(workingDirectory, gitExecutable, string.Empty, "rev-parse", "--verify", "--quiet", revision) is not null;
    }

    /// <summary>Reads the current commit.</summary>
    /// <returns>The full <c>HEAD</c> sha, or <c>null</c> when it cannot be determined.</returns>
    public static string? GetHead(string workingDirectory, string gitExecutable = "git")
    {
        string? output = RunGit(
            workingDirectory, gitExecutable, string.Empty, "rev-parse", "HEAD");
        string head = output?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(head) ? null : head;
    }

    /// <summary>Reads the dirty set: staged, unstaged, and untracked paths.</summary>
    /// <returns>Porcelain status per repository-relative path; empty when absent.</returns>
    public static IReadOnlyDictionary<string, string> GetDirtyStatuses(
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
            : ParsePorcelainZ(status);
    }

    /// <summary>Hashes working-tree files the way the index sees them.</summary>
    /// <returns>Blob id per path that could be hashed; empty when absent.</returns>
    public static IReadOnlyDictionary<string, string> GetBlobIds(
        string workingDirectory,
        IEnumerable<string> paths,
        string gitExecutable = "git")
    {
        ArgumentNullException.ThrowIfNull(paths);

        // hash-object aborts the whole batch on the first unreadable path, so only
        // files that still exist go on stdin; the rest have no blob to record.
        List<string> existing = paths
            .Where(p => IsRepoRelative(p) && ExistsInRepo(workingDirectory, p))
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
    public static IReadOnlySet<string> GetChangedPathsSinceBase(
        string workingDirectory,
        string @base,
        string gitExecutable = "git")
    {
        HashSet<string> changed = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(@base))
        {
            string? diff = RunGit(
                workingDirectory,
                gitExecutable,
                string.Empty,
                "diff",
                "--name-only",
                "-z",
                $"{@base}...HEAD");
            if (diff is not null)
                changed.UnionWith(SplitNul(diff));
        }

        changed.UnionWith(GetDirtyStatuses(workingDirectory, gitExecutable).Keys);
        return changed;
    }

    /// <summary>Reads the new-side hunk ranges of the diff against a base.</summary>
    /// <returns>One range per hunk; empty when absent.</returns>
    public static IReadOnlyList<HunkRange> GetHunkRanges(
        string workingDirectory,
        string @base,
        string gitExecutable = "git")
    {
        if (string.IsNullOrWhiteSpace(@base))
            return [];
        string? diff = RunGit(
            workingDirectory, gitExecutable, string.Empty, "diff", "--unified=0", @base);
        return diff is null ? [] : ParseHunkRanges(diff);
    }

    /// <summary>Reads the changed hunks of the change against a base, committed work and the working tree together.</summary>
    /// <remarks>
    /// Untracked files are part of the change too: each is one whole-file hunk. The list is never
    /// cut short, because a lens that sees part of a change judges a partial change as the whole.
    /// </remarks>
    /// <returns>Every hunk ordered by path; <c>null</c> when the base is unreadable or nothing changed.</returns>
    public static IReadOnlyList<ChangedHunk>? GetChangedHunks(
        string workingDirectory,
        string @base,
        string gitExecutable = "git")
    {
        ChangeSet? change = GetChangeSet(workingDirectory, @base, gitExecutable);
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
    public static ChangeSet? GetChangeSet(
        string workingDirectory,
        string @base,
        string gitExecutable = "git")
    {
        if (string.IsNullOrWhiteSpace(@base))
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
            @base);
        if (diff is null)
        {
            return null;
        }

        (List<MeasuredHunk> hunks, HashSet<string> renameOnly) = ParseChangeSet(diff);

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

        foreach (string path in SplitNul(untracked))
        {
            if (!IsRepoRelative(path))
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
    public static string? GetDiffSummary(
        string workingDirectory,
        string @base,
        IReadOnlyCollection<string> changedPaths,
        string gitExecutable = "git")
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        if (string.IsNullOrWhiteSpace(@base) || changedPaths.Count == 0)
            return null;

        string? mergeBase = RunGit(workingDirectory, gitExecutable, string.Empty, "merge-base", @base, "HEAD")?.Trim();
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

        Dictionary<string, List<string>> headers = ParseHunkHeaders(diff);
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
    public static GitSnapshot? CaptureSnapshot(
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
    public static IReadOnlySet<string> GetChangedPathsSinceSnapshot(
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
                changed.UnionWith(SplitNul(diff));
        }

        changed.RemoveWhere(IsArtifactsPath);
        return changed;
    }

    /// <returns>The child's stdout on exit zero; <c>null</c> when git cannot answer.</returns>
    /// <summary>
    /// A plan path is repository-relative: a rooted path, a parent segment, or an embedded
    /// newline would either escape the repository or add a second path to the
    /// <c>--stdin-paths</c> batch. Those never reach <c>File.Exists</c> or git.
    /// </summary>
    private static bool IsRepoRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\0', StringComparison.Ordinal)
            || path.Contains('\n', StringComparison.Ordinal)
            || path.Contains('\r', StringComparison.Ordinal)
            || Path.IsPathRooted(path))
        {
            return false;
        }

        // Drive-relative (C:foo) is not rooted but still escapes the repository.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return false;
        }

        // Win32 strips a trailing dot or space from a segment, so ".. " resolves to
        // the parent even though it is not the literal "..". Reject both.
        return path.Split('/', '\\').All(segment =>
            segment != ".."
            && !segment.EndsWith('.')
            && !segment.EndsWith(' '));
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
        catch (Exception ex) when (ex is Win32Exception
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            // Outside a repository git exits non-zero (handled above); a missing
            // binary, a dead working directory, or an unreadable pipe lands here.
            // Either way the fact is absent, never a failure.
            return null;
        }
    }

    private static Dictionary<string, string> ParsePorcelainZ(string output)
    {
        Dictionary<string, string> statuses = new(StringComparer.Ordinal);
        // With -z a rename is two NUL-separated tokens: "R  <new>" then "<old>".
        // The status-prefixed token carries the new path, which is what the Arbiter
        // attributes the change to.
        string[] entries = output.Split('\0');
        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            if (entry.Length < 4 || entry[2] != ' ')
                continue;
            string status = entry.Substring(0, 2);
            string path = entry.Substring(3);
            if (string.IsNullOrEmpty(path))
                continue;
            if ((status[0] == 'R' || status[0] == 'C')
                && i + 1 < entries.Length
                && !string.IsNullOrEmpty(entries[i + 1]))
                i++;
            statuses[path] = status;
        }

        return statuses;
    }

    private static List<string> SplitNul(string output)
    {
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static bool IsArtifactsPath(string path)
    {
        return path.Equals("artifacts", StringComparison.Ordinal)
            || path.StartsWith("artifacts/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the hunks and the full-similarity renames out of one zero-context diff.
    /// </summary>
    /// <remarks>
    /// A hunk's body is counted by its header, not recognised by its first characters: a removed
    /// <c>-- x</c> prints as <c>--- x</c>, and an added <c>++ y</c> as <c>+++ y</c>, and either would
    /// otherwise be read as a file header.
    /// </remarks>
    private static (List<MeasuredHunk> Hunks, HashSet<string> RenameOnly) ParseChangeSet(string diff)
    {
        List<PendingHunk> pending = [];
        HashSet<string> renameOnly = new(StringComparer.Ordinal);
        string? oldPath = null;
        string? hunkPath = null;
        bool fullSimilarity = false;
        PendingHunk? current = null;
        int bodyLinesLeft = 0;
        using StringReader reader = new(diff);
        while (reader.ReadLine() is { } line)
        {
            if (bodyLinesLeft > 0)
            {
                if (!line.StartsWith('\\'))
                {
                    bodyLinesLeft--;
                    // A byte-order mark sits on a file's first line only, and git prints it on the removed
                    // copy but not the re-added one. Stripping it lets the two cancel in NetChangedLines.
                    string content = (line.Length > 0 ? line.Substring(1) : string.Empty).TrimStart('﻿');
                    if (line.StartsWith('+'))
                    {
                        current?.Added.Add(content);
                    }
                    else
                    {
                        current?.Removed.Add(content);
                    }
                }

                continue;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                oldPath = null;
                hunkPath = null;
                fullSimilarity = false;
            }
            else if (line.StartsWith("similarity index 100%", StringComparison.Ordinal))
            {
                fullSimilarity = true;
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                // Rename paths carry no a/ or b/ prefix, so they are unquoted but never stripped.
                if (fullSimilarity)
                {
                    renameOnly.Add(Unquote(line.Substring("rename to ".Length)));
                }
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                string side = NormalizeDiffPath(line.Substring(4).Trim());
                oldPath = side.StartsWith("a/", StringComparison.Ordinal) ? side.Substring(2) : side;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                string side = NormalizeDiffPath(line.Substring(4).Trim());
                // A deleted file's hunks are named by the path it had.
                hunkPath = side == "/dev/null" ? oldPath : side;
            }
            else if (hunkPath is not null
                && line.StartsWith("@@ ", StringComparison.Ordinal)
                && TryParseHunkHeader(line, out int oldStart, out int oldCount, out int start, out int count))
            {
                current = new PendingHunk(new ChangedHunk(hunkPath, oldStart, oldCount, start, count));
                pending.Add(current);
                bodyLinesLeft = oldCount + count;
            }
        }

        List<MeasuredHunk> hunks = pending
            .Select(hunk => new MeasuredHunk(hunk.Hunk, NetChangedLines(hunk.Removed, hunk.Added)))
            .ToList();
        return (hunks, renameOnly);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The lines a hunk actually changes: an added line cancels one identical removed line, so a line
    /// git shows as removed and re-added unchanged is not part of the change.
    /// </summary>
    /// <remarks>
    /// Git's diff heuristics often print a one-line insertion as a replacement of its neighbour. Read
    /// literally, that replacement would make a comment-only edit look like a code edit.
    /// </remarks>
    private static List<string> NetChangedLines(List<string> removed, List<string> added)
    {
        List<string> unmatchedRemoved = [.. removed];
        List<string> net = [];
        foreach (string line in added)
        {
            if (!unmatchedRemoved.Remove(line))
            {
                net.Add(line);
            }
        }

        net.AddRange(unmatchedRemoved);
        return net;
    }

    /// <summary>A hunk whose body is still being read: its header and the lines it removes and adds.</summary>
    private sealed class PendingHunk(ChangedHunk hunk)
    {
        public ChangedHunk Hunk { get; } = hunk;

        public List<string> Removed { get; } = [];

        public List<string> Added { get; } = [];
    }

    private static string Unquote(string path) =>
        path.Length >= 2 && path[0] == '"' && path[^1] == '"'
            ? path.Substring(1, path.Length - 2)
            : path;

    /// <summary>Reads both sides of a unified hunk header, <c>@@ -a[,b] +c[,d] @@</c>.</summary>
    /// <remarks>A bare <c>-a</c> or <c>+c</c> side covers exactly one line.</remarks>
    private static bool TryParseHunkHeader(
        string header,
        out int oldStart,
        out int oldCount,
        out int newStart,
        out int newCount)
    {
        oldStart = 0;
        oldCount = 0;
        newStart = 0;
        newCount = 0;
        int plus = header.IndexOf(" +", 3, StringComparison.Ordinal);
        int tail = plus < 0 ? -1 : header.IndexOf(" @@", plus, StringComparison.Ordinal);
        if (!header.StartsWith("@@ -", StringComparison.Ordinal) || plus < 0 || tail < 0)
        {
            return false;
        }

        return TryParseRange(header.AsSpan(4, plus - 4), out oldStart, out oldCount)
            && TryParseRange(header.AsSpan(plus + 2, tail - plus - 2), out newStart, out newCount);
    }

    private static bool TryParseRange(ReadOnlySpan<char> range, out int start, out int count)
    {
        start = 0;
        count = 0;
        int comma = range.IndexOf(',');
        if (comma < 0)
        {
            count = 1;
            return int.TryParse(range, out start) && start >= 0;
        }

        return int.TryParse(range[..comma], out start)
            && int.TryParse(range[(comma + 1)..], out count)
            && start >= 0
            && count >= 0;
    }

    private static List<HunkRange> ParseHunkRanges(string diff)
    {
        List<HunkRange> ranges = [];
        string? current = null;
        using StringReader reader = new(diff);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                current = NormalizeDiffPath(line.Substring(4).Trim());
                if (current == "/dev/null")
                    current = null;
            }
            else if (current is not null && line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (TryParseNewSide(line, out int start, out int count))
                    ranges.Add(new HunkRange(current, start, count));
            }
        }

        return ranges;
    }

    private static Dictionary<string, List<string>> ParseHunkHeaders(string diff)
    {
        Dictionary<string, List<string>> headers = new(StringComparer.Ordinal);
        string? oldPath = null;
        string? path = null;
        int bodyLinesLeft = 0;
        using StringReader reader = new(diff);
        while (reader.ReadLine() is { } line)
        {
            // Counting body lines keeps a removed "-- x" or added "++ y", which print as
            // "--- x" and "+++ y", from being read as a file header.
            if (bodyLinesLeft > 0)
            {
                if (!line.StartsWith('\\'))
                    bodyLinesLeft--;
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                string side = NormalizeDiffPath(line.Substring(4).Trim());
                oldPath = side.StartsWith("a/", StringComparison.Ordinal) ? side.Substring(2) : side;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                string side = NormalizeDiffPath(line.Substring(4).Trim());
                path = side == "/dev/null" ? oldPath : side;
            }
            else if (path is not null
                && line.StartsWith("@@ ", StringComparison.Ordinal)
                && TryParseNewSide(line, out _, out int added)
                && TryParseOldCount(line, out int removed))
            {
                bodyLinesLeft = removed + added;
                if (!headers.TryGetValue(path, out List<string>? pathHeaders))
                {
                    pathHeaders = [];
                    headers[path] = pathHeaders;
                }

                pathHeaders.Add(line);
            }
        }

        return headers;
    }

    private static bool TryParseOldCount(string header, out int count)
    {
        // Header shape: "@@ -a[,b] +c[,d] @@ ...". A bare "-a" covers exactly one line.
        count = 1;
        int plus = header.IndexOf(" +", StringComparison.Ordinal);
        if (plus < 4)
            return false;
        ReadOnlySpan<char> side = header.AsSpan(4, plus - 4);
        int comma = side.IndexOf(',');
        return comma < 0 || int.TryParse(side.Slice(comma + 1), out count);
    }

    private static string NormalizeDiffPath(string path)
    {
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path.Substring(1, path.Length - 2);
        return path.StartsWith("b/", StringComparison.Ordinal) ? path.Substring(2) : path;
    }

    private static bool TryParseNewSide(string header, out int start, out int count)
    {
        start = 0;
        count = 0;
        // Header shape: "@@ -a[,b] +c[,d] @@ ...". Only the new side matters.
        int plus = header.IndexOf(" +", StringComparison.Ordinal);
        int tail = header.IndexOf(" @@", plus, StringComparison.Ordinal);
        if (plus < 0 || tail < 0)
            return false;
        string side = header.Substring(plus + 2, tail - plus - 2);
        int comma = side.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            // A bare "+c" hunk always covers exactly one line.
            if (!int.TryParse(side, out start) || start < 0)
                return false;
            count = 1;
            return true;
        }

        return int.TryParse(side.AsSpan(0, comma), out start)
            && int.TryParse(side.AsSpan(comma + 1), out count)
            && start >= 0
            && count >= 0;
    }
}
