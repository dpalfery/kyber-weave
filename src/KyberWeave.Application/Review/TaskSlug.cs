namespace KyberWeave.Core.Review;

/// <summary>Turns whatever task id an agent holds into the folder name the review engine uses.</summary>
public static class TaskSlug
{
    /// <summary>Lower-cases, turns each run of non-alphanumerics into one hyphen and trims hyphens; empty when nothing is left.</summary>
    public static string Normalise(string? id)
    {
        System.Text.StringBuilder slug = new();
        foreach (char c in id ?? string.Empty)
        {
            char lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
                slug.Append(lower);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }

        return slug.ToString().TrimEnd('-');
    }

    /// <summary>
    /// The folder among <paramref name="folders"/> whose normalised name is the task's, matched without regard to case;
    /// null when none is.
    /// </summary>
    /// <param name="folders">The task folder names, from <c>IReviewCycleStore.TaskFolders</c>.</param>
    /// <param name="task">The task id the caller holds.</param>
    public static string? FindFolder(IReadOnlyList<string> folders, string task)
    {
        string slug = Normalise(task);
        return slug.Length == 0 ? null : folders.FirstOrDefault(name => Normalise(name) == slug);
    }
}
