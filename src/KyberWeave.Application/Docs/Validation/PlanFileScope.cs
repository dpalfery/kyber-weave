using KyberWeave.Core;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Sorts the entries a task's text yields as its files into repository paths, scratch paths and other text. A plan's
/// prose also holds routes, URLs and call expressions in backticks, and a file list that carries them feeds wrong
/// scopes into the in-flight overlap check and the diff attribution.
/// </summary>
/// <remarks>
/// The judgment reads the entry's text and nothing else. The task file is compared with the plan again on every packet,
/// so a rule that asked the working tree whether a path or its folder exists gave a different file list as soon as a
/// worker created that folder, and the run stopped on a task file that no longer matched its unchanged plan.
/// </remarks>
internal static partial class PlanFileScope
{
    /// <summary>
    /// Splits <paramref name="entries"/> into the kept repository paths, the scratch paths and the dropped text. Neither
    /// the scratch paths nor the dropped text belong in a task file.
    /// </summary>
    public static (IReadOnlyList<string> Kept, IReadOnlyList<string> Scratch, IReadOnlyList<string> Dropped) Filter(IReadOnlyList<string> entries)
    {
        List<string> kept = [];
        List<string> scratch = [];
        List<string> dropped = [];
        foreach (string entry in entries)
        {
            string? relative = AsRelativePath(entry);
            if (relative is null)
                dropped.Add(entry);
            else if (IsScratch(relative))
                scratch.Add(entry); // reported to the author, so in the author's own text
            else
                kept.Add(relative);
        }

        return (kept, scratch, dropped);
    }

    /// <summary>The entry as a repository-relative path with forward slashes, or null when its text cannot be one.</summary>
    private static string? AsRelativePath(string entry)
    {
        string text = entry.Trim();
        if (text.Length == 0 || text.Contains("://", StringComparison.Ordinal) || text.Contains('(', StringComparison.Ordinal)
            || text.Contains(')', StringComparison.Ordinal) || text.Any(char.IsWhiteSpace) || SchemeRegex().IsMatch(text)
            || text.Any(character => character is '\'' or '"' or '`' or '<' or '>' or '{' or '}' or '|' or '$'))
        {
            return null;
        }

        // A leading slash is a route such as /api/drivers, and a pattern with no literal folder before it names no place.
        string relative = text.TrimEnd('/').Replace('\\', '/');
        while (relative.StartsWith("./", StringComparison.Ordinal))
        {
            relative = relative[2..];
        }

        // A path that climbs out of the repository or starts at a drive is not a place inside it.
        string[] segments = relative.Split('/');
        if (relative.Length == 0 || relative.StartsWith('/') || DriveRegex().IsMatch(relative)
            || segments.Any(segment => segment is ".." or "."))
        {
            return null;
        }

        return segments[0].IndexOfAny(['*', '?']) >= 0 ? null : relative;
    }

    // Case is ignored so the answer is the same on a file system that folds case and on one that does not.
    private static bool IsScratch(string relative) =>
        relative.Equals(ScratchLayout.Root, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(ScratchLayout.Root + "/", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:(?!\\|/)", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DriveRegex();
}
