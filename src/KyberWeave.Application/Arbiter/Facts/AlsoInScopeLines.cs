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
        return Prefix + taskId + Separator + string.Join(", ", files);
    }

    /// <summary>The files every also-in-scope line of <paramref name="dispatch"/> names, in the order they first appear.</summary>
    /// <param name="dispatch">The dispatch text, routing header block included.</param>
    public static IReadOnlyList<string> Files(string dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        List<string> files = [];
        foreach (string raw in dispatch.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (!line.StartsWith(Prefix, StringComparison.Ordinal))
                continue;

            int close = line.IndexOf(Separator, Prefix.Length, StringComparison.Ordinal);
            if (close < 0)
                continue;

            foreach (string file in line[(close + Separator.Length)..]
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!files.Contains(file, StringComparer.Ordinal))
                    files.Add(file);
            }
        }

        return files;
    }
}
