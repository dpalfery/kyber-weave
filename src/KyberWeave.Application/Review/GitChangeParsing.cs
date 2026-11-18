namespace KyberWeave.Core.Review;

/// <summary>The rules for reading what git lists about a changed working tree: which <c>--base</c> values are accepted and how its NUL-separated output becomes paths.</summary>
public static class GitChangeParsing
{
    /// <summary>The ref the working tree is compared with when none is named.</summary>
    public const string DefaultBase = "HEAD";

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

    /// <summary>Splits git's NUL-separated output into its non-empty entries.</summary>
    public static string[] SplitPaths(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The changed and untracked paths as one distinct, ordinal-sorted list.</summary>
    public static IReadOnlyList<string> Combine(IEnumerable<string> changed, IEnumerable<string> untracked) =>
        changed
            .Concat(untracked)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Reads <c>git diff -z --name-status -M</c> output and the untracked paths into one list with the kind of each change.</summary>
    /// <param name="status">The NUL-separated entries of the name-status diff.</param>
    /// <param name="untracked">The untracked paths.</param>
    public static IReadOnlyList<ChangedPath> ParseNameStatus(string[] status, string[] untracked)
    {
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
}
