using KyberWeave.Core.Review;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The one spelling of the line a fix round's dispatch uses to name another plan task's files the fix may edit, and
/// the reader that turns those lines back into the paths the dispatch puts in scope.
/// </summary>
public static class AlsoInScopeLines
{
    /// <summary>The text every also-in-scope line starts with, before the task id.</summary>
    public const string Prefix = "Also in scope (task ";

    private const string Separator = "): ";

    /// <summary>The line naming <paramref name="files"/> as also in scope for <paramref name="taskId"/>.</summary>
    /// <param name="taskId">The plan task whose files the fix may edit.</param>
    /// <param name="files">The files of that task, repository-relative.</param>
    public static string Line(string taskId, IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(files);
        return Prefix + taskId + Separator + string.Join(", ", files.Where(IsNameable));
    }

    /// <summary>Whether <paramref name="file"/> can be written on a line without changing the line's grammar.</summary>
    /// <param name="file">A path a finding or a plan lists.</param>
    public static bool IsNameable(string file) =>
        CitedLineCheck.IsRepositoryRelative(file)
        && !file.Contains(',', StringComparison.Ordinal)
        && !file.Any(char.IsControl);

    /// <summary>The files every also-in-scope line of <paramref name="dispatch"/> names, in the order they first appear.</summary>
    /// <param name="dispatch">The dispatch text, routing header block included.</param>
    public static IReadOnlyList<string> Files(string dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        List<string> files = [];
        foreach (AlsoInScopeEntry entry in Entries(dispatch))
        {
            if (!files.Contains(entry.File, StringComparer.Ordinal))
                files.Add(entry.File);
        }

        return files;
    }

    /// <summary>The task id and file every also-in-scope line of <paramref name="dispatch"/> names, in the order they appear.</summary>
    /// <param name="dispatch">The dispatch text, routing header block included.</param>
    public static IReadOnlyList<AlsoInScopeEntry> Entries(string dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        List<AlsoInScopeEntry> entries = [];
        foreach (string raw in dispatch.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (!line.StartsWith(Prefix, StringComparison.Ordinal))
                continue;

            int close = line.IndexOf(Separator, Prefix.Length, StringComparison.Ordinal);
            if (close < 0)
                continue;

            string taskId = line[Prefix.Length..close];
            foreach (string file in line[(close + Separator.Length)..]
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsNameable(file))
                    entries.Add(new AlsoInScopeEntry(taskId, file));
            }
        }

        return entries;
    }
}

/// <summary>One file an also-in-scope line names, and the plan task the line says it belongs to.</summary>
/// <param name="TaskId">The plan task whose file the line names.</param>
/// <param name="File">The file, repository-relative.</param>
public readonly record struct AlsoInScopeEntry(string TaskId, string File);
