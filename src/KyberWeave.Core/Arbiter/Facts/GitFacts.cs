using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>A new-side hunk range from a unified diff.</summary>
/// <param name="Path">Repository-relative path of the file.</param>
/// <param name="StartLine">One-based first new-side line.</param>
/// <param name="LineCount">Number of new-side lines in the hunk.</param>
public sealed record HunkRange(string Path, int StartLine, int LineCount);

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
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(workingDirectory, p)))
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
