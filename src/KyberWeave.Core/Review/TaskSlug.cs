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
    /// The existing folder under <paramref name="scratchpad"/> whose normalised name is the task's, matched without
    /// regard to case; null when none exists.
    /// </summary>
    public static string? FindFolder(string scratchpad, string task)
    {
        string slug = Normalise(task);
        return slug.Length == 0 ? null : Folders(scratchpad).FirstOrDefault(name => Normalise(name) == slug);
    }

    /// <summary>The task folders under <paramref name="scratchpad"/> that hold a <c>review</c> folder.</summary>
    public static IReadOnlyList<string> Folders(string scratchpad) =>
        Directory.Exists(scratchpad)
            ? [.. Directory.EnumerateDirectories(scratchpad)
                .Where(dir => Directory.Exists(Path.Combine(dir, "review")))
                .Select(dir => Path.GetFileName(dir))
                .Order(StringComparer.Ordinal)]
            : [];
}
