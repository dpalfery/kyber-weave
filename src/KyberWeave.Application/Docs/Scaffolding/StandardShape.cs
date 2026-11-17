namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>
/// Puts a host standard's own sections under the six headings the ontology requires, keeping
/// every authored word.
/// </summary>
/// <remarks>
/// A host that adopted its standard before the six-heading shape arrived wrote sections such as
/// <c>## Stack</c> and <c>## Commands</c>. Reordering those headings is the only way to pass the
/// shape check, and deleting text to get there would discard the host's standard. So each
/// authored section becomes a third-level section under the required heading it best fits, or
/// under <c>Coding standards</c> when no heading fits; its own headings are demoted one level so
/// the hierarchy stays a hierarchy. A standard that already carries the six in order is left
/// byte-for-byte alone.
/// </remarks>
internal static class StandardShape
{
    /// <summary>Words that place an authored heading under one of the six.</summary>
    /// <remarks>
    /// Deliberately ordered as <see cref="KyberWeave.Core.Configuration.DocsLayout.StandardHeadings"/>
    /// is ordered, with a heading's own name first. A heading that matches nothing lands under
    /// <c>Coding standards</c>, which is the index this table assigns to the widest subject.
    /// </remarks>
    private static readonly string[][] FitWords =
    [
        [
            "naming", "name", "names", "layout", "layouts", "placement", "structure",
            "folder", "folders", "project", "projects", "directory", "directories",
            "file", "files", "organization", "organisation", "style", "styles"
        ],
        [
            "coding", "code", "codes", "pattern", "patterns", "error", "errors",
            "exception", "exceptions", "async", "stack", "type", "types", "dependency",
            "dependencies", "resource", "resources", "http", "convention", "conventions",
            "rule", "rules", "hygiene", "configuration", "composition"
        ],
        [
            "test", "tests", "testing", "coverage", "fixture", "fixtures", "spec",
            "specs", "e2e", "gate", "gates", "assertion", "assertions"
        ],
        [
            "command", "commands", "build", "builds", "run", "runs", "lint", "format",
            "script", "scripts", "cli", "tool", "tools", "tooling", "restore", "publish"
        ],
        [
            "security", "secret", "secrets", "auth", "authentication", "authorization",
            "identity", "credential", "credentials", "privacy", "data", "permission",
            "permissions", "privilege", "safety", "compliance", "migration", "migrations",
            "schema"
        ],
        ["checklist", "done", "gap", "gaps", "review", "reviewer", "reviewers", "merge", "completion"]
    ];

    private const int CodingStandardsIndex = 1;

    private static readonly char[] WordSeparators =
        [' ', '\t', '-', '_', '/', ':', ';', ',', '.', '(', ')', '\'', '"'];

    /// <summary>
    /// Reshapes <paramref name="text"/> when its headings are not the required six in order.
    /// </summary>
    /// <param name="text">The standard's current text, frontmatter included.</param>
    /// <param name="required">The headings every standard carries, in order.</param>
    /// <returns>The reshaped text and each move it made, or null when the standard already fits.</returns>
    internal static ReshapeResult? Reshape(string text, IReadOnlyList<string> required)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(required);

        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        IReadOnlyList<Section> sections = Split(text, out List<string> head);

        if (AlreadyFits(sections, required)) return null;

        Section?[] primaries = new Section?[required.Count];
        List<Section>[] buckets = [.. required.Select(_ => new List<Section>())];
        List<(string Heading, string Under)> moves = [];

        foreach (Section section in sections)
        {
            int exact = IndexOf(required, section.Heading);
            if (exact >= 0 && primaries[exact] is null)
            {
                primaries[exact] = section;
                continue;
            }

            int target = Classify(section.Heading, required);
            buckets[target].Add(section);
            moves.Add((section.Heading, required[target]));
        }

        List<string> output = [.. head];
        TrimTrailingBlanks(output);
        if (output.Count > 0) output.Add(string.Empty);

        for (int index = 0; index < required.Count; index++)
        {
            output.Add($"## {required[index]}");
            output.Add(string.Empty);

            if (primaries[index] is { } primary) AppendBody(output, primary.Body);

            foreach (Section moved in buckets[index])
            {
                output.Add($"### {moved.Heading}");
                output.Add(string.Empty);
                AppendBody(output, Demote(moved.Body));
            }
        }

        TrimTrailingBlanks(output);
        return new ReshapeResult(string.Join(newline, output) + newline, moves);
    }

    /// <summary>The text a reshape produced, and the sections it moved.</summary>
    /// <param name="Text">The standard's new text.</param>
    /// <param name="Moves">Each authored heading and the required heading it now sits under.</param>
    internal sealed record ReshapeResult(
        string Text,
        IReadOnlyList<(string Heading, string Under)> Moves);

    private sealed record Section(string Heading, IReadOnlyList<string> Body);

    /// <summary>Splits the standard into the lines before the first <c>##</c> and its sections.</summary>
    private static IReadOnlyList<Section> Split(string text, out List<string> head)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        head = [];
        List<Section> sections = [];
        List<string> body = [];
        string? heading = null;
        bool inFence = false;

        foreach (string line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (heading is not null) sections.Add(new Section(heading, [.. body]));
                heading = line[3..].Trim();
                body.Clear();
                continue;
            }

            if (heading is null) head.Add(line);
            else body.Add(line);
        }

        if (heading is not null) sections.Add(new Section(heading, [.. body]));
        return sections;
    }

    private static bool AlreadyFits(IReadOnlyList<Section> sections, IReadOnlyList<string> required)
    {
        if (sections.Count != required.Count) return false;

        for (int index = 0; index < required.Count; index++)
        {
            if (!string.Equals(sections[index].Heading, required[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static int IndexOf(IReadOnlyList<string> headings, string heading)
    {
        for (int index = 0; index < headings.Count; index++)
        {
            if (string.Equals(headings[index], heading.Trim(), StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    /// <summary>The required heading most of the authored heading's words belong to.</summary>
    private static int Classify(string heading, IReadOnlyList<string> required)
    {
        string[] words = heading
            .ToUpperInvariant()
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);

        int best = -1;
        int bestScore = 0;
        for (int index = 0; index < required.Count && index < FitWords.Length; index++)
        {
            int score = words.Count(word =>
                FitWords[index].Contains(word, StringComparer.OrdinalIgnoreCase));
            if (score <= bestScore) continue;

            best = index;
            bestScore = score;
        }

        return bestScore > 0 && best >= 0 ? best : Math.Min(CodingStandardsIndex, required.Count - 1);
    }

    /// <summary>Appends a body without its surrounding blank lines, then one blank separator.</summary>
    private static void AppendBody(List<string> output, IReadOnlyList<string> body)
    {
        int start = 0;
        while (start < body.Count && string.IsNullOrWhiteSpace(body[start])) start++;

        int end = body.Count - 1;
        while (end >= start && string.IsNullOrWhiteSpace(body[end])) end--;

        if (start > end) return;

        for (int index = start; index <= end; index++) output.Add(body[index]);
        output.Add(string.Empty);
    }

    /// <summary>Demotes a moved section's own headings one level so the six stay the top level.</summary>
    private static IReadOnlyList<string> Demote(IReadOnlyList<string> body)
    {
        List<string> demoted = new(body.Count);
        bool inFence = false;

        foreach (string line in body)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                demoted.Add(line);
                continue;
            }

            int hashes = inFence ? 0 : HeadingLevel(line);
            demoted.Add(hashes is >= 3 and <= 5 ? "#" + line : line);
        }

        return demoted;
    }

    private static int HeadingLevel(string line)
    {
        int hashes = 0;
        while (hashes < line.Length && line[hashes] == '#') hashes++;
        return hashes > 0 && hashes < line.Length && line[hashes] == ' ' ? hashes : 0;
    }

    private static void TrimTrailingBlanks(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
    }
}
