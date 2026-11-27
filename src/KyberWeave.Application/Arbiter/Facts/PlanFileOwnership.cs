namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The one rule for whether a plan task's listed path owns a file: the path is normalized, and it owns the file when
/// it is the file or a directory the file sits under.
/// </summary>
public static class PlanFileOwnership
{
    /// <summary>The repository-relative spelling of <paramref name="path"/>: forward slashes, no leading <c>./</c>.</summary>
    /// <param name="path">A path written by a plan, a seat or git.</param>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    /// <summary>Whether a task's listed path is <paramref name="path"/>, or a directory it sits under.</summary>
    /// <param name="listed">One path from a task's <c>Files</c> list, or a packet's file.</param>
    /// <param name="path">The repository-relative file being judged.</param>
    public static bool Owns(string listed, string path)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(path);
        string entry = Normalize(listed).TrimEnd('/');
        string file = Normalize(path);
        return entry.Length > 0
               && (string.Equals(entry, file, StringComparison.Ordinal)
                   || file.StartsWith(entry + "/", StringComparison.Ordinal));
    }
}
