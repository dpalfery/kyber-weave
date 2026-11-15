using System.Text;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>One reference file's part in a brief.</summary>
/// <param name="File">The authored file name from the map.</param>
/// <param name="ReadFrom">The path the text was read from.</param>
/// <param name="WholeFileLeftOut">The condition that left the whole file out, or null when it was read.</param>
/// <param name="LeftOut">Headings left out, with the condition that removed each.</param>
/// <param name="Unmapped">Headings the map does not mention; their text is included.</param>
/// <param name="MissingFromFile">Headings the map names that the file does not have.</param>
/// <param name="Text">The included text, in document order.</param>
/// <param name="FullCharacters">The size of the whole file in characters.</param>
public sealed record BriefPart(
    string File,
    string ReadFrom,
    string? WholeFileLeftOut,
    IReadOnlyList<(string Heading, string Condition)> LeftOut,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> MissingFromFile,
    string Text,
    int FullCharacters);

/// <summary>A brief: the parts that apply to the facts, and what was left out.</summary>
/// <param name="Agent">The agent the brief is for.</param>
/// <param name="Facts">The facts the selection used.</param>
/// <param name="Parts">One part per file in the map, in map order.</param>
/// <param name="UnlistedFiles">Reference files present beside the mapped ones that the map does not name. They are not in the brief; read them whole.</param>
public sealed record BriefResult(string Agent, BriefFacts Facts, IReadOnlyList<BriefPart> Parts, IReadOnlyList<string> UnlistedFiles)
{
    /// <summary>The characters of every mapped reference file, whole.</summary>
    public int FullCharacters => Parts.Sum(part => part.FullCharacters);

    /// <summary>The characters of the text the brief carries.</summary>
    public int BriefCharacters => Parts.Sum(part => part.Text.Length);
}

/// <summary>Selects, from an agent's reference files, the sections that apply to a run's facts.</summary>
public static class BriefAssembler
{
    /// <summary>Builds the brief for <paramref name="agent"/>.</summary>
    /// <param name="map">The map of files and headings to facts.</param>
    /// <param name="agent">The agent whose reference files are read.</param>
    /// <param name="facts">The run's facts; unknown ones keep their sections.</param>
    /// <param name="referencesDirectory">The directory holding that agent's installed reference files.</param>
    /// <exception cref="BriefMapException">The map does not know the agent, or a mapped file is missing; the message names the path.</exception>
    public static BriefResult Assemble(BriefMap map, string agent, BriefFacts facts, string referencesDirectory)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(referencesDirectory);

        IReadOnlyList<BriefFileRule> rules = map.FilesOf(agent)
            ?? throw new BriefMapException(
                $"The brief map has no entry for agent '{agent}'. Agents it describes: {string.Join(", ", map.Agents)}.");

        List<BriefPart> parts = [];
        foreach (BriefFileRule rule in rules)
        {
            string path = ResolveFile(referencesDirectory, rule.File);
            string text = File.ReadAllText(path);
            parts.Add(BuildPart(rule, path, text, facts));
        }

        HashSet<string> mapped = rules.Select(rule => rule.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] unlisted = Directory.EnumerateFiles(referencesDirectory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !mapped.Contains(name) && !mapped.Contains(Path.ChangeExtension(name, ".md")))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new BriefResult(agent, facts, parts, unlisted);
    }

    /// <summary>
    /// Finds a reference file under the name the map gives it, or under the <c>.markdown</c> name the
    /// harnesses that scan their agents directory for <c>*.md</c> install it as.
    /// </summary>
    /// <exception cref="BriefMapException">Neither exists; the message names both paths.</exception>
    public static string ResolveFile(string referencesDirectory, string file)
    {
        string authored = Path.Combine(referencesDirectory, file);
        if (File.Exists(authored))
        {
            return authored;
        }

        string guarded = Path.Combine(
            referencesDirectory,
            file.EndsWith(".md", StringComparison.Ordinal) ? string.Concat(file.AsSpan(0, file.Length - 3), ".markdown") : file);
        if (File.Exists(guarded))
        {
            return guarded;
        }

        throw new BriefMapException(
            $"Reference file '{file}' is named in the brief map but is not installed: neither '{authored}' nor '{guarded}' exists.");
    }

    /// <summary>Selects the sections of one file's <paramref name="text"/> that apply to <paramref name="facts"/>.</summary>
    internal static BriefPart BuildPart(BriefFileRule rule, string readFrom, string text, BriefFacts facts)
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
