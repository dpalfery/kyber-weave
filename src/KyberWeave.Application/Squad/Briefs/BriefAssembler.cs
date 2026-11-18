using System.Text;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>Selects, from an agent's reference files, the sections that apply to a run's facts.</summary>
public static class BriefAssembler
{
    /// <summary>Gets the name a harness that scans its agents directory for <c>*.md</c> installs a reference file under.</summary>
    /// <param name="file">The name the brief map gives the file.</param>
    /// <returns>The <c>.markdown</c> spelling of an <c>.md</c> name, or the name unchanged.</returns>
    public static string GuardedName(string file) =>
        file.EndsWith(".md", StringComparison.Ordinal) ? string.Concat(file.AsSpan(0, file.Length - 3), ".markdown") : file;

    /// <summary>Selects the sections of one file's <paramref name="text"/> that apply to <paramref name="facts"/>.</summary>
    public static BriefPart BuildPart(BriefFileRule rule, string readFrom, string text, BriefFacts facts)
    {
        if (!rule.When.Holds(facts))
        {
            return new BriefPart(rule.File, readFrom, rule.When.Text, [], [], [], string.Empty, text.Length);
        }

        IReadOnlyList<MarkdownSection> sections = MarkdownSections.Split(text);
        Dictionary<string, (string Heading, BriefCondition Condition)> byHeading = new(StringComparer.Ordinal);
        foreach ((string heading, BriefCondition condition) in rule.Sections)
        {
            byHeading[MarkdownSections.Normalize(heading)] = (heading, condition);
        }

        bool[] kept = new bool[sections.Count];
        List<(string, string)> leftOut = [];
        List<string> unmapped = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        StringBuilder included = new();

        for (int index = 0; index < sections.Count; index++)
        {
            MarkdownSection section = sections[index];
            string? key = section.Heading is null ? null : MarkdownSections.Normalize(section.Heading);
            BriefCondition? condition = null;
            if (key is not null && byHeading.TryGetValue(key, out (string Heading, BriefCondition Condition) entry))
            {
                condition = entry.Condition;
                seen.Add(key);
            }

            if (section.Parent >= 0 && !kept[section.Parent])
            {
                continue;
            }

            if (section.Heading is not null)
            {
                if (condition is null)
                {
                    unmapped.Add(section.Heading);
                }
                else if (!condition.Holds(facts))
                {
                    leftOut.Add((section.Heading, condition.Text));
                    continue;
                }
            }

            kept[index] = true;
            included.Append(section.Text);
        }

        string[] missing = byHeading
            .Where(pair => !seen.Contains(pair.Key))
            .Select(pair => pair.Value.Heading)
            .ToArray();

        return new BriefPart(rule.File, readFrom, null, leftOut, unmapped, missing, included.ToString().TrimEnd() + "\n", text.Length);
    }
}
