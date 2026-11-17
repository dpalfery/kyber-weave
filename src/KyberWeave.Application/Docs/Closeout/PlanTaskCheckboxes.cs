using System.Text.RegularExpressions;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>
/// Reads the numbered tasks a plan document still lists unchecked, so the closeout gate and the merge gate cannot
/// disagree about whether a plan's work is recorded as done.
/// </summary>
/// <remarks>
/// Only the <c>## Tasks</c> section counts, and only outside a fenced block: a prose checklist elsewhere, or the
/// checkbox grammar shown as an example, is not a task. A plan archived with an unticked task reads as finished work
/// that shipped, which is what happened when a tiered run archived a plan whose second task was never delivered.
/// </remarks>
public static partial class PlanTaskCheckboxes
{
    /// <summary>The ids of the tasks the text still lists unchecked, in the order they appear.</summary>
    /// <param name="markdown">The plan document, whole or with its frontmatter removed.</param>
    public static IReadOnlyList<string> Unchecked(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        List<string> ids = [];
        bool inTasks = false;
        bool inFence = false;
        foreach (string line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal))
            {
                inTasks = line.StartsWith("## ", StringComparison.Ordinal) && IsTasksHeadingLine(line[3..]);
                continue;
            }

            if (inTasks && UncheckedTask().Match(line) is { Success: true } match)
            {
                ids.Add(match.Groups["id"].Value.TrimEnd('.', ':', ')'));
            }
        }

        return ids;
    }

    private static bool IsTasksHeadingLine(string text)
    {
        string normalized = text.Trim().TrimEnd('.', ':').Trim();
        return normalized.Equals("Tasks", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Implementation Tasks", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^[ \t]*-[ \t]+\[ \][ \t]+(?<id>\d+(?:\.\d+)?)\.?(?:[ \t]|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UncheckedTask();
}
