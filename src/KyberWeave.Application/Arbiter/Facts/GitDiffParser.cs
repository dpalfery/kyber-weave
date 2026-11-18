using System.Text;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>Parses git's output into the Arbiter's change facts. Holds no I/O: the adapter that runs git feeds it text.</summary>
internal static class GitDiffParser
{

    /// <returns>The child's stdout on exit zero; <c>null</c> when git cannot answer.</returns>
    /// <summary>
    /// A plan path is repository-relative: a rooted path, a parent segment, or an embedded
    /// newline would either escape the repository or add a second path to the
    /// <c>--stdin-paths</c> batch. Those never reach <c>File.Exists</c> or git.
    /// </summary>
    internal static bool IsRepoRelative(string path)
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


    internal static Dictionary<string, string> ParsePorcelainZ(string output)
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


    internal static List<string> SplitNul(string output)
    {
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }


    internal static bool IsArtifactsPath(string path)
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
    internal static (List<MeasuredHunk> Hunks, HashSet<string> RenameOnly) ParseChangeSet(string diff)
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


    /// <summary>
    /// The lines a hunk actually changes: an added line cancels one identical removed line, so a line
    /// git shows as removed and re-added unchanged is not part of the change.
    /// </summary>
    /// <remarks>
    /// Git's diff heuristics often print a one-line insertion as a replacement of its neighbour. Read
    /// literally, that replacement would make a comment-only edit look like a code edit.
    /// </remarks>
    internal static List<string> NetChangedLines(List<string> removed, List<string> added)
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


    internal static string Unquote(string path) =>
        path.Length >= 2 && path[0] == '"' && path[^1] == '"'
            ? path.Substring(1, path.Length - 2)
            : path;


    /// <summary>Reads both sides of a unified hunk header, <c>@@ -a[,b] +c[,d] @@</c>.</summary>
    /// <remarks>A bare <c>-a</c> or <c>+c</c> side covers exactly one line.</remarks>
    internal static bool TryParseHunkHeader(
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


    internal static bool TryParseRange(ReadOnlySpan<char> range, out int start, out int count)
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


    internal static List<HunkRange> ParseHunkRanges(string diff)
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


    internal static Dictionary<string, List<string>> ParseHunkHeaders(string diff)
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


    internal static bool TryParseOldCount(string header, out int count)
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


    internal static string NormalizeDiffPath(string path)
    {
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path.Substring(1, path.Length - 2);
        return path.StartsWith("b/", StringComparison.Ordinal) ? path.Substring(2) : path;
    }


    internal static bool TryParseNewSide(string header, out int start, out int count)
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
